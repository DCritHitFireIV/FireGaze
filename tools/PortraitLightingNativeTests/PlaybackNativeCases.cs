using FireGaze.PortraitPreview;

internal static unsafe class PlaybackNativeCases
{
 static bool Set(NativePortraitTarget target,PortraitIdentity identity,bool paused)
  =>target.SetAnimationPaused(identity,paused);
 public static void Run(Action<bool,string> check)
 {
  using var f=new EditorFixture();using var target=new NativePortraitTarget(f,_=>false,_=>{});
  target.TryResolve(out var identity,out _,out _);
  var before=target.CaptureEditorState(identity)!;
  check(Set(target,identity,true)&&f.Paused&&f.Calls==1&&f.PauseCalls==1,"narrow playback pause invokes exactly one native pause operation");
  var after=target.CaptureEditorState(identity)!;
  check(after.ToJSON()==(before with{Composition=before.Composition with{Paused=true}}).ToJSON(),"playback pause preserves exact wardrobe, beauty, pose, camera, light and decorations");
  check(f.PoseCalls==0&&f.FrameCalls==0&&f.AccentCalls==0&&f.VisibilityCalls==0,"playback never reapplies a snapshot, animation timeline or camera");
  var calls=f.Calls;
  check(Set(target,identity,true)&&f.Calls==calls,"already paused request is accepted without another native call");
  f.Framework=false;check(!Set(target,identity,false)&&f.Calls==calls,"playback refuses native pause changes off Framework");f.Framework=true;
  check(!Set(target,identity with{Generation=999},false)&&f.Calls==calls,"playback refuses a replaced owner before calling native pause");
  f.DeferPause=true;
  check(Set(target,identity,false)&&f.Calls==calls+1&&f.Paused&&f.View->IsAnimationPauseStatePending,"accepted asynchronous native playback remains successful while pause state is pending");
  calls=f.Calls;check(!Set(target,identity,false)&&f.Calls==calls,"a new playback operation waits until a fresh snapshot can be read");
  f.DeferPause=false;f.View->IsAnimationPauseStatePending=false;f.Paused=false;
  f.AfterPause=()=>f.Editor->EditorState=null;
  check(!Set(target,identity,true)&&f.Calls==calls+1&&f.PoseCalls==0,"owner replacement during native pause stops without replaying previous pose");
  f.Editor->EditorState=f.State;f.AfterPause=()=>throw new InvalidOperationException("native pause failure");calls=f.Calls;
  check(!Set(target,identity,false)&&f.Calls==calls+1&&!f.Paused&&f.PoseCalls==0,"native pause exception is contained without overwriting the newly applied playback state");
  f.AfterPause=null;
  check(target.CaptureEditorState(identity)!.ToJSON()==before.ToJSON(),"failed playback path preserves every unrelated snapshot field");
 }
}
