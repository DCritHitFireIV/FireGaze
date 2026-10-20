using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace FireGaze.PortraitPreview;

/// <summary>Detached identity plus a hash of the current SRV/crop/layer generation, excluding light RGB.</summary>
internal sealed record PortraitLightingFitCaptureFrame(PortraitCaptureSample Sample,PortraitCaptureSurface Surface)
{
 public int Width=>Surface.Width;
 public int Height=>Surface.Height;
 public bool IsValid=>Sample is{IsValid:true,Scope:PortraitImageScope.Portrait}&&Sample.Viewport.IsValid&&Surface is{IsValid:true};
}

internal interface IPortraitLightingFitReadback
{
 // Submission is on Framework. The bridge retains texture until this physical task finishes.
 Task<IReadOnlyList<PortraitLightRgba>> ReadAsync(IDalamudTextureWrap texture);
}

/// <summary>
/// A single physical capture spans two stable rendered frames, Framework acquisition, Draw composition,
/// fresh Framework validation and asynchronous CPU readback. Close invalidates publication without
/// cancelling public texture work or releasing its independent references early.
/// </summary>
internal sealed class PortraitLightingFitCaptureBridge(IPortraitLightingFitCaptureApi api,IPortraitLightingFitReadback readback):IDisposable
{
 private readonly object sync=new();
 private readonly PortraitCaptureRetirement physical=new();
 private Work? work;
 private long drawSerial;
 private bool disposed;
 public bool IsBusy{get{lock(sync)return work is not null;}}
 internal bool IsPhysicallyIdle=>physical.Pending==0&&physical.CleanupFailures==0;
 public int CleanupFailures=>physical.CleanupFailures;
 public string? LastCleanupFailureType=>physical.LastCleanupFailureType;

 public bool TryReadFrame(PortraitCaptureViewport viewport,out PortraitLightingFitCaptureFrame? frame)
 {
  frame=null;
  lock(sync)
  {
   if(disposed||!viewport.IsValid||!physical.TryEnter(out var lifetime))return false;
   try
   {
    if(!api.IsFrameworkThread||!api.TrySample(PortraitImageScope.Portrait,viewport,out var sample)
     ||sample is not{IsValid:true,Scope:PortraitImageScope.Portrait}||sample.Viewport!=viewport
     ||!api.TryDescribePortrait(sample,out var surface)||surface is not{IsValid:true}
     ||!api.TrySample(PortraitImageScope.Portrait,viewport,out var after)||after!=sample||disposed)return false;
    frame=new(sample,surface);return true;
   }
   catch(Exception failure){physical.ObserveFailure(failure);return false;}
   finally{lifetime.Dispose();}
  }
 }

 public Task<IReadOnlyList<PortraitLightRgba>> CaptureAsync(PortraitLightingFitCaptureFrame expected)
 {
  lock(sync)
  {
   if(disposed||work is not null||!api.IsFrameworkThread||expected is not{IsValid:true}||!Current(expected)
    ||!physical.TryEnter(out var lifetime))
    return Task.FromException<IReadOnlyList<PortraitLightRgba>>(Failure());
   work=new(expected,drawSerial,lifetime);
   return work.Publication.Task;
  }
 }

 private bool Current(PortraitLightingFitCaptureFrame expected)
  =>TryReadFrame(expected.Sample.Viewport,out var fresh)&&fresh==expected;

 public void Update()
 {
  if(!api.IsFrameworkThread)return;
  lock(sync)
  {
   if(disposed||work is not{} active||active.Starting)return;
   try{if(!Current(active.Expected))active.Invalid=true;}
   catch(Exception){active.Invalid=true;}
   if(disposed||!ReferenceEquals(work,active))return;
   if(active.Phase==Phase.Settling)
   {
    if(active.Invalid){Finish(active);return;}
    if(drawSerial-active.StartedDraw<2)return;
    active.Phase=Phase.Copying;active.Starting=true;
    try{active.Copy=api.CaptureAsync(active.Expected.Sample,active.Expected.Surface);}
    catch(Exception failure){physical.ObserveFailure(failure);Finish(active);}
    finally{active.Starting=false;}
    if(disposed)Detach(active);
    return;
   }
   if(active.Phase==Phase.Copying)
   {
    if(active.Copy?.IsCompleted!=true)return;
    try{active.Layers=active.Copy.GetAwaiter().GetResult();}
    catch(Exception failure){physical.ObserveFailure(failure);active.Invalid=true;}
    active.CopyConsumed=true;
    if(active.Invalid||active.Layers is null||active.Layers.Width!=active.Expected.Width
     ||active.Layers.Height!=active.Expected.Height||active.Layers.Layers.Count==0){Finish(active);return;}
    active.Phase=Phase.WaitingDraw;return;
   }
   if(active.Phase==Phase.WaitingDraw)
   {
    if(active.Invalid)Finish(active);
    return;
   }
   if(active.Phase==Phase.Composed)
   {
    if(active.Invalid||active.Candidate is null){Finish(active);return;}
    var owned=active.Candidate;active.Candidate=null;
    active.Phase=Phase.Reading;active.Starting=true;
    active.Read=ReadAndRelease(owned);
    active.Starting=false;
    if(disposed)Detach(active);
    return;
   }
   if(active.Read?.IsCompleted!=true)return;
   IReadOnlyList<PortraitLightRgba>? pixels=null;
   try{pixels=active.Read.GetAwaiter().GetResult();}
   catch(Exception failure){physical.ObserveFailure(failure);active.Invalid=true;}
   if(active.Invalid||pixels is null||pixels.Count!=(long)active.Expected.Width*active.Expected.Height
    ||pixels.Any(pixel=>!pixel.Rgb.IsValid||pixel.A is <0 or >255)){Finish(active);return;}
   active.Publication.TrySetResult(Array.AsReadOnly(pixels.ToArray()));
   if(ReferenceEquals(work,active))work=null;
   active.Lifetime.Dispose();
  }
 }

 private async Task<IReadOnlyList<PortraitLightRgba>> ReadAndRelease(IDalamudTextureWrap texture)
 {
  // A public readback provider may do staging/Present work before it returns its Task. Keep that
  // synchronous prefix outside Framework: Update only transfers an independently owned wrapper.
  try{return await Task.Run(()=>readback.ReadAsync(texture)).ConfigureAwait(false);}
  finally{physical.Release(texture);}
 }

 /// <summary>Call exactly once per UiBuilder.Draw. No native state is read here.</summary>
 public void Draw()
 {
  lock(sync)
  {
   if(disposed)return;
   drawSerial++;
   if(work is not{Phase:Phase.WaitingDraw,Layers:{} layers,Invalid:false} active)return;
   active.Layers=null;active.Starting=true;
   IDalamudTextureWrap? composed=null;
   try
   {
    composed=api.Compose(layers);
    if(composed.Width!=active.Expected.Width||composed.Height!=active.Expected.Height)active.Invalid=true;
    if(!disposed&&!active.Invalid&&ReferenceEquals(work,active))
    {active.Candidate=composed;composed=null;active.Phase=Phase.Composed;}
   }
   catch(Exception failure){physical.ObserveFailure(failure);active.Invalid=true;}
   finally{physical.Release(composed);physical.Release(layers);active.Starting=false;}
   if(disposed||active.Invalid)Finish(active);
  }
 }

 public void Close()
 {
  lock(sync)
  {
   if(work is not{} active)return;
   active.Invalid=true;
   if(!active.Starting&&active.Phase is Phase.Settling or Phase.WaitingDraw or Phase.Composed)Finish(active);
  }
 }

 private static InvalidOperationException Failure()=>new("当前肖像图像已变化或尚未准备好，请重试。");
 private void Finish(Work active)
 {
  physical.Release(active.Layers);active.Layers=null;
  physical.Release(active.Candidate);active.Candidate=null;
  active.Publication.TrySetException(Failure());
  if(ReferenceEquals(work,active))work=null;
  active.Lifetime.Dispose();
 }
 public Task DrainPhysicalAsync()=>physical.Seal();

 public void Dispose()
 {
  lock(sync)
  {
   if(disposed)return;disposed=true;
   if(work is not{} active)return;
   active.Invalid=true;
   if(!active.Starting)Detach(active);
  }
 }

 private void Detach(Work active)
 {
  if(active.Detached)return;active.Detached=true;
  physical.Release(active.Layers);active.Layers=null;
  physical.Release(active.Candidate);active.Candidate=null;
  if(active.Phase==Phase.Copying&&active.Copy is{} copy&&!active.CopyConsumed)
  {
   active.CopyConsumed=true;
   _=copy.ContinueWith(task=>
   {
    try{if(task.IsCompletedSuccessfully)physical.Release(task.Result);else physical.ObserveFailure(task.Exception);active.Publication.TrySetException(Failure());}
    finally{active.Lifetime.Dispose();}
   },CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
  }
  else if(active.Phase==Phase.Reading&&active.Read is{} read)
   _=read.ContinueWith(task=>{try{physical.ObserveFailure(task.Exception);active.Publication.TrySetException(Failure());}finally{active.Lifetime.Dispose();}},
    CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
  else{active.Publication.TrySetException(Failure());active.Lifetime.Dispose();}
  if(ReferenceEquals(work,active))work=null;
 }

 private enum Phase{Settling,Copying,WaitingDraw,Composed,Reading}
 private sealed class Work(PortraitLightingFitCaptureFrame expected,long startedDraw,IDisposable lifetime)
 {
  public IDisposable Lifetime{get;}=lifetime;
  public PortraitLightingFitCaptureFrame Expected{get;}=expected;
  public long StartedDraw{get;}=startedDraw;
  public Phase Phase;
  public bool Invalid,Starting,CopyConsumed,Detached;
  public Task<PortraitCaptureLayers?>? Copy;
  public PortraitCaptureLayers? Layers;
  public IDalamudTextureWrap? Candidate;
  public Task<IReadOnlyList<PortraitLightRgba>>? Read;
  public TaskCompletionSource<IReadOnlyList<PortraitLightRgba>> Publication{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
 }
}

/// <summary>Public readback only. Async continuations touch owned texture/CPU data, never the editor.</summary>
internal sealed class PortraitLightingFitTextureReadback(ITextureReadbackProvider readback):IPortraitLightingFitReadback
{
 public async Task<IReadOnlyList<PortraitLightRgba>> ReadAsync(IDalamudTextureWrap texture)
 {
  var raw=await readback.GetRawImageAsync(texture,new TextureModificationArgs{Uv1=Vector2.One},
   leaveWrapOpen:true,cancellationToken:CancellationToken.None).ConfigureAwait(false);
  return await Task.Run(()=>Decode(raw.RawData,raw.Specification.Width,raw.Specification.Height,
   raw.Specification.Pitch,raw.Specification.DxgiFormat)).ConfigureAwait(false);
 }
 internal static IReadOnlyList<PortraitLightRgba> Decode(byte[] bytes,int width,int height,int pitch,int format)
 {
  if(width<=0||height<=0||(long)width*height>PortraitCaptureGeometry.MaximumPixels||pitch<(long)width*4
   ||(long)pitch*height>bytes.Length||format is not(28 or 29 or 87 or 91))throw new InvalidOperationException("Unsupported capture pixels.");
  var pixels=new PortraitLightRgba[width*height];var bgra=format is 87 or 91;
  for(var y=0;y<height;y++)for(var x=0;x<width;x++)
  {
   var at=y*pitch+x*4;
   pixels[y*width+x]=new(bytes[at+(bgra?2:0)],bytes[at+1],bytes[at+(bgra?0:2)],bytes[at+3]);
  }
  return Array.AsReadOnly(pixels);
 }
}
