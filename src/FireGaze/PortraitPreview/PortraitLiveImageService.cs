using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

/// <summary>
/// One physical copy at a time. Framework validates immutable state; Draw composes owned resources;
/// another Framework pass validates before publication. No native pointer is used after an await.
/// Closing invalidates publication, not the public viewport task: canceling that task before its
/// scheduled tick can leave its FirstUpdateTask unfinished in the supported Dalamud build.
/// </summary>
internal sealed class PortraitLiveImageService:IDisposable
{
 private readonly object sync=new();
 private readonly IPortraitLiveImageApi api;
 private readonly PortraitCaptureGate gate;
 private (PortraitImageScope Scope,PortraitCaptureViewport Viewport)? request;
 private Work? work;
 private PortraitCapturedImage? current;
 private bool disposed;
 private string? error;
 public PortraitLiveImageService(IPortraitLiveImageApi api,Func<DateTimeOffset>? clock=null)
 {this.api=api;gate=new(clock??(()=>DateTimeOffset.UtcNow));}
 public string? Error{get{lock(sync)return error;}}
 public bool IsBusy{get{lock(sync)return work is not null;}}
 public void Request(PortraitImageScope scope,PortraitCaptureViewport viewport)
 {
  if(scope is not (PortraitImageScope.Portrait or PortraitImageScope.VisibleAdventurerPlate))throw new ArgumentOutOfRangeException(nameof(scope));
  lock(sync){if(disposed)return;request=(scope,viewport);gate.Reset();ClearCurrent();error=null;}
 }
 public void Close()
 {lock(sync){request=null;gate.Reset();ClearCurrent();error=null;}}
 public PortraitCapturedImage? AcquireCurrent()
 {lock(sync)return !disposed&&current is {} image?new(image.Texture.CreateWrapSharingLowLevelResource(),image.Sample):null;}
 public void Update()
 {
  if(!api.IsFrameworkThread)return;
  lock(sync)
  {
   if(disposed)return;
   PortraitCaptureSample? sample=null;
   var requested=request;
   if(requested is {} desired)
   {
    try{if(!api.TrySample(desired.Scope,desired.Viewport,out sample)||sample?.IsValid!=true)sample=null;}
    catch(Exception){sample=null;error="当前图像尚未准备好，请稍后重试。";}
   }
   // A native lookup may reenter Close/Request/Dispose.
   if(disposed)return;
   if(requested!=request)sample=null;
   gate.Observe(sample);
   if(current is {} previous&&previous.Sample!=sample)ClearCurrent();
   if(work is {} active)
   {
    if(!active.Consumed&&active.Copy?.IsCompleted==true)
    {
     active.Consumed=true;
     try{active.Layers=active.Copy.GetAwaiter().GetResult();}
     catch(Exception){error="暂时无法读取图像，请重试。";}
    }
    if(!gate.CanAccept(active.Ticket))
    {
     active.Layers?.Dispose();active.Layers=null;active.Candidate?.Dispose();active.Candidate=null;
     if(active.Consumed)work=null;
    }
    else if(active.Candidate is {} candidate)
    {
     current=new(candidate,active.Ticket.Sample);active.Candidate=null;work=null;error=null;
    }
    else if(active.Consumed&&active.Layers is null)work=null;
   }
   if(work is not null||request is null||sample is null)return;
   if(gate.Begin() is not {} ticket)return;
   var started=new Work(ticket);work=started;
   try{started.Copy=api.CaptureAsync(ticket.Sample);}
   catch(Exception){error="暂时无法读取图像，请重试。";started.Consumed=true;work=null;return;}
   if(disposed)ObserveDetached(started);
  }
 }
 public void Draw(bool inputHeld=false)
 {
  lock(sync)
  {
   if(disposed)return;
   if(inputHeld){gate.Reset();ClearCurrent();return;}
   if(work is not {Consumed:true,Layers:{} layers} active||!gate.CanAccept(active.Ticket))return;
   active.Layers=null;
   IDalamudTextureWrap? composed=null;
   try
   {
    if(layers.Width<=0||layers.Height<=0||(long)layers.Width*layers.Height>PortraitCaptureGeometry.MaximumPixels||layers.Layers.Count==0)
     throw new InvalidOperationException("Invalid capture dimensions.");
    composed=api.Compose(layers);
    if(composed.Width!=layers.Width||composed.Height!=layers.Height)throw new InvalidOperationException("Unexpected composition dimensions.");
    if(!disposed&&ReferenceEquals(work,active)&&gate.CanAccept(active.Ticket))
    {active.Candidate=composed;composed=null;}
   }
   catch(Exception){if(!disposed)error="暂时无法合成图像，请重试。";}
   finally{composed?.Dispose();layers.Dispose();}
  }
 }
 private void ClearCurrent(){current?.Dispose();current=null;}
 public void Dispose()
 {
  lock(sync)
  {
   if(disposed)return;disposed=true;request=null;gate.Reset();ClearCurrent();
   if(work is {} active)
   {
    active.Layers?.Dispose();active.Layers=null;active.Candidate?.Dispose();active.Candidate=null;
    ObserveDetached(active);work=null;
   }
  }
 }
 private static void ObserveDetached(Work active)
 {
  if(active.Consumed||active.Copy is null)return;
  active.Consumed=true;
  _=active.Copy.ContinueWith(task=>
  {if(task.IsCompletedSuccessfully)task.Result?.Dispose();else _=task.Exception;},CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
 }
 private sealed class Work(PortraitLiveCaptureTicket ticket)
 {
  public PortraitLiveCaptureTicket Ticket{get;}=ticket;
  public Task<PortraitCaptureLayers?>? Copy;
  public bool Consumed;
  public PortraitCaptureLayers? Layers;
  public IDalamudTextureWrap? Candidate;
 }
}
