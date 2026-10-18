using FireGaze.PortraitPreview;
unsafe class Program {
 static int pass,fail;static void Check(bool ok,string n){if(ok)pass++;else fail++;Console.WriteLine((ok?"PASS ":"FAIL ")+n);}
 static int Main(){
  var a=new PortraitEditorSnapshot {GameBuild="7.57",EntryPoint=1,Customize=Enumerable.Range(0,26).Select(i=>(byte)i).ToArray(),Look=new(Enumerable.Range(0,13).Select(i=>new PortraitPiece((uint)i,1,2)).ToArray(),false,true,true),Composition=new(){CameraPosition=new(1.234567f,2,3,4),CameraTarget=new(5,6,7,8),CameraYaw=1.4f,CameraPitch=2.3f,CameraDistance=2.4f,CameraZoomNormalized=.75f,ImageRotation=-10,CameraZoom=10,BannerTimeline=88,AnimationProgress=.345f,Expression=7,HeadDirection=new(1,2),EyeDirection=new(3,4),DirectionalRed=1,DirectionalGreen=2,DirectionalBlue=3,DirectionalBrightness=4,DirectionalVerticalAngle=-45,DirectionalHorizontalAngle=90,AmbientRed=4,AmbientGreen=5,AmbientBlue=6,AmbientBrightness=7,Background=30,Frame=40,Decoration=50,PoseClassJob=23,Paused=true}};
  var b=PortraitEditorSnapshot.FromJSON(a.ToJSON());
  Check(b!=null && b.SameForHistory(a),"complete snapshot JSON roundtrip retains every presentation field");
  Check(!a.ToJSON().Contains("Editor")&&!a.ToJSON().Contains("Address")&&!a.ToJSON().Contains("CharacterId"),"portable JSON excludes native identity and pointers");
  Check(b!.Composition.CameraPosition.X==1.234567f,"camera snapshot retains full float precision");
  b.Customize[6]=99;b.Look.Pieces[0]=default;
  Check(a.Customize[6]==6 && a.Look.Pieces[0].ItemID==0 && a.Look.Pieces[0].Stain0==1,"deserialization creates independent customize and equipment arrays");
  Check(!a.IsCompatibleWith(a with {GameBuild="7.58"})&&!a.IsCompatibleWith(a with {EntryPoint=2}),"game build and editor entrypoint isolate history");
  foreach(var index in new[]{0,1,4}){var c=a.Copy();c.Customize[index]++;Check(!a.IsCompatibleWith(c),"race sex tribe changes are rejected "+index);}
  var playing=a with {Composition=a.Composition with {Paused=false}};
  Check(playing.SameForHistory(playing with {Composition=playing.Composition with {AnimationProgress=.9f}}),"passive animation progress never creates history edits");
  Check(!a.SameForHistory(a with {Composition=a.Composition with {AnimationProgress=.9f}}),"paused position edits are recorded");
  Check(!a.SameForHistory(a with {Composition=a.Composition with {Frame=41}}),"native design edits are recorded");
  Check(!a.SameForHistory(a with {Composition=a.Composition with {Paused=false}}),"playback toggle is recorded");
  Check(PortraitEditorSnapshot.FromJSON("{")==null && PortraitEditorSnapshot.FromJSON("null")==null,"malformed snapshots fail closed");
  Check(!(a with{Customize=new byte[25]}).IsValid && !(a with{Version=2}).IsValid,"invalid version and customize length are rejected");
  Check(!(a with{Composition=a.Composition with{CameraDistance=float.NaN}}).IsValid,"nonfinite composition values are rejected");
  var target=typeof(PortraitLook).Assembly.GetType("FireGaze.PortraitPreview.NativePortraitTarget");
  Check(target?.GetMethod("CaptureEditorState")!=null && target.GetMethod("ApplyEditorState")!=null,"complete native editor capture and restore entrypoints exist");
  Check(typeof(PortraitComposition).GetProperty("HeadFacingCamera")!=null && typeof(PortraitComposition).GetProperty("EyesFacingCamera")!=null,"native camera tracking toggles are included in complete snapshots");
  GazeCaptureTests.Run(Check);
  using(var beautyFixture=new EditorFixture {Paused=true,RestartPoseOnSeek=true})
  using(var beautyTarget=new NativePortraitTarget(beautyFixture,_=>true,_=>{})) {
   beautyFixture.Data.AnimationProgress=.001f;
   beautyTarget.TryResolve(out var beautyIdentity,out _,out _);
   var beautyBefore=beautyTarget.CaptureEditorState(beautyIdentity)!;
   var beautyAfter=beautyBefore.Copy();beautyAfter.Customize[6]=8;
   Check(beautyTarget.ApplyEditorState(beautyIdentity,beautyAfter)
    && beautyTarget.CaptureEditorState(beautyIdentity)!.Composition.AnimationProgress==.001f
    && beautyFixture.PoseCalls==0 && beautyFixture.PauseCalls==0,
    "beauty-only edit preserves paused pose without restarting the native timeline");
   var wardrobe=beautyTarget.CaptureEditorState(beautyIdentity)!.Copy();wardrobe.Look.Pieces[3]=new(778,4,5);
   Check(beautyTarget.ApplyEditorState(beautyIdentity,wardrobe) && beautyFixture.PoseCalls==0
    && beautyTarget.CaptureEditorState(beautyIdentity)!.Composition.AnimationProgress==.001f,
    "wardrobe-only history restore preserves the current paused pose");
  }
  using var f=new EditorFixture();using var t=new NativePortraitTarget(f,_=>true,_=>{});t.SnapshotGameBuild="7.57";
  Check(t.TryResolve(out var id,out _,out _),"isolated editor resolves for full state capture");
  f.View->IsAnimationPauseStatePending=true;
  Check(t.CaptureEditorState(id)==null,"a native pose awaiting its requested pause is not sampled as a history state");
  f.View->IsAnimationPauseStatePending=false;
  using(var poseFixture=new EditorFixture {Paused=true,UseNativeTimelineBias=true})
  using(var poseTarget=new NativePortraitTarget(poseFixture,_=>true,_=>{})) {
   poseTarget.TryResolve(out var poseIdentity,out _,out _);
   var poseBefore=poseTarget.CaptureEditorState(poseIdentity)!;
   var pose=poseBefore with {Composition=poseBefore.Composition with {BannerTimeline=2,AnimationProgress=.39900002f}};
   Check(poseTarget.ApplyEditorState(poseIdentity,pose)
    && poseTarget.CaptureEditorState(poseIdentity)!.Composition.AnimationProgress==.39900002f
    && poseFixture.State->BannerEntry.AnimationProgress==.39900002f,
    "a captured paused pose is restored exactly through the native timeline time bias");
   Check(poseTarget.ApplyEditorState(poseIdentity,poseBefore) && poseTarget.ApplyEditorState(poseIdentity,pose)
    && poseTarget.CaptureEditorState(poseIdentity)!.Composition.AnimationProgress==.39900002f,
    "repeated timeline history restore never accumulates the native millisecond subtraction");
  }
  using(var designFixture=new EditorFixture {Paused=true,RestartPoseOnSeek=true})
  using(var designTarget=new NativePortraitTarget(designFixture,_=>true,_=>{})) {
   designTarget.TryResolve(out var designIdentity,out _,out _);
   var designBefore=designTarget.CaptureEditorState(designIdentity)!;
   var design=designBefore with {Composition=designBefore.Composition with {CameraYaw=7,CameraZoom=160,
    ImageRotation=23,Expression=8,HeadDirection=new(1,2),EyeDirection=new(3,4),Background=31,Frame=41,Decoration=51,
    DirectionalRed=5,DirectionalGreen=6,DirectionalBlue=7,DirectionalBrightness=8,DirectionalVerticalAngle=9,DirectionalHorizontalAngle=10,
    AmbientRed=11,AmbientGreen=12,AmbientBlue=13,AmbientBrightness=14}};
   Check(designTarget.ApplyEditorState(designIdentity,design)
    && designFixture.PoseCalls==0 && designFixture.Data.AnimationProgress==.25f
    && designTarget.CaptureEditorState(designIdentity)!.SameForHistory(design),
    "camera lighting expression and decoration restore without restarting an unchanged paused timeline");
   designFixture.Actor->LookAt.BannerCameraFollowFlag=(FFXIVClientStructs.FFXIV.Client.Game.Character.LookAtContainer.BannerCameraFollowFlags)0x80;
   var following=design with {Composition=design.Composition with {HeadFacingCamera=true,EyesFacingCamera=true}};
   Check(designTarget.ApplyEditorState(designIdentity,following)
    && (byte)designFixture.Actor->LookAt.BannerCameraFollowFlag==0x83
    && designTarget.CaptureEditorState(designIdentity)!.Composition.HeadFacingCamera
    && designTarget.CaptureEditorState(designIdentity)!.Composition.EyesFacingCamera,
    "restoring camera-follow toggles updates actual head and eye flags while preserving unrelated bits");
   var headOff=following with {Composition=following.Composition with {HeadFacingCamera=false}};
   Check(designTarget.ApplyEditorState(designIdentity,headOff)
    && (byte)designFixture.Actor->LookAt.BannerCameraFollowFlag==0x82
    && !designTarget.CaptureEditorState(designIdentity)!.Composition.HeadFacingCamera
    && designTarget.CaptureEditorState(designIdentity)!.Composition.EyesFacingCamera,
    "disabling head follow preserves enabled eye follow in the native portrait actor");
  }
  var original=t.Capture(id)!;var full=t.CaptureEditorState(id)!;
  Check(full.GameBuild=="7.57" && full.Composition.BannerTimeline==12 && full.Composition.Expression==7 && full.Composition.Frame==40 && full.Composition.Decoration==50,"native capture includes current timeline expression and design");
  Check(full.Composition.CameraPosition.X==1.234567f,"native capture reads full precision camera");
  var changed=full.Copy();changed.Customize[6]=8;changed.Look.Pieces[3]=new(778,4,5);changed=changed with{Composition=changed.Composition with{BannerTimeline=88,AnimationProgress=.9f,Frame=41,Decoration=51,CameraYaw=5,Paused=true,CharacterVisible=false}};
  Check(t.ApplyEditorState(id,changed),"full editor history restores current isolated view");
  Check(f.VisibilityCalls>0 && !f.View->CharacterVisible,"complete restore uses native character visibility API");
  Check(f.View->ModelData.CustomizeData.Hairstyle==8 && f.View->PortraitCharacterData.CustomizeData.Hairstyle==8 && f.Actor->DrawData.CustomizeData.Hairstyle==8,"customize is aligned between view caches and isolated actor");
  Check(f.Data.BannerTimeline==88 && f.Data.AnimationProgress==.9f && f.State->BannerEntry.BannerTimeline==88 && f.State->BannerEntry.BannerFrame==41 && f.State->BannerEntry.BannerDecoration==51 && f.Paused && f.View->CameraYaw==5,"native composition and temporary editor copy update together");
  Check(t.RestoreOriginal(id,original) && f.View->ModelData.CustomizeData.Hairstyle==4 && f.Actor->DrawData.CustomizeData.Hairstyle==4,"ordinary close restores original customization along with wardrobe");
  Check(f.View->CameraYaw==5 && f.Data.BannerTimeline==88,"ordinary close preserves latest native composition");
  var beforeFailure=t.CaptureEditorState(id)!;f.RejectCustomize=true;
  Check(!t.ApplyEditorState(id,changed) && t.CaptureEditorState(id)!.SameForHistory(beforeFailure),"rejected native customization rolls back previously applied wardrobe");f.RejectCustomize=false;
  var calls=f.Calls;f.Framework=false;
  Check(t.CaptureEditorState(id)==null && !t.ApplyEditorState(id,changed) && !t.ApplyCustomization(id,changed.Customize,false) && f.Calls==calls,"full state paths never touch native functions on workers");f.Framework=true;
  var bad=changed.Copy();bad.Customize[0]++;calls=f.Calls;
  Check(!t.ApplyEditorState(id,bad) && f.Calls==calls,"incompatible snapshot is rejected before native writes");
  f.ThrowPose=true;beforeFailure=t.CaptureEditorState(id)!;
  var poseChange=changed with {Composition=changed.Composition with {BannerTimeline=89,CameraYaw=6}};
  Check(!t.ApplyEditorState(id,poseChange) && t.CaptureEditorState(id)!.SameForHistory(beforeFailure),"native pose exception rolls back complete current temporary state");
  t.ApplyEditorState(id,changed);f.ThrowCustomize=true;bool closed=false;try{closed=t.RestoreOriginal(id,original);}catch{}
  Check(!closed && t.LastError?.Contains("customize failure")==true,"native customization failure while closing is contained and preserves teardown error");
  f.ReplaceOnPose=true;calls=f.FrameCalls+f.AccentCalls;
  Check(!t.ApplyEditorState(id,poseChange) && f.FrameCalls+f.AccentCalls==calls,"reentrant editor replacement stops immediately without frame or rollback writes");
  Console.WriteLine($"{pass}/{pass+fail} editor snapshot cases passed");return fail==0?0:1;
 }
}
