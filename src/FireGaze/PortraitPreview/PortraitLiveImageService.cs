using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitLiveCaptureDiagnostics(bool busy,bool hasImage,string? error,
 string phase,int width,int height,string stateKey,int physicalPending,int cleanupFailures,string? cleanupFailureType);

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
 private readonly PortraitCaptureRetirement physical=new();
 private (PortraitImageScope Scope,PortraitCaptureViewport Viewport)? request;
 private Work? work;
 private PortraitCapturedImage? current;
 private bool disposed;
 private string? error;
 public PortraitLiveImageService(IPortraitLiveImageApi api,Func<DateTimeOffset>? clock=null)
 {this.api=api;gate=new(clock??(()=>DateTimeOffset.UtcNow));}
 public string? Error{get{lock(sync)return error;}}
 public bool IsBusy{get{lock(sync)return work is not null;}}
 public PortraitLiveCaptureDiagnostics Diagnostics
 {
  get
  {
   lock(sync)
   {
    var phase=disposed?"disposed":work is {Candidate:not null}?"awaiting-publication":
     work is {Consumed:true,Layers:not null}?"awaiting-composition":work is not null?"copying":
     current is not null?"published":request is null?"closed":"settling";
    return new(work is not null,current is not null,error,phase,current?.Width??0,current?.Height??0,current?.Sample.StateKey??"",
     physical.Pending,physical.CleanupFailures,physical.LastCleanupFailureType);
   }
  }
 }
 public void Request(PortraitImageScope scope,PortraitCaptureViewport viewport)
 {
  if(scope is not (PortraitImageScope.Portrait or PortraitImageScope.VisibleAdventurerPlate))throw new ArgumentOutOfRangeException(nameof(scope));
  lock(sync){if(disposed||physical.CleanupFailures!=0)return;request=(scope,viewport);gate.Reset();ClearCurrent();error=null;}
 }
 public void Close()
 {lock(sync){request=null;gate.Reset();ClearCurrent();error=null;}}
 public PortraitCapturedImage? AcquireCurrent()
 {
  lock(sync)
  {
   if(disposed||current is not{} image||!physical.TryEnter(out var lifetime))return null;
   IDalamudTextureWrap? shared=null;
   try
   {
    // Sharing has an external synchronous prefix. Own it before close can reenter,
    // then transfer that same lease to the consumer's independent image wrapper.
    shared=image.Texture.CreateWrapSharingLowLevelResource();
    if(disposed||!ReferenceEquals(current,image)||physical.CleanupFailures!=0)return null;
    var result=new PortraitCapturedImage(shared,image.Sample,physical,lifetime);
    shared=null;lifetime=null!;return result;
   }
   catch(Exception failure){physical.ObserveFailure(failure);return null;}
   finally{physical.Release(shared);lifetime?.Dispose();}
  }
 }
 public void Update(bool allowCapture=true)
 {
  if(!api.IsFrameworkThread)return;
  lock(sync)
  {
   if(disposed)return;
   // Isolation revokes even a settling request with no physical task. Keep
   // pending copies owned until completion, but never inspect a native source
   // or revive a previous request when live capture is later re-enabled.
   if(!allowCapture||physical.CleanupFailures!=0){request=null;gate.Reset();ClearCurrent();error=null;}
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
     catch(Exception failure){physical.ObserveFailure(failure);error="暂时无法读取图像，请重试。";}
    }
    if(!gate.CanAccept(active.Ticket))
    {
     physical.Release(active.Layers);active.Layers=null;physical.Release(active.Candidate);active.Candidate=null;
     if(active.Consumed){work=null;active.Lifetime.Dispose();}
    }
    else if(active.Candidate is {} candidate)
    {
     current=new(candidate,active.Ticket.Sample);active.Candidate=null;work=null;active.Lifetime.Dispose();error=null;
    }
    else if(active.Consumed&&active.Layers is null){work=null;active.Lifetime.Dispose();}
   }
   if(work is not null||request is null||sample is null)return;
   if(gate.Begin() is not {} ticket)return;
   var started=new Work(ticket,physical.Enter()){Starting=true};work=started;
   try{started.Copy=api.CaptureAsync(ticket.Sample);}
   catch(Exception failure){physical.ObserveFailure(failure);error="暂时无法读取图像，请重试。";started.Consumed=true;work=null;started.Lifetime.Dispose();return;}
   finally{started.Starting=false;}
   if(disposed)ObserveDetached(started);
  }
 }
 public void Draw(bool inputHeld=false)
 {
  lock(sync)
  {
   if(disposed)return;
   // Tools select regions on the independently owned published image. Held input delays new
   // captures, while Framework's full sample comparison still retires any genuinely stale image.
   if(inputHeld){gate.Reset();return;}
   if(work is not {Consumed:true,Layers:{} layers} active||!gate.CanAccept(active.Ticket))return;
   active.Layers=null;active.Starting=true;
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
   catch(Exception failure){physical.ObserveFailure(failure);if(!disposed)error="暂时无法合成图像，请重试。";}
   finally{physical.Release(composed);physical.Release(layers);active.Starting=false;if(disposed)ObserveDetached(active);}
  }
 }
 private void ClearCurrent(){physical.Release(current);current=null;}
 public void Dispose()
 {
  lock(sync)
  {
   if(disposed)return;disposed=true;request=null;gate.Reset();ClearCurrent();
   if(work is {} active)
   {
    physical.Release(active.Layers);active.Layers=null;physical.Release(active.Candidate);active.Candidate=null;
    if(!active.Starting)ObserveDetached(active);work=null;
   }
  }
 }
 public Task DrainPhysicalAsync()=>physical.Seal();
 private void ObserveDetached(Work active)
 {
  if(active.Consumed||active.Copy is null){active.Lifetime.Dispose();return;}
  active.Consumed=true;
  _=active.Copy.ContinueWith(task=>
  {try{if(task.IsCompletedSuccessfully)physical.Release(task.Result);else physical.ObserveFailure(task.Exception);}finally{active.Lifetime.Dispose();}},CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
 }
 private sealed class Work(PortraitLiveCaptureTicket ticket,IDisposable lifetime)
 {
  public IDisposable Lifetime{get;}=lifetime;
  public PortraitLiveCaptureTicket Ticket{get;}=ticket;
  public Task<PortraitCaptureLayers?>? Copy;
  public bool Consumed,Starting;
  public PortraitCaptureLayers? Layers;
  public IDalamudTextureWrap? Candidate;
 }
}
