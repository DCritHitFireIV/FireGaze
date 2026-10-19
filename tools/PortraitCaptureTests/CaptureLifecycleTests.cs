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
   var late=new CountingCaptureTexture();a.Complete(new(800,500,[new(late)],false));
   await Until(()=>late.Disposes==1);
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
   a.OnCapture=service.Dispose;service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();now=now.AddMilliseconds(200);service.Update();
   var late=new CountingCaptureTexture();a.Complete(new(576,960,[new(late)]));await Until(()=>late.Disposes==1);
   Check(a.Composes==0,"reentrant unload during source acquisition still observes and releases its late result");
  }
  {
   var a=new FakeCaptureApi();using var service=new PortraitLiveImageService(a,()=>now);
   service.Request(PortraitImageScope.Portrait,a.Sample.Viewport);service.Update();now=now.AddMilliseconds(200);service.Update();
   var layer=new CountingCaptureTexture();a.Complete(new(576,960,[new(layer)]));service.Update();
   a.OnCompose=service.Dispose;service.Draw();
   Check(layer.Disposes==1&&a.Outputs.Single().Disposes==1&&service.AcquireCurrent() is null,"reentrant unload during composition releases both layers and returned image once");
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
  return checks;
 }
 private static async Task Until(Func<bool> condition)
 {
  var end=DateTime.UtcNow.AddSeconds(5);
  while(!condition()){if(DateTime.UtcNow>end)throw new TimeoutException("capture cleanup did not finish");await Task.Delay(1);}
 }
}

internal sealed class FakeCaptureApi : IPortraitLiveImageApi
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
 public void Complete(PortraitCaptureLayers layers)=>Pending.SetResult(layers);
 public IDalamudTextureWrap Compose(PortraitCaptureLayers layers)
 {Composes++;var result=new CountingCaptureTexture(layers.Width,layers.Height);Outputs.Add(result);OnCompose?.Invoke();return result;}
}
internal sealed class CountingCaptureTexture(int width=576,int height=960,nint handle=123) : IDalamudTextureWrap
{
 public int Disposes;
 public List<CountingCaptureTexture> Shares=[];
 public ImTextureID Handle=>new(handle);
 public int Width=>width;
 public int Height=>height;
 public void Dispose()=>Interlocked.Increment(ref Disposes);
 public IDalamudTextureWrap CreateWrapSharingLowLevelResource(){var result=new CountingCaptureTexture(width,height);Shares.Add(result);return result;}
}
#endif
