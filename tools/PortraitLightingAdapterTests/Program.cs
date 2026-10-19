using FireGaze.PortraitPreview;

var checks=0;var failed=0;
void Check(bool value,string label){checks++;if(!value)failed++;Console.WriteLine((value?"PASS ":"FAIL ")+label);}
#if HAS_LIGHTING_ADAPTER
var backend=new Backend();var adapter=new PortraitLightingFitAdapter(backend);
Check(adapter.TryRead(out var initial),"adapter reads current immutable six channel state");
Check(adapter.TrySet(initial,[31,32,33,41,42,43]),"adapter accepts a conditional six field write");
Check(backend.State.Parameters.SequenceEqual(new[]{31,32,33,41,42,43}),"successful writes read back exact desired native values");
Check(!adapter.TrySet(initial,[1,2,3,4,5,6])&&backend.WriteCount==1,"stale expected state cannot issue a native write");
adapter.TryRead(out var expected);
var pending=adapter.CaptureAsync(expected);
Check(!pending.IsCompleted&&backend.CaptureCount==1,"real capture remains asynchronous until backend completion and framework publication");
var rejected=adapter.CaptureAsync(expected);
Check(rejected.IsFaulted&&backend.CaptureCount==1,"a second request cannot bypass a physical in flight readback");
backend.CompleteCapture();
Check(!pending.IsCompleted,"worker completion alone cannot publish a capture before framework validation");
adapter.Poll();
Check(pending.IsCompletedSuccessfully&&pending.Result.State.EditorIdentity==expected.EditorIdentity
    &&pending.Result.State.Parameters.SequenceEqual(expected.Parameters),"framework validates and publishes CPU pixels");
adapter.TryRead(out expected);var changedCapture=adapter.CaptureAsync(expected);
backend.State=backend.State with{FixedStateKey="new camera"};backend.CompleteCapture();adapter.Poll();
Check(changedCapture.IsFaulted,"camera change during readback rejects pixels");
backend.State=backend.State with{FixedStateKey="fixed"};adapter.TryRead(out expected);
var oldCapture=adapter.CaptureAsync(expected);adapter.CancelCapturePublication();
var blocked=adapter.CaptureAsync(expected);
Check(blocked.IsFaulted&&backend.CaptureCount==3,"cancellation retains physical in flight barrier");
backend.CompleteCapture();adapter.Poll();
Check(oldCapture.IsCanceled&&!adapter.HasPendingCapture,"late cancelled capture is drained without publication");
adapter.TryRead(out expected);backend.PartialWrite=true;backend.FailCleanup=true;
Check(!adapter.TrySet(expected,[51,52,53,61,62,63])&&adapter.HasPendingCleanup,"partial native write creates a retained cleanup obligation");
Check(!adapter.TryRead(out _),"unrestored partial sample cannot become a new fit baseline");
backend.FailCleanup=false;
Check(adapter.TryRead(out var restored)&&restored.Parameters.SequenceEqual(expected.Parameters)&&!adapter.HasPendingCleanup,
    "later framework read retries narrow cleanup and restores owned values");
adapter.TryRead(out expected);backend.PartialWrite=true;backend.FailCleanup=true;
Check(!adapter.TrySet(expected,[71,72,73,81,82,83]),"second partial write also retains its ownership");
backend.State=backend.State with{Parameters=new[]{199,72,73,41,42,43},FixedStateKey="foreign camera"};backend.FailCleanup=false;
Check(adapter.TryRead(out restored)&&restored.Parameters[0]==199&&restored.Parameters[1]==expected.Parameters[1]
    &&restored.FixedStateKey=="foreign camera","cleanup preserves externally replaced channels and fixed rendering fields");
adapter.SetSamplingHistorySuppressed(true);adapter.TryRead(out expected);
Check(adapter.TryCommitHistory(expected,expected.Parameters)&&backend.CommitCount==1&&backend.Suppressed,"commit accepts exactly current owner while suppression is active");
backend.State=backend.State with{EditorIdentity="replacement"};
Check(!adapter.TryCommitHistory(expected,expected.Parameters)&&backend.CommitCount==1,"stale owner never appends history");
backend.Framework=false;
Check(!adapter.TryRead(out _)&&!adapter.TrySet(expected,[1,2,3,4,5,6]),"native adapter refuses read and write off Framework");
backend.Framework=true;adapter.SetSamplingHistorySuppressed(false);
Check(!backend.Suppressed,"sampling suppression is released explicitly");
var sessionBackend=new Backend();var sessionAdapter=new PortraitLightingFitAdapter(sessionBackend);
var session=new PortraitLightingFitSession(sessionAdapter);
PortraitColorToneTarget.TryCreate([70,134,198],[70,134,198],[70,134,198],out var target);
Check(session.TryStart(target!,new[]{true,true}),"real session starts with the structured native adapter");
sessionBackend.PartialWrite=true;sessionBackend.FailCleanup=true;sessionBackend.CompleteCapture();sessionAdapter.Poll();session.Poll();
Check(session.Outcome==PortraitLightingFitSessionOutcome.AdapterFailed&&session.HasPendingCleanup
    &&sessionAdapter.HasPendingNativeWrite&&!session.HistorySuppressionReleased,
    "partial native probe retains session cleanup and history suppression");
Check(!session.TryStart(target!,new[]{true,true}),"retained partial native probe blocks a new session");
sessionBackend.FailCleanup=false;sessionAdapter.Poll();session.Poll();
Check(!session.HasPendingCleanup&&!sessionAdapter.HasPendingNativeWrite&&session.RollbackComplete
    &&session.HistorySuppressionReleased&&!sessionBackend.Suppressed,"framework retry settles both cleanup obligations before releasing history");
foreach(var externalAxis in new[]{0,4})
{
 var retryBackend=new Backend();var retryAdapter=new PortraitLightingFitAdapter(retryBackend);retryAdapter.TryRead(out var retryBaseline);
 retryBackend.PartialWrite=true;retryBackend.FailCleanup=true;
 Check(!retryAdapter.TrySet(retryBaseline,[51,52,53,61,62,63])&&retryAdapter.HasPendingCleanup,
     $"axis {externalAxis}: failed sample retains a cleanup lease");
 var external=retryBackend.State.Parameters.ToArray();external[externalAxis]=199;
 retryBackend.State=retryBackend.State with{Parameters=external,FixedStateKey="external camera"};
 retryBackend.FailCleanup=false;retryBackend.PartialWrite=true;retryAdapter.Poll();
 Check(retryAdapter.HasPendingCleanup,$"axis {externalAxis}: partial compensation retains cleanup across frames");
 Check(retryAdapter.TryRead(out var retryRestored)&&retryRestored.Parameters[externalAxis]==199
     &&Enumerable.Range(0,6).Where(axis=>axis!=externalAxis).All(axis=>retryRestored.Parameters[axis]==retryBaseline.Parameters[axis])
     &&retryRestored.FixedStateKey=="external camera"&&!retryAdapter.HasPendingCleanup,
     $"axis {externalAxis}: repeated partial compensation never adopts an external channel into the old lease");
}
var interruptedBackend=new Backend();var interruptedAdapter=new PortraitLightingFitAdapter(interruptedBackend);
var interruptedSession=new PortraitLightingFitSession(interruptedAdapter);
Check(interruptedSession.TryStart(target!,new[]{true,true}),"session partial-cleanup regression starts");
interruptedBackend.PartialWrite=true;interruptedBackend.FailCleanup=true;
interruptedBackend.CompleteCapture();interruptedAdapter.Poll();interruptedSession.Poll();
var externalSession=interruptedBackend.State.Parameters.ToArray();externalSession[1]=199;
interruptedBackend.State=interruptedBackend.State with{Parameters=externalSession,FixedStateKey="user changed camera"};
interruptedBackend.FailCleanup=false;interruptedBackend.PartialWrite=true;
interruptedAdapter.Poll();interruptedSession.Poll();
Check(interruptedBackend.State.Parameters[1]==199&&interruptedBackend.State.FixedStateKey=="user changed camera"
    &&!interruptedAdapter.HasPendingNativeWrite&&!interruptedSession.HasPendingCleanup
    &&interruptedSession.HistorySuppressionReleased&&!interruptedBackend.Suppressed,
    "session cancellation and adapter retries preserve external channels and release suppression together");
Console.WriteLine($"PortraitLightingAdapterTests: {checks-failed} passed, {failed} failed");
return failed==0?0:1;

sealed class Backend:IPortraitLightingFitBackend
{
 public bool Framework=true,Suppressed,PartialWrite,FailCleanup;public int WriteCount,CaptureCount,CommitCount;
 public PortraitLightingFitState State=new("editor","fixed",2,1,new[]{11,12,13,21,22,23},Enumerable.Repeat(new PortraitNativeLightRange(0,255,1),6).ToArray());
 TaskCompletionSource<IReadOnlyList<PortraitLightRgba>>? capture;
 public bool IsFrameworkThread=>Framework;
 public bool TryRead(out PortraitLightingFitState state){state=State;return true;}
 public bool IsEditorOwnerDestroyed(string owner)=>State.EditorIdentity!=owner;
 public PortraitLightingFitWriteResult Write(PortraitLightingFitState expected,IReadOnlyList<int> desired)
 {
  WriteCount++;if(FailCleanup&&!PartialWrite)return new(false,null);
  if(PartialWrite){PartialWrite=false;var partial=State.Parameters.ToArray();for(var i=0;i<3;i++)partial[i]=desired[i];State=State with{Parameters=partial};return new(false,State);}
  State=State with{Parameters=desired.ToArray()};return new(true,State);
 }
 public void SetHistorySuppressed(bool value)=>Suppressed=value;
 public Task<IReadOnlyList<PortraitLightRgba>> CaptureAsync(PortraitLightingFitState state){CaptureCount++;capture=new(TaskCreationOptions.RunContinuationsAsynchronously);return capture.Task;}
 public void CompleteCapture()=>capture!.SetResult(new[]{new PortraitLightRgba(1,2,3,255),new PortraitLightRgba(4,5,6,255)});
 public bool CommitHistory(PortraitLightingFitState expected,IReadOnlyList<int> baseline){CommitCount++;return true;}
}
#else
Check(false,"six-channel lighting adapter is absent");
return 1;
#endif
