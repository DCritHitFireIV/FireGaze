#if HAS_FEATURE_BACKEND
using FireGaze.PortraitPreview;

internal static class LifecycleCases
{
 public static void Run(Action<bool,string> check)
 {
  PortraitColorToneTarget.TryCreate([70,134,198],[70,134,198],[70,134,198],out var goal);
  var region=new PortraitSampleRegion(0,0,1,1);
  using(var feature=new PortraitPreviewFeature(false))
  {
   feature.Tick();feature.StartLightingFit(goal!,region);feature.Tick();
   check(feature.CaptureCount==0 && feature.NativeWrites==0 && feature.HistoryCount==1 && !feature.Blocked,
    "capture isolation blocks fitting without GPU, native RGB or history mutations");
   check(feature.LightingFit is null && feature.Errors.Count==1,
    "disabled image tools publish no live source and explain why a fitting request was declined");
  }
  var captureTime=TimeSpan.Zero;
  using(var feature=new PortraitPreviewFeature(captureClock:()=>captureTime))
  {
   captureTime=TimeSpan.FromSeconds(59);
   feature.StartLightingFit(goal!,region);
   feature.CompleteCapture();
   captureTime=TimeSpan.FromSeconds(60);feature.Tick();
   check(feature.CaptureCount==1 && feature.NativeWrites==0 && feature.HistoryCount==1,
    "lease expiry rejects a completed baseline before it can queue a new native probe or GPU capture");
   check(!feature.Blocked && !feature.IsHistorySuppressed
     && feature.LightingFit?.Outcome==PortraitLightingFitSessionOutcome.Cancelled,
    "expired baseline fitting ends as cancellation after its physical capture has drained");
  }
  captureTime=TimeSpan.Zero;
  using(var feature=new PortraitPreviewFeature(captureClock:()=>captureTime))
  {
   captureTime=TimeSpan.FromSeconds(59);
   feature.StartLightingFit(goal!,region);feature.CompleteCapture();feature.Tick();
   captureTime=TimeSpan.FromSeconds(60);feature.Tick();
   check(feature.CaptureCount==2 && feature.NativeColors.All(value=>value==100)
     && feature.Blocked && !feature.IsHistorySuppressed && feature.NativeWrites==2,
    "lease expiry cancels an unchanged pending probe and restores RGB while retaining its physical GPU barrier");
   feature.Tick();feature.Tick();
   check(feature.CaptureCount==2 && feature.NativeWrites==2 && feature.HistoryCount==1,
    "expired pending probe polling cannot continue calibration or repeat a completed rollback");
   feature.CompleteCapture();feature.Tick();
   check(!feature.Blocked && feature.CaptureCount==2 && feature.HistoryCount==1,
    "an expired pending probe drains its final physical task without scheduling another probe");
  }
  captureTime=TimeSpan.Zero;
  using(var feature=new PortraitPreviewFeature(captureClock:()=>captureTime))
  {
   captureTime=TimeSpan.FromSeconds(59);
   feature.StartLightingFit(goal!,region);feature.CompleteCapture();feature.Tick();
   check(feature.CaptureCount==2 && feature.NativeColors[0]==116,
    "lease probe fixture reaches one live isolated RGB write with its physical capture pending");
   feature.ChangeAmbientBlue(77);
   captureTime=TimeSpan.FromSeconds(60);feature.Tick();
   check(feature.NativeColors.SequenceEqual(new[]{100,100,100,100,100,77})
     && feature.Blocked && !feature.IsHistorySuppressed && feature.NativeWrites==2,
    "lease expiry conditionally restores owned RGB while preserving the newer player color and pending GPU barrier");
   feature.Tick();feature.Tick();
   check(feature.CaptureCount==2 && feature.NativeWrites==2 && feature.HistoryCount==1,
    "expired probe polling neither queues another sample nor writes duplicate compensation or history");
   feature.CompleteCapture();feature.Tick();
   check(!feature.Blocked && feature.NativeColors.SequenceEqual(new[]{100,100,100,100,100,77})
     && feature.CaptureCount==2 && feature.HistoryCount==1,
    "late expired probe result drains without publication restart or history commit");
  }
  captureTime=TimeSpan.Zero;
  using(var feature=new PortraitPreviewFeature(captureClock:()=>captureTime))
  {
   captureTime=TimeSpan.FromSeconds(59);
   feature.StartLightingFit(goal!,region);
   for(var i=0;i<7;i++){feature.CompleteCapture();feature.Tick();}
   check(feature.CaptureCount==8 && feature.NativeColors.SequenceEqual(new[]{105,105,105,106,106,106}),
    "lease candidate fixture reaches independently captured proposed RGB before committing history");
   feature.CompleteCapture();captureTime=TimeSpan.FromSeconds(60);feature.Tick();
   check(feature.NativeColors.All(value=>value==100) && feature.HistoryCount==1
     && feature.LightingFit?.Outcome==PortraitLightingFitSessionOutcome.Cancelled && !feature.Blocked,
    "lease expiry rejects even a completed candidate and restores RGB instead of committing a final edit");
   feature.Tick();feature.Tick();
   check(feature.CaptureCount==8 && feature.HistoryCount==1 && feature.LightingFit is null,
    "expired candidate remains stopped after cleanup and cannot silently resume fitting");
  }
  using(var feature=new PortraitPreviewFeature())
  {
   feature.StartLightingFit(goal!,region);feature.CompleteCapture();feature.Tick();
   feature.Readable=false;feature.Tick();
   check(feature.PreviewActive && feature.SaveBlocked && feature.IsHistorySuppressed,
    "Framework preview validation keeps save protection and sampling cleanup during temporary unreadability");
   feature.Readable=true;feature.Tick();
   check(feature.NativeColors.All(value=>value==100) && feature.PreviewActive && feature.SaveBlocked && !feature.IsHistorySuppressed,
    "same-owner recovery re-enables paused capture and completes retained RGB restoration");
   feature.CompleteCapture();feature.Tick();
   check(!feature.Blocked && feature.CancelForEdit(),"recovered preview can drain its physical capture and close normally");
  }
  using(var feature=new PortraitPreviewFeature())
  {
   feature.Tick();var expected=feature.LightingFit!.FixedStateKey;
   feature.ChangeCamera();feature.StartLightingFit(goal!,region,expected);
   check(feature.CaptureCount==0 && feature.NativeWrites==0 && feature.HistoryCount==1 && !feature.Blocked,
    "UI start with stale selected-image fixed state performs no native/GPU/history writes");
   check(feature.Errors.Count==1,"stale selected-image start explains that the portrait has changed");
  }
  using(var feature=new PortraitPreviewFeature())
  {
   feature.Tick();var expected=feature.LightingFit!.FixedStateKey;
   feature.StartLightingFit(goal!,region,expected);
   check(feature.CaptureCount==1 && feature.IsHistorySuppressed && feature.HistoryCount==1,
    "UI start accepts the fixed state of the freshly selected image");
  }
  using(var feature=new PortraitPreviewFeature())
  {
   feature.Tick();feature.StartLightingFit(goal!,region,"");
   check(feature.CaptureCount==0 && feature.NativeWrites==0 && feature.HistoryCount==1,
    "empty expected fixed state is rejected rather than bypassing UI validation");
  }
  using(var feature=new PortraitPreviewFeature())
  {
   feature.Tick();
   check(feature.LightingFit is {CanStart:true,Busy:false},"actual Feature publishes readiness from fresh paused native source");
   feature.StartLightingFit(goal!,region);
   check(feature.IsHistorySuppressed && feature.Blocked && feature.CaptureCount==1,"Feature suppresses history before baseline capture");
   for(var i=0;i<20&&feature.Blocked;i++){feature.CompleteCapture();feature.Tick();}
   check(feature.LightingFit?.Outcome==PortraitLightingFitSessionOutcome.Accepted,"actual Feature, production backend, real adapter and session accept calibrated lights");
   check(feature.CaptureCount==8 && feature.HistoryCount==2,"eight physical samples create one final edit after the existing baseline");
   check(feature.HistoryLabels.Count(label=>label=="调整灯光色彩")==1,"Feature publishes exactly one final user-facing lighting record");
   check(feature.NativeColors.SequenceEqual(new[]{105,105,105,106,106,106}),"Feature preserves readback-validated native RGB");
   check(feature.HistoryBaselineMatches && !feature.IsHistorySuppressed && !feature.Blocked,"successful commit advances observer baseline and releases both barriers");
   var count=feature.HistoryCount;feature.Tick();feature.Tick();
   check(feature.HistoryCount==count,"idle Feature polling does not duplicate the final edit");
  }
  using(var feature=new PortraitPreviewFeature())
  {
   feature.StartLightingFit(goal!,region);feature.CompleteCapture();feature.Tick();
   check(feature.NativeColors[0]==116,"actual Feature has applied the first isolated native probe");
   check(!feature.CancelForEdit() && feature.NativeColors.All(value=>value==100) && !feature.IsHistorySuppressed,
    "editing cancels and immediately restores probe while retaining physical GPU barrier");
   feature.CompleteCapture();feature.Tick();
   check(feature.CancelForEdit() && feature.HistoryCount==1,"late readback drains and cancelled probes add no history");
  }
  using(var feature=new PortraitPreviewFeature())
  {
   feature.PartialNextWrite=true;feature.RejectCleanup=true;
   feature.StartLightingFit(goal!,region);feature.CompleteCapture();feature.Tick();
   check(feature.Blocked && feature.IsHistorySuppressed && feature.NativeColors[0]==116,
    "Feature retains partially changed native RGB and suppression after failed cleanup");
   check(feature.RawSliderValues.All(value=>value==100),"partial native write can leave sliders at old RGB");
   feature.RejectCleanup=false;feature.Tick();
   check(!feature.Blocked && !feature.IsHistorySuppressed && feature.NativeColors.All(value=>value==100),
    "cleanup tolerates unsynchronized old slider values and restores retained owned RGB");
  }
  using(var feature=new PortraitPreviewFeature())
  {
   feature.StartLightingFit(goal!,region);feature.CompleteCapture();feature.Tick();
   feature.PlayAndMoveCamera();feature.Tick();
   check(feature.NativeColors.All(value=>value==100) && !feature.Paused && feature.CameraPitch==9,
    "Feature can compensate RGB after playback resumes without restoring old pose or camera");
   feature.CompleteCapture();feature.Tick();
   check(!feature.Blocked && !feature.IsHistorySuppressed,"playing cancellation settles after readback release");
  }
  using(var feature=new PortraitPreviewFeature())
  {
   feature.Readable=false;feature.Tick();feature.StartLightingFit(goal!,region);
   check(feature.CaptureCount==0 && feature.Errors.Count==1,"unreadable initial source starts no native or GPU transaction");
   feature.Readable=true;feature.Paused=false;feature.StartLightingFit(goal!,region);
   check(feature.CaptureCount==0,"playing source cannot start a new fitting transaction");
  }
 }
}
#endif
