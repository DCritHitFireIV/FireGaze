#if HAS_CAPTURE
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.PortraitPreview;

internal static class CaptureLifecycleTests
{
 public static async Task<int> Run()
 {
  var checks=0;
  void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
  var now=DateTimeOffset.Parse("2026-10-19T00:00:00Z");
  var api=new FakeCaptureApi();
  using(var service=new PortraitLiveImageService(api,()=>now))
  {
   Check(service.Diagnostics is { busy:false,hasImage:false,phase:"closed" },"unused capture diagnostics report no physical task or image");
   service.Request(PortraitImageScope.Portrait,api.Sample.Viewport);
   api.Framework=false;service.Update();Check(api.Samples==0,"non-Framework update never samples native state");
   api.Framework=true;service.Update();Check(api.Captures==0,"new state waits for settle period");
   now=now.AddMilliseconds(200);service.Update();Check(api.Captures==1,"settled source starts exactly one asynchronous capture");
   Check(service.Diagnostics is { busy:true,hasImage:false,phase:"copying" },"pending physical copy is distinguishable from a published image");
   service.Update();service.Draw();Check(api.Captures==1&&api.Composes==0,"pending copy is neither duplicated nor synchronously waited");
   var charTexture=new CountingCaptureTexture();api.Complete(new(576,960,[new(charTexture)]));
   service.Draw();Check(api.Composes==0,"copy completion requires fresh Framework validation before Draw");
   service.Update();Check(api.Composes==0,"Framework never executes draw-list composition");
   Check(service.Diagnostics.phase=="awaiting-composition","completed physical copy awaits the Draw handoff");
   service.Draw();Check(api.Composes==1&&charTexture.Disposes==1,"Draw consumes and releases owned layers exactly once");
   Check(service.Diagnostics is {busy:true,hasImage:false,phase:"awaiting-publication"},"candidate is not diagnosed as published before Framework validation");
   Check(service.AcquireCurrent() is null,"composition requires another fresh Framework validation before publication");
   service.Update();using(var image=service.AcquireCurrent())
    Check(image is not null&&image.Width==576&&image.Height==960&&image.Sample.StateKey=="state-a","published image keeps genuine dimensions and source identity");
   Check(api.Outputs.Single().Disposes==0&&api.Outputs.Single().Shares.Single().Disposes==1,"consumer owns its independent shared lease");
   var shares=api.Outputs.Single().Shares.Count;
   Check(service.Diagnostics is {busy:false,hasImage:true,phase:"published",width:576,height:960,stateKey:"state-a"}
     &&api.Outputs.Single().Shares.Count==shares&&api.Captures==1,
     "diagnostics expose actual published dimensions and state without acquiring a texture or starting work");
   service.Update();service.Draw();Check(api.Captures==1,"unchanged state does not continuously regenerate pixels");
   service.Draw(inputHeld:true);
   using(var selecting=service.AcquireCurrent())
    Check(selecting is not null&&selecting.Sample==api.Sample&&api.Outputs.Single().Disposes==0,
     "held UI pointer retains the already validated owned image for subject selection");
   service.Update();service.Draw(inputHeld:true);
   using(var selecting=service.AcquireCurrent())
    Check(selecting is not null&&api.Captures==1,"continued selection retains current image without starting another physical capture");
   api.Sample=api.Sample with{StateKey="state-b"};service.Update();service.Draw(inputHeld:true);
   Check(service.AcquireCurrent() is null&&api.Outputs.Single().Disposes==1,"external edit invalidates old image without writing any editor state");
   service.Update();now=now.AddMilliseconds(200);service.Update();Check(api.Captures==2,"next settled external state can be captured");
   var stale=new CountingCaptureTexture();api.Complete(new(576,960,[new(stale)]));
   api.Sample=api.Sample with{Owner=new(5,6)};service.Update();service.Draw();
   Check(stale.Disposes==1&&api.Composes==1,"old-character completion is released before composition");
   service.Close();
   Check(service.Diagnostics is {busy:false,hasImage:false,phase:"closed",width:0,height:0,stateKey:""},"closed capture diagnostics do not retain an old image identity");
  }
  foreach(var change in new[]{"editor","scope","rectangle","viewport","unready","close"})
  {
   var a=new FakeCaptureApi();using var service=new PortraitLiveImageService(a,()=>now);
   service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();now=now.AddMilliseconds(200);service.Update();
   var old=new CountingCaptureTexture();a.Complete(new(576,960,[new(old)]));
   switch(change)
   {
    case "editor":a.Sample=a.Sample with{Editor=a.Sample.Editor with{Generation=42}};break;
    case "scope":service.Request(PortraitImageScope.VisibleAdventurerPlate,a.Sample.Viewport);break;
    case "rectangle":a.Sample=a.Sample with{Plate=new(4,5,6,7,8,9,10,new(11,12,13,14),"card")};break;
    case "viewport":a.Sample=a.Sample with{Viewport=a.Sample.Viewport with{Size=new(1000,700)}};break;
    case "unready":a.Ready=false;break;
    case "close":service.Close();break;
   }
   service.Update();service.Draw();Check(old.Disposes==1&&a.Composes==0,change+" invalidates completion without leaking or drawing stale data");
  }
  {
   var a=new FakeCaptureApi();var service=new PortraitLiveImageService(a,()=>now);
   service.Request(PortraitImageScope.VisibleAdventurerPlate,a.Sample.Viewport);service.Update();now=now.AddMilliseconds(200);service.Update();
   var before=DateTime.UtcNow;service.Dispose();Check(DateTime.UtcNow-before<TimeSpan.FromSeconds(1)&&!a.Pending.Task.IsCompleted,"unload never waits for Present or an unfinished source");
   var retirement=PhysicalDrain(service);var wasPending=!retirement.IsCompleted;
   Check(!service.IsBusy&&!service.IsPhysicallyIdle,"logical live-image close cannot qualify census while its physical provider tail remains");
   var late=new CountingCaptureTexture();a.Complete(new(800,500,[new(late)],false));
   await Until(()=>late.Disposes==1);
   await retirement.WaitAsync(TimeSpan.FromSeconds(5));
   Check(service.IsPhysicallyIdle,"live-image physical idle becomes true only after late resource release");
   Check(wasPending,"host live-image lifetime cannot retire while physical copy is unfinished");
   service.Dispose();service.Update();service.Draw();Check(late.Disposes==1&&a.Composes==0&&service.AcquireCurrent() is null,"late capture after unload is released once and never published");
  }
  {
   var a=new FakeCaptureApi();using var service=new PortraitLiveImageService(a,()=>now);
   service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();now=now.AddMilliseconds(200);service.Update();
   service.Close();service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();now=now.AddSeconds(1);service.Update();
   Check(a.Captures==1,"closing and retrying does not bypass an actual physical in-flight source");
   var late=new CountingCaptureTexture();a.Complete(new(576,960,[new(late)]));service.Update();
   Check(late.Disposes==1,"late old request is released when a new request owns the surface");
  }
  {
   var a=new FakeCaptureApi();using var service=new PortraitLiveImageService(a,()=>now);
   var heldDuringPrefix=false;
   a.OnCapture=()=>{service.Dispose();heldDuringPrefix=!PhysicalDrain(service).IsCompleted;};service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();now=now.AddMilliseconds(200);service.Update();
   var late=new CountingCaptureTexture();a.Complete(new(576,960,[new(late)]));await Until(()=>late.Disposes==1);
   Check(a.Composes==0,"reentrant unload during source acquisition still observes and releases its late result");
   Check(heldDuringPrefix,"physical live retirement admits ownership before reentrant provider prefix");
  }
  {
   var a=new FakeCaptureApi();using var service=new PortraitLiveImageService(a,()=>now);
   service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();now=now.AddMilliseconds(200);service.Update();
   var layer=new CountingCaptureTexture();a.Complete(new(576,960,[new(layer)]));service.Update();
   var heldDuringCompose=false;
   a.OnCompose=()=>{service.Dispose();heldDuringCompose=!PhysicalDrain(service).IsCompleted;};service.Draw();
   Check(layer.Disposes==1&&a.Outputs.Single().Disposes==1&&service.AcquireCurrent() is null,"reentrant unload during composition releases both layers and returned image once");
   Check(heldDuringCompose,"physical live retirement cannot complete inside a still-running composition prefix");
   await PhysicalDrain(service).WaitAsync(TimeSpan.FromSeconds(5));
  }
  {
   var a=new FakeCaptureApi();using var service=new PortraitLiveImageService(a,()=>now);
   service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();now=now.AddMilliseconds(200);service.Update();
   a.Pending.SetException(new InvalidOperationException("secret implementation failure"));service.Update();
   Check(service.Error is {Length:>0}&&!service.Error.Contains("secret"),"capture errors are observed and sanitized");
   now=now.AddSeconds(10);service.Update();Check(a.Captures==1,"same failed state does not spin repeated GPU captures");
  }
  {
   var texture=new CountingCaptureTexture();var layers=new PortraitCaptureLayers(576,960,[new(texture)]);
   layers.Dispose();layers.Dispose();Check(texture.Disposes==1,"layer owner disposal is idempotent");
  }
  {
   var a=new FakeCaptureApi{Ready=false};using var service=new PortraitLiveImageService(a,()=>now);
   service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();
   Check(!service.IsBusy&&a.Captures==0,"unready editor retains a request without a physical task");
   a.Ready=true;var samples=a.Samples;now=now.AddSeconds(1);
   service.Update(false);
   Check(a.Samples==samples&&a.Captures==0,"off revokes a retained request before any native sampling");
   Check(service.Diagnostics.phase=="closed","off removes even a request with no physical task");
   service.Update(true);now=now.AddSeconds(1);service.Update(true);
   Check(a.Samples==samples&&a.Captures==0,"reopening alone cannot revive the revoked native request");
   service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update(true);
   now=now.AddMilliseconds(200);service.Update(true);
   Check(a.Captures==1,"restored live mode accepts a new explicit request after settling");
   var fresh=new CountingCaptureTexture();a.Complete(new(576,960,[new(fresh)]));
   service.Update(true);service.Draw();service.Update(true);
   using(var image=service.AcquireCurrent())Check(image is not null,"fresh live request can publish after isolation ends");
  }
  {
   var a=new FakeCaptureApi();using var service=new PortraitLiveImageService(a,()=>now);
   service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();now=now.AddMilliseconds(200);service.Update();
   var samples=a.Samples;
   service.Update(false);service.Draw();
   Check(service.IsBusy&&!a.Pending.Task.IsCompleted,"off retains the physical copy barrier without cancelling or synchronously waiting");
   service.Update(false);
   Check(a.Samples==samples&&a.Captures==1,"off drains pending work without sampling or starting a second capture");
   var late=new CountingCaptureTexture();a.Complete(new(576,960,[new(late)]));
   service.Update(false);service.Draw();service.Update(false);
   Check(!service.IsBusy&&late.Disposes==1&&a.Composes==0&&service.AcquireCurrent() is null,
    "off releases late physical layers once without composition or publication");
   service.Update(true);now=now.AddSeconds(1);service.Update(true);
   Check(a.Captures==1&&a.Samples==samples,"draining a physical task cannot revive its old request");
  }
  foreach(var stage in new[]{"layers","candidate","published"})
  {
   var a=new FakeCaptureApi();using var service=new PortraitLiveImageService(a,()=>now);
   service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();now=now.AddMilliseconds(200);service.Update();
   var layer=new CountingCaptureTexture();a.Complete(new(576,960,[new(layer)]));service.Update();
   if(stage!="layers")service.Draw();
   if(stage=="published")service.Update();
   var samples=a.Samples;
   service.Update(false);service.Draw();service.Update(false);
   Check(layer.Disposes==1&&a.Outputs.All(texture=>texture.Disposes==1),stage+" isolation releases each owned resource once");
   Check(!service.IsBusy&&service.AcquireCurrent() is null&&a.Samples==samples,
    stage+" isolation prevents publication and further native sampling");
   Check(a.Composes==(stage=="layers"?0:1),stage+" isolation starts no extra composition");
  }
  foreach(var stage in new[]{"late","layers","candidate","published"})
  {
   var a=new FakeCaptureApi();var service=new PortraitLiveImageService(a,()=>now);
   service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();now=now.AddMilliseconds(200);service.Update();
   var failed=new CountingCaptureTexture{OnDispose=()=>throw new InvalidOperationException("unconfirmed release")};
   if(stage!="late")
   {
    a.Complete(new(576,960,[new(failed)]));service.Update();
    if(stage is "candidate" or "published")
    {failed.OnDispose=null;service.Draw();a.Outputs.Single().OnDispose=()=>throw new InvalidOperationException("unconfirmed release");}
    if(stage=="published")service.Update();
   }
   service.Dispose();var retirement=PhysicalDrain(service);
   if(stage=="late")a.Complete(new(576,960,[new(failed)]));
   await Until(()=>stage is "candidate" or "published"?a.Outputs.Single().Disposes==1:failed.Disposes==1);
   await Task.Delay(20);
   Check(!retirement.IsCompleted,stage+" live texture release failure quarantines ownership and keeps host retirement pending");
  }
  {
   var failed=new CountingCaptureTexture{OnDispose=()=>throw new InvalidOperationException("unconfirmed release")};
   var other=new CountingCaptureTexture();var layers=new PortraitCaptureLayers(576,960,[new(failed),new(other)]);
   try{layers.Dispose();}catch(Exception){}
   Check(failed.Disposes==1&&other.Disposes==1,"layer release attempts every independent resource even when the first release fails");
  }
  foreach(var change in new[]{"none","dispose","close","replace","failed consumer release","failed share cleanup"})
  {
   var a=new FakeCaptureApi();var service=new PortraitLiveImageService(a,()=>now);
   service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();now=now.AddMilliseconds(200);service.Update();
   a.Complete(new(576,960,[new(new CountingCaptureTexture())]));service.Update();service.Draw();service.Update();
   Task? reentrantDrain=null;var heldInPrefix=false;
   var output=a.Outputs.Single();
   output.OnShare=shared=>
   {
    switch(change)
    {
     case "dispose":service.Dispose();reentrantDrain=PhysicalDrain(service);heldInPrefix=!reentrantDrain.IsCompleted;break;
     case "close":service.Close();break;
     case "replace":service.Request(PortraitImageScope.VisibleAdventurerPlate,a.Sample.Viewport);break;
     case "failed consumer release":shared.OnDispose=()=>throw new InvalidOperationException("unknown shared release");break;
     case "failed share cleanup":shared.OnDispose=()=>throw new InvalidOperationException("unknown prefix release");PortraitCaptureCleanupException.DisposeAll(shared);break;
    }
   };
   PortraitCapturedImage? borrowed=null;Exception? failure=null;
   try{borrowed=service.AcquireCurrent();}catch(Exception error){failure=error;}
   var shared=output.Shares.Single();
   if(change is "none" or "failed consumer release")
   {
    service.Dispose();var retirement=PhysicalDrain(service);
    Check(borrowed is not null&&!retirement.IsCompleted,"borrowed live image retains provider lifetime until consumer disposal: "+change);
    borrowed!.Dispose();borrowed.Dispose();
    Check(shared.Disposes==1,"borrowed live image releases its independent shared wrapper once: "+change);
    Check(change=="none"?retirement.IsCompletedSuccessfully:!retirement.IsCompleted&&service.Diagnostics.cleanupFailures==1,
     "borrowed live image confirms physical release or quarantines its failure: "+change);
   }
   else
   {
    service.Dispose();var retirement=PhysicalDrain(service);
    Check(failure is null&&borrowed is null&&shared.Disposes==1,"sharing reentry or cleanup failure cannot escape a stale untracked image: "+change);
    Check(change=="failed share cleanup"?!retirement.IsCompleted&&service.Diagnostics.cleanupFailures==1:retirement.IsCompletedSuccessfully,
     "sharing prefix completion releases or quarantines actual owned resources: "+change);
    if(change=="dispose")Check(heldInPrefix&&reentrantDrain is{IsCompletedSuccessfully:true},"sharing prefix owns a physical lease before reentrant dispose seals the scope");
   }
  }
  return checks;
 }
 private static Task PhysicalDrain(object owner)=>owner.GetType().GetMethod("DrainPhysicalAsync")?.Invoke(owner,null) as Task??Task.CompletedTask;
 private static async Task Until(Func<bool> condition)
 {
  var end=DateTime.UtcNow.AddSeconds(5);
  while(!condition()){if(DateTime.UtcNow>end)throw new TimeoutException("capture cleanup did not finish");await Task.Delay(1);}
 }
}

internal sealed class FakeCaptureApi : IPortraitLightingFitCaptureApi
{
 public bool Framework=true,Ready=true;
 public int Samples,Captures,Composes;
 public Action? OnCapture,OnCompose;
 public PortraitCaptureSample Sample=new(new(1,2),new(11,12,13,14,15),"state-a",PortraitImageScope.Portrait,new(7,Vector2.Zero,new(1920,1080)));
 public TaskCompletionSource<PortraitCaptureLayers?> Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public List<CountingCaptureTexture> Outputs=[];
 public bool IsFrameworkThread=>Framework;
 public bool TrySample(PortraitImageScope scope,PortraitCaptureViewport view,out PortraitCaptureSample? sample)
 {Samples++;sample=Ready?Sample with{Scope=scope}:null;return sample is not null;}
 public Task<PortraitCaptureLayers?> CaptureAsync(PortraitCaptureSample sample)
 {Captures++;Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);OnCapture?.Invoke();return Pending.Task;}
 public bool TryDescribePortrait(PortraitCaptureSample sample,out PortraitCaptureSurface? surface)
 {surface=new("controlled-lifecycle-surface",576,960);return true;}
 public Task<PortraitCaptureLayers?> CaptureAsync(PortraitCaptureSample sample,PortraitCaptureSurface expectedSurface)
  =>CaptureAsync(sample);
 public void Complete(PortraitCaptureLayers layers)=>Pending.SetResult(layers);
 public IDalamudTextureWrap Compose(PortraitCaptureLayers layers)
 {Composes++;var result=new CountingCaptureTexture(layers.Width,layers.Height);Outputs.Add(result);OnCompose?.Invoke();return result;}
}
internal sealed class CountingCaptureTexture(int width=576,int height=960,nint handle=123) : IDalamudTextureWrap
{
 public int Disposes;
 public Action? OnDispose;
 public Action<CountingCaptureTexture>? OnShare;
 public List<CountingCaptureTexture> Shares=[];
 public ImTextureID Handle=>new(handle);
 public int Width=>width;
 public int Height=>height;
 public void Dispose(){Interlocked.Increment(ref Disposes);OnDispose?.Invoke();}
 public IDalamudTextureWrap CreateWrapSharingLowLevelResource(){var result=new CountingCaptureTexture(width,height);Shares.Add(result);OnShare?.Invoke(result);return result;}
}
#endif
