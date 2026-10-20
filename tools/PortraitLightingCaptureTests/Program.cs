using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.PortraitPreview;

#if !HAS_BRIDGE
Console.Error.WriteLine("FAIL no real settled Framework/Draw/CPU capture bridge exists");
return 1;
#else
var checks=0;
void Check(bool condition,string name){if(!condition)throw new InvalidOperationException(name);checks++;Console.WriteLine("PASS "+name);}
static PortraitLightingFitCaptureFrame Frame(FakeApi api)=>new(api.Sample,api.Surface);
static void Settle(PortraitLightingFitCaptureBridge bridge){bridge.Update();bridge.Draw();bridge.Update();bridge.Draw();bridge.Update();}
static async Task Copied(PortraitLightingFitCaptureBridge bridge,FakeApi api,Readback readback,Texture texture)
{api.Copy.SetResult(new(2,1,[new(texture)]));bridge.Update();bridge.Draw();bridge.Update();await Until(()=>readback.SubmissionEntered.IsSet);}
static async Task Until(Func<bool> condition){var end=DateTime.UtcNow.AddSeconds(5);while(!condition()){if(DateTime.UtcNow>end)throw new TimeoutException();await Task.Delay(1);}}
var api=new FakeApi();var readback=new Readback(api);
using(var bridge=new PortraitLightingFitCaptureBridge(api,readback))
{
 api.Framework=false;
 Check(!bridge.TryReadFrame(api.Sample.Viewport,out _)&&api.Samples==0,"off-Framework observation never reads source");
 api.Framework=true;
 Check(bridge.TryReadFrame(api.Sample.Viewport,out var described)&&described==Frame(api),"frame describes full sample plus color-independent surface generation and dimensions");
 var task=bridge.CaptureAsync(described!);
 bridge.Update();Check(api.Copies==0,"request cannot capture before any rendered frame");
 bridge.Draw();bridge.Update();Check(api.Copies==0,"one rendered frame is insufficient after a state change");
 bridge.Draw();bridge.Update();Check(api.Copies==1&&!task.IsCompleted,"two stable rendered frames start one physical public copy");
 bridge.Update();bridge.Draw();Check(api.Copies==1&&api.Composes==0,"pending physical copy is never overlapped or synchronously waited");
 var layer=new Texture();api.Copy.SetResult(new(2,1,[new(layer)]));
 bridge.Draw();Check(api.Composes==0,"copy completion cannot bypass a fresh Framework sample before composition");
 bridge.Update();Check(api.Composes==0,"Framework validation never composes a draw list");
 bridge.Draw();Check(api.Composes==1&&layer.Disposes==1&&readback.Calls==0,"Draw composes and releases input layers without starting CPU readback");
 bridge.Update();await Until(()=>readback.SubmissionEntered.IsSet);Check(api.Outputs[0].Disposes==0,"next Framework validation begins readback while retaining composed surface");
 readback.Pending.SetResult([new(12,34,56,255),new(78,90,123,255)]);
 await Until(()=>api.Outputs[0].Disposes==1);
 Check(!task.IsCompleted,"completed CPU readback waits for Framework post-validation");
 bridge.Update();var pixels=await task;
 Check(pixels.Count==2&&pixels[0]==new PortraitLightRgba(12,34,56,255),"published result contains CPU-owned RGBA pixels only");
 Check(!bridge.IsBusy&&api.Outputs[0].Disposes==1,"successful publication ends physical flight and disposes output once");
}
foreach(var change in new[]{"owner","editor","state","surface","size","viewport","unready"})
{
 var a=new FakeApi();var r=new Readback(a);using var bridge=new PortraitLightingFitCaptureBridge(a,r);
 var task=bridge.CaptureAsync(Frame(a));Settle(bridge);
 var late=new Texture();a.Copy.SetResult(new(2,1,[new(late)]));
 switch(change){
  case "owner":a.Sample=a.Sample with{Owner=new(99,100)};break;
  case "editor":a.Sample=a.Sample with{Editor=a.Sample.Editor with{Generation=42}};break;
  case "state":a.Sample=a.Sample with{StateKey="changed-color-or-pose"};break;
  case "surface":a.Surface=a.Surface with{GenerationKey="new-srv-or-crop"};break;
  case "size":a.Surface=a.Surface with{Width=3};break;
  case "viewport":a.Sample=a.Sample with{Viewport=a.Sample.Viewport with{Size=new(1000,700)}};break;
  case "unready":a.Ready=false;break;
 }
 bridge.Update();bridge.Draw();Check(task.IsFaulted&&late.Disposes==1&&a.Composes==0,change+" mismatch rejects capture and releases late layers before Draw");
 _=task.Exception;
}
{
 var a=new FakeApi();var r=new Readback(a);using var bridge=new PortraitLightingFitCaptureBridge(a,r);
 var task=bridge.CaptureAsync(Frame(a));bridge.Draw();a.Sample=a.Sample with{StateKey="changed"};bridge.Update();bridge.Draw();bridge.Update();
 Check(task.IsFaulted&&a.Copies==0,"state change during two-frame settling never starts stale capture");_=task.Exception;
 var next=bridge.CaptureAsync(Frame(a));bridge.Update();bridge.Draw();bridge.Update();
 Check(a.Copies==0,"new request starts its own two-frame settling window");
 bridge.Draw();bridge.Update();Check(a.Copies==1,"second stable render allows the new state capture");
 bridge.Close();var layer=new Texture();a.Copy.SetResult(new(2,1,[new(layer)]));bridge.Update();Check(next.IsFaulted&&layer.Disposes==1,"closed request drains its actual copy without publication");_=next.Exception;
}
foreach(var phase in new[]{"copy","readback"})
{
 var a=new FakeApi();var r=new Readback(a);using var bridge=new PortraitLightingFitCaptureBridge(a,r);
 var old=bridge.CaptureAsync(Frame(a));Settle(bridge);
 if(phase=="readback")await Copied(bridge,a,r,new Texture());
 bridge.Close();
 var refused=bridge.CaptureAsync(Frame(a));Check(refused.IsFaulted&&bridge.IsBusy&&!old.IsCompleted,phase+" cancellation preserves one physical in-flight barrier across reopen");_=refused.Exception;
 if(phase=="copy"){var late=new Texture();a.Copy.SetResult(new(2,1,[new(late)]));bridge.Update();Check(late.Disposes==1,"cancelled late copy is disposed exactly once");}
 else{r.Pending.SetResult([new(1,2,3,255),new(4,5,6,255)]);await Until(()=>a.Outputs[0].Disposes==1);bridge.Update();}
 Check(old.IsFaulted&&!bridge.IsBusy,phase+" cancellation completes only after physical resources drain");_=old.Exception;
 var fresh=bridge.CaptureAsync(Frame(a));Check(!fresh.IsCompleted,"new capture may start after old physical work completes");bridge.Close();_=fresh.Exception;
}
{
 var a=new FakeApi();var r=new Readback(a){BlockSubmission=true};using var bridge=new PortraitLightingFitCaptureBridge(a,r);
 var task=bridge.CaptureAsync(Frame(a));Settle(bridge);await Copied(bridge,a,r,new Texture());
 Check(r.SubmissionEntered.Wait(TimeSpan.FromSeconds(5)),"physical readback submission starts after Framework validation");
 Check(!r.SubmissionReturned.IsSet,"Framework update returns while a readback provider synchronous prefix remains blocked");
 r.ReleaseSubmission.Set();r.Pending.SetResult([new(1,2,3,255),new(4,5,6,255)]);
 await Until(()=>a.Outputs[0].Disposes==1);bridge.Update();Check(task.IsCompletedSuccessfully,"deferred readback still publishes after Framework validation");
}
{
 var a=new FakeApi();var r=new Readback(a);using var bridge=new PortraitLightingFitCaptureBridge(a,r);
 var task=bridge.CaptureAsync(Frame(a));Settle(bridge);await Copied(bridge,a,r,new Texture());
 a.Sample=a.Sample with{StateKey="changed-during-readback"};r.Pending.SetResult([new(1,2,3,255),new(4,5,6,255)]);
 await Until(()=>a.Outputs[0].Disposes==1);bridge.Update();
 Check(task.IsFaulted,"full sample is revalidated after CPU readback before publication");_=task.Exception;
}
foreach(var phase in new[]{"copy","readback"})
{
 var a=new FakeApi();var r=new Readback(a);var bridge=new PortraitLightingFitCaptureBridge(a,r);
 var task=bridge.CaptureAsync(Frame(a));Settle(bridge);
 if(phase=="readback")await Copied(bridge,a,r,new Texture());
 var before=DateTime.UtcNow;bridge.Dispose();Check(DateTime.UtcNow-before<TimeSpan.FromSeconds(1)&&!task.IsCompleted,phase+" unload never blocks on unfinished GPU work");
 var retirement=bridge.GetType().GetMethod("DrainPhysicalAsync")?.Invoke(bridge,null) as Task??Task.CompletedTask;
 var held=!retirement.IsCompleted;
 Check(!bridge.IsPhysicallyIdle,phase+" census admission remains closed through the physical lighting tail");
 Texture? late=null;
 if(phase=="copy"){late=new Texture();a.Copy.SetResult(new(2,1,[new(late)]));}
 else r.Pending.SetResult([new(1,2,3,255),new(4,5,6,255)]);
 await Until(()=>task.IsCompleted);
 await retirement.WaitAsync(TimeSpan.FromSeconds(5));
 Check(bridge.IsPhysicallyIdle,phase+" census physical idle is restored only after release");
 Check(held,phase+" host lifetime remains held until physical completion and late release");
 Check(task.IsFaulted&&(late?.Disposes??a.Outputs[0].Disposes)==1,phase+" late result after unload is released and cannot publish");_=task.Exception;
}
{
 var a=new FakeApi{ThrowSample=true};using var bridge=new PortraitLightingFitCaptureBridge(a,new Readback(a));
 Check(!bridge.TryReadFrame(a.Sample.Viewport,out _),"source exception is contained as an unavailable frame");
 var task=bridge.CaptureAsync(Frame(a));Check(task.IsFaulted&&!bridge.IsBusy,"source exception refuses capture without starting a physical task");_=task.Exception;
}
foreach(var phase in new[]{"copy","compose","readback"})foreach(var dispose in new[]{false,true})
{
 var a=new FakeApi();var r=new Readback(a);var bridge=new PortraitLightingFitCaptureBridge(a,r);
 var heldInPrefix=false;
 Action cancel=()=>{if(dispose){bridge.Dispose();heldInPrefix=!(bridge.GetType().GetMethod("DrainPhysicalAsync")?.Invoke(bridge,null) as Task??Task.CompletedTask).IsCompleted;}else bridge.Close();};
 if(phase=="copy")a.OnCapture=cancel;else if(phase=="compose")a.OnCompose=cancel;else r.OnRead=cancel;
 var task=bridge.CaptureAsync(Frame(a));Settle(bridge);
 if(phase=="readback")await Copied(bridge,a,r,new Texture());
 else if(phase=="compose"){a.Copy.SetResult(new(2,1,[new(new Texture())]));bridge.Update();bridge.Draw();bridge.Update();}
 if(phase=="copy")a.Copy.SetResult(new(2,1,[new(new Texture())]));
 else if(phase=="readback")r.Pending.SetResult([new(1,2,3,255),new(4,5,6,255)]);
 for(var spin=0;spin<30&&!task.IsCompleted;spin++){bridge.Update();await Task.Delay(1);}
 Check(task.IsFaulted&&!bridge.IsBusy,phase+" synchronous "+(dispose?"dispose":"close")+" reentrancy preserves and drains physical ownership");
 Check(a.Outputs.All(x=>x.Disposes==1),phase+" synchronous cancellation releases each composed output once");_=task.Exception;bridge.Dispose();
 if(dispose)Check(heldInPrefix,phase+" reentrant prefix retains physical ownership until actual release");
}
foreach(var failure in new[]{"copy","compose","readback","dimensions","pixels","nonfinite"})
{
 var a=new FakeApi();var r=new Readback(a);using var bridge=new PortraitLightingFitCaptureBridge(a,r);
 var task=bridge.CaptureAsync(Frame(a));Settle(bridge);
 if(failure=="copy")a.Copy.SetException(new InvalidOperationException("copy failure"));
 else
 {
  if(failure=="compose")a.OnCompose=()=>throw new InvalidOperationException("compose failure");
  a.Copy.SetResult(new(failure=="dimensions"?3:2,1,[new(new Texture())]));bridge.Update();bridge.Draw();bridge.Update();
  if(failure=="readback")
  {
   await Until(()=>r.SubmissionEntered.IsSet);
   r.Pending.SetException(new InvalidOperationException("read failure"));
  }
  else if(failure=="pixels")
  {
   await Until(()=>r.SubmissionEntered.IsSet);
   r.Pending.SetResult([new(1,2,3,255)]);
  }
  else if(failure=="nonfinite")
  {
   await Until(()=>r.SubmissionEntered.IsSet);
   r.Pending.SetResult([new(double.NaN,2,3,255),new(1,2,3,255)]);
  }
 }
 for(var spin=0;spin<30&&!task.IsCompleted;spin++){bridge.Update();await Task.Delay(1);}
 Check(task.IsFaulted&&!bridge.IsBusy,failure+" failure rejects publication and drains bridge");
 Check(a.Outputs.All(x=>x.Disposes==1),failure+" failure disposes composed outputs once");_=task.Exception;
}
foreach(var format in new[]{28,29,87,91})
{
 var bytes=new byte[]{1,2,3,255,0,0,0,0,10,20,30,128,0,0,0,0};
 var pixels=PortraitLightingFitTextureReadback.Decode(bytes,1,2,8,format);
 Check(pixels.Count==2&&pixels[0]==(format is 87 or 91?new PortraitLightRgba(3,2,1,255):new PortraitLightRgba(1,2,3,255))
  &&pixels[1].A==128,"raw format "+format+" decodes channel order, pitch padding and alpha without retaining bytes");
}
foreach(var input in new (int Width,int Height,int Pitch,int Format,byte[] Bytes)[]{(1,1,4,0,new byte[4]),(0,1,4,28,new byte[4]),(2,1,4,28,new byte[8]),(2,2,8,28,new byte[8])})
{
 try{PortraitLightingFitTextureReadback.Decode(input.Bytes,input.Width,input.Height,input.Pitch,input.Format);Check(false,"invalid raw input rejected");}
 catch(InvalidOperationException){Check(true,"invalid format/dimensions/pitch/truncated data rejected before decode");}
}
foreach(var phase in new[]{"copy","layers","candidate","readback"})
{
 var a=new FakeApi();var r=new Readback(a);var bridge=new PortraitLightingFitCaptureBridge(a,r);
 var publication=bridge.CaptureAsync(Frame(a));Settle(bridge);
 var failed=new Texture{OnDispose=()=>throw new InvalidOperationException("unconfirmed release")};
 if(phase!="copy")
 {
  a.Copy.SetResult(new(2,1,[new(failed)]));bridge.Update();
  if(phase is "candidate" or "readback")
  {failed.OnDispose=null;bridge.Draw();a.Outputs.Single().OnDispose=()=>throw new InvalidOperationException("unconfirmed release");}
  if(phase=="readback"){bridge.Update();await Until(()=>r.SubmissionEntered.IsSet);}
 }
 bridge.Dispose();var retirement=bridge.DrainPhysicalAsync();
 if(phase=="copy")a.Copy.SetResult(new(2,1,[new(failed)]));
 if(phase=="readback")r.Pending.SetResult([new(1,2,3,255),new(4,5,6,255)]);
 await Until(()=>phase is "candidate" or "readback"?a.Outputs.Single().Disposes==1:failed.Disposes==1);
 await Task.Delay(20);
 Check(!retirement.IsCompleted,phase+" lighting texture release failure keeps provider lifetime quarantined");
 _=publication.Exception;
}
foreach(var reentrant in new[]{false,true})
{
 var a=new FakeApi();var bridge=new PortraitLightingFitCaptureBridge(a,new Readback(a));
 var failed=new Texture{OnDispose=()=>throw new InvalidOperationException("unconfirmed description release")};
 Task? earlyDrain=null;var heldInPrefix=false;
 a.OnDescribe=()=>
 {
  if(reentrant){bridge.Dispose();earlyDrain=bridge.DrainPhysicalAsync();heldInPrefix=!earlyDrain.IsCompleted;}
  PortraitCaptureCleanupException.DisposeAll(failed);
 };
 var accepted=bridge.TryReadFrame(a.Sample.Viewport,out _);
 bridge.Dispose();var retirement=bridge.DrainPhysicalAsync();
 Check(!accepted&&failed.Disposes==1&&bridge.CleanupFailures==1&&!retirement.IsCompleted,
  (reentrant?"reentrant":"ordinary")+" description cleanup failure remains quarantined instead of silently retiring physical ownership");
 if(reentrant)Check(heldInPrefix&&earlyDrain is{IsCompleted:false},"description owns its synchronous prefix before native cleanup can reenter Dispose and seal");
}
{
 var a=new FakeApi();var bridge=new PortraitLightingFitCaptureBridge(a,new Readback(a));
 var heldInPrefix=false;Task? earlyDrain=null;
 a.OnDescribe=()=>{bridge.Dispose();earlyDrain=bridge.DrainPhysicalAsync();heldInPrefix=!earlyDrain.IsCompleted;};
 var accepted=bridge.TryReadFrame(a.Sample.Viewport,out _);
 Check(!accepted&&heldInPrefix&&earlyDrain is{IsCompletedSuccessfully:true},"successful description retires only after its reentrant synchronous prefix has returned");
}
Console.WriteLine($"PortraitLightingCaptureTests: {checks} passed, 0 failed");return 0;

sealed class FakeApi:IPortraitLightingFitCaptureApi
{
 public bool Framework=true,Ready=true,ThrowSample;public int Samples,Copies,Composes;public Action? OnCapture,OnCompose,OnDescribe;
 public PortraitCaptureSample Sample=new(new(1,2),new(3,4,5,6,7),"full-state-a",PortraitImageScope.Portrait,new(1,Vector2.Zero,new(1920,1080)));
 public PortraitCaptureSurface Surface=new("surface-a",2,1);
 public bool IsFrameworkThread=>Framework;
 public TaskCompletionSource<PortraitCaptureLayers?> Copy=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public List<Texture> Outputs=[];
 public bool TrySample(PortraitImageScope scope,PortraitCaptureViewport viewport,out PortraitCaptureSample? sample){if(ThrowSample)throw new InvalidOperationException("private source failure");Samples++;sample=Ready?Sample:null;return Ready;}
 public bool TryDescribePortrait(PortraitCaptureSample sample,out PortraitCaptureSurface? surface){OnDescribe?.Invoke();surface=Ready?Surface:null;return Ready;}
 public Task<PortraitCaptureLayers?> CaptureAsync(PortraitCaptureSample sample)=>CaptureAsync(sample,Surface);
 public Task<PortraitCaptureLayers?> CaptureAsync(PortraitCaptureSample sample,PortraitCaptureSurface surface){if(!Framework||sample!=Sample||surface!=Surface)throw new InvalidOperationException();Copies++;Copy=new(TaskCreationOptions.RunContinuationsAsynchronously);OnCapture?.Invoke();return Copy.Task;}
 public IDalamudTextureWrap Compose(PortraitCaptureLayers layers){Composes++;OnCompose?.Invoke();var output=new Texture(layers.Width,layers.Height);Outputs.Add(output);return output;}
}
sealed class Readback(FakeApi api):IPortraitLightingFitReadback
{
 public int Calls;public Action? OnRead;public bool BlockSubmission;
 public ManualResetEventSlim SubmissionEntered=new(false),SubmissionReturned=new(false),ReleaseSubmission=new(false);
 public TaskCompletionSource<IReadOnlyList<PortraitLightRgba>> Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
 public Task<IReadOnlyList<PortraitLightRgba>> ReadAsync(IDalamudTextureWrap texture)
 {
  GC.KeepAlive(api);
  Calls++;Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);OnRead?.Invoke();SubmissionEntered.Set();
  if(BlockSubmission)ReleaseSubmission.Wait();SubmissionReturned.Set();return Pending.Task;
 }
}
sealed class Texture(int width=2,int height=1):IDalamudTextureWrap
{
 public int Disposes;public ImTextureID Handle=>new((nint)123);public int Width=>width;public int Height=>height;
 public Action? OnDispose;
 public void Dispose(){Interlocked.Increment(ref Disposes);OnDispose?.Invoke();}
}
#endif
