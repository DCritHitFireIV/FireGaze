using FireGaze.PortraitPreview;

unsafe class Program
{
 static int checks,failed;
 static void Check(bool result,string label){checks++;if(!result)failed++;Console.WriteLine((result?"PASS ":"FAIL ")+label);}
 static int Main()
 {
#if HAS_LIGHTING_NATIVE
  using var fixture=new EditorFixture();fixture.Paused=true;
  fixture.AfterDirectionalSet=null;fixture.ThrowDirectionalSet=false;fixture.DeferGazeExport=false;
  fixture.ReplaceOnPose=false;fixture.RestartPoseOnSeek=false;fixture.RejectCustomize=false;
  using var target=new NativePortraitTarget(fixture,_=>false,_=>{});
  Check(target.TryResolve(out var identity,out _,out _),"six-color native fixture resolves");
  var before=target.CaptureEditorState(identity)!;
  var desired=new PortraitNativeLightColors(31,32,33,41,42,43);
  var result=target.WriteLightColors(identity,before,desired);
  Check(result.Applied&&result.Owned==desired,"narrow native six-color write returns exact ownership");
  var after=target.CaptureEditorState(identity)!;
  Check(PortraitNativeLightColors.From(after.Composition)==desired,"both native triplets are read back");
  Check(before with{Composition=desired.Apply(before.Composition)} is var expected&&expected.ToJSON()==after.ToJSON(),
   "six-color write preserves every unrelated snapshot field");
  Check(fixture.PoseCalls==0&&fixture.PauseCalls==0&&fixture.FrameCalls==0&&fixture.AccentCalls==0,
   "six-color write never restarts animation or decoration");
  Check(fixture.State->BannerEntry.DirectionalLightingColorRed==31&&fixture.State->BannerEntry.AmbientLightingColorBlue==43,
   "only live editor-local working colors are synchronized");
  var calls=fixture.Calls;
  Check(!target.WriteLightColors(identity,before,new(1,2,3,4,5,6)).Applied&&fixture.Calls==calls,
   "stale full expected snapshot cannot modify native lighting");
  fixture.View->CameraYaw+=1;
  Check(!target.WriteLightColors(identity,after,new(1,2,3,4,5,6)).Applied&&fixture.Calls==calls,
   "foreign camera change blocks an otherwise matching six-color request");
  before=target.CaptureEditorState(identity)!;fixture.Framework=false;
  Check(!target.WriteLightColors(identity,before,new(1,2,3,4,5,6)).Applied&&fixture.Calls==calls,
   "six-color setter refuses native access off Framework");
  fixture.Framework=true;
  Check(!target.WriteLightColors(identity with{Generation=999},before,new(1,2,3,4,5,6)).Applied&&fixture.Calls==calls,
   "replaced character generation refuses six-color write");
  fixture.Paused=false;before=target.CaptureEditorState(identity)!;
  var cleanup=target.WriteLightColors(identity,before,new(11,12,13,21,22,23));
  Check(cleanup.Applied&&!fixture.Paused&&fixture.PoseCalls==0&&fixture.PauseCalls==0,
   "conditional color cleanup remains available after foreign animation playback resumes");
  before=target.CaptureEditorState(identity)!;calls=fixture.Calls;
  fixture.AfterDirectionalColor=()=>fixture.Data.Expression=99;
  var partial=target.WriteLightColors(identity,before,new(91,92,93,101,102,103));
  Check(!partial.Applied&&partial.Owned is{DirectionalRed:91,AmbientRed:21}&&partial.Actual?.Composition.Expression==99
    &&fixture.Calls==calls+1,"foreign fixed-state callback returns exact partial triplet ownership without a second native write");
  fixture.AfterDirectionalColor=null;
  var preserved=target.CaptureEditorState(identity)!;
  Check(target.WriteLightColors(identity,preserved,PortraitNativeLightColors.From(before.Composition)).Applied
    &&fixture.Data.Expression==99,"narrow compensation preserves another plugin's expression change");
  before=target.CaptureEditorState(identity)!;
  fixture.AfterAmbientColor=()=>throw new InvalidOperationException("after actual ambient write");
  partial=target.WriteLightColors(identity,before,new(111,112,113,121,122,123));
  Check(!partial.Applied&&partial.Owned is{DirectionalRed:111,AmbientBlue:123}
    &&partial.Actual?.Composition.AmbientBlue==123,"exception after native mutation reports actual snapshot and both owned triplets");
  fixture.AfterAmbientColor=null;
  preserved=target.CaptureEditorState(identity)!;
  Check(target.WriteLightColors(identity,preserved,PortraitNativeLightColors.From(before.Composition)).Applied,
    "failed native call leaves enough information for conditional restoration");
  before=target.CaptureEditorState(identity)!;calls=fixture.Calls;
  fixture.AfterDirectionalColor=()=>fixture.Editor->EditorState=null;
  partial=target.WriteLightColors(identity,before,new(131,132,133,141,142,143));
  Check(!partial.Applied&&partial.Owned is{DirectionalRed:131}&&partial.Actual is null&&fixture.Calls==calls+1,
    "native owner destruction retains opaque ownership without touching replaced allocation");
#else
  Check(false,"narrow native six-color writer is absent");
#endif
  OwnerLifecycleCases.Run(Check);
  PlaybackNativeCases.Run(Check);
  Console.WriteLine($"PortraitLightingNativeTests: {checks-failed} passed, {failed} failed");return failed==0?0:1;
 }
}
