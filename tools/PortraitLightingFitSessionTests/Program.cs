#if HAS_LIGHTING_SESSION
using FireGaze.PortraitPreview;
#endif

var checks=0;
var failures=new List<string>();
void Check(bool condition,string message){checks++;if(!condition)failures.Add(message);}
#if HAS_LIGHTING_SESSION
PortraitColorToneTarget Target()
{
    if(!PortraitColorToneTarget.TryCreate([70,134,198],[70,134,198],[70,134,198],out var target))throw new InvalidOperationException();
    return target!;
}
var mask=new[]{true,true};
void Complete(PortraitLightingFitSession session,TestLightingAdapter adapter)
{
    for(var i=0;i<20&&session.IsActive;i++){if(adapter.HasPending)adapter.CompleteCapture();session.Poll();}
}

// Seven real calibration captures and one independently rendered candidate precede the only commit.
var successAdapter=new TestLightingAdapter();
var success=new PortraitLightingFitSession(successAdapter);
Check(success.TryStart(Target(),mask),"stable editor starts a sampling transaction");
Check(success.IsActive&&successAdapter.Suppressed&&success.HasPendingCapture,"sampling suppresses ordinary history before capture");
Check(!success.TryStart(Target(),mask)&&successAdapter.CaptureCount==1,"start while running cannot queue another capture");
success.Poll();success.Poll();
Check(successAdapter.CaptureCount==1,"polling an incomplete frame never issues more captures");
Complete(success,successAdapter);
Check(success.Outcome==PortraitLightingFitSessionOutcome.Accepted&&!success.IsActive,"successful actual candidate ends the session");
Check(successAdapter.CaptureCount==8,"baseline plus six probes plus candidate are captured");
Check(successAdapter.CommitCount==1&&successAdapter.SuppressedAtCommit,"only accepted final edit commits once while transient sampling stays suppressed");
Check(!successAdapter.Suppressed&&success.HistorySuppressionReleased,"successful completion releases history suppression");
Check(success.Verification?.Accepted==true&&successAdapter.State.Parameters.SequenceEqual([105,105,105,106,106,106]),"actual validated native values remain applied");
Check(successAdapter.Writes.Count==13,"each probe starts from baseline and restores baseline before the next axis");
Check(successAdapter.Writes.Take(12).Where((_,i)=>i%2==1).All(p=>p.All(v=>v==100)),"calibration never accumulates unrelated axis changes");
success.Poll();success.Poll();
Check(successAdapter.CommitCount==1,"polling after completion cannot duplicate history");

// Real candidate capture, rather than the prediction, decides acceptance.
var badCandidateAdapter=new TestLightingAdapter{BadCandidateCapture=true};
var badCandidate=new PortraitLightingFitSession(badCandidateAdapter);
Check(badCandidate.TryStart(Target(),mask),"candidate rejection fixture starts");
Complete(badCandidate,badCandidateAdapter);
Check(badCandidate.Outcome==PortraitLightingFitSessionOutcome.CandidateRejected&&badCandidateAdapter.CommitCount==0,"worse real candidate does not create history");
Check(badCandidateAdapter.State.Parameters.All(v=>v==100)&&badCandidate.RollbackComplete&&!badCandidateAdapter.Suppressed,"candidate rejection restores owned lights and releases suppression");

var commitAdapter=new TestLightingAdapter{RejectCommit=true};
var commit=new PortraitLightingFitSession(commitAdapter);
Check(commit.TryStart(Target(),mask),"commit failure fixture starts");Complete(commit,commitAdapter);
Check(commit.Outcome==PortraitLightingFitSessionOutcome.CommitFailed&&commitAdapter.CommitCount==1,"failed commit is not retried");
Check(commitAdapter.State.Parameters.All(v=>v==100)&&!commitAdapter.Suppressed,"commit failure returns preview to the baseline lights");

// A camera change and a concurrent color edit cancel calibration without overwriting either external edit.
var externalAdapter=new TestLightingAdapter();var external=new PortraitLightingFitSession(externalAdapter);
Check(external.TryStart(Target(),mask),"external edit fixture starts");
externalAdapter.CompleteCapture();external.Poll();
Check(externalAdapter.State.Parameters[0]==116,"first axis is actively owned before interruption");
externalAdapter.ExternalChange(fixedKey:"camera changed",axis:1,value:120);
external.Poll();
Check(external.Outcome==PortraitLightingFitSessionOutcome.ExternalChange&&externalAdapter.CommitCount==0,"fixed-state or native color changes stop sampling");
Check(externalAdapter.State.FixedStateKey=="camera changed"&&externalAdapter.State.Parameters[0]==100&&externalAdapter.State.Parameters[1]==120,
    "rollback changes only still-owned light fields and preserves the external camera and color");
var externalWrites=externalAdapter.Writes.Count;
externalAdapter.CompleteCapture();external.Poll();
Check(externalAdapter.Writes.Count==externalWrites&&externalAdapter.CommitCount==0&&!external.HasPendingCapture,"late stale frame cannot write or commit");

var replacementAdapter=new TestLightingAdapter();var replacement=new PortraitLightingFitSession(replacementAdapter);
Check(replacement.TryStart(Target(),mask),"editor replacement fixture starts");replacementAdapter.CompleteCapture();replacement.Poll();
replacementAdapter.ExternalChange(identity:"new editor");var beforeReplacementWrites=replacementAdapter.Writes.Count;replacement.Poll();
Check(replacementAdapter.Writes.Count==beforeReplacementWrites&&replacement.Outcome==PortraitLightingFitSessionOutcome.ExternalChange,
    "replaced editor identity never receives an old rollback");

// Cancel/timeout return synchronously while retaining one pending capture to prevent readback pileups.
var cancelAdapter=new TestLightingAdapter();var cancelled=new PortraitLightingFitSession(cancelAdapter);
Check(cancelled.TryStart(Target(),mask),"cancel fixture starts");cancelled.Cancel();
Check(cancelled.Outcome==PortraitLightingFitSessionOutcome.Cancelled&&!cancelAdapter.Suppressed&&cancelled.HasPendingCapture,"cancel releases history without waiting for Present");
Check(!cancelled.TryStart(Target(),mask)&&cancelAdapter.CaptureCount==1,"cancelled in-flight readback blocks another transaction");
cancelAdapter.CompleteCapture();cancelled.Poll();
Check(!cancelled.HasPendingCapture&&cancelled.TryStart(Target(),mask),"a drained late frame permits a new transaction");cancelled.Cancel();
var time=new ManualLightingTime();var timeoutAdapter=new TestLightingAdapter();var timeout=new PortraitLightingFitSession(timeoutAdapter,time);
Check(timeout.TryStart(Target(),mask,new(CaptureTimeout:TimeSpan.FromSeconds(1))),"timeout fixture starts");
time.Advance(TimeSpan.FromSeconds(2));timeout.Poll();
Check(timeout.Outcome==PortraitLightingFitSessionOutcome.TimedOut&&!timeoutAdapter.Suppressed,"stalled readback times out without blocking Framework");
for(var i=0;i<10;i++)timeout.Poll();
Check(timeout.HasPendingCapture&&!timeout.TryStart(Target(),mask)&&timeoutAdapter.CaptureCount==1,"timeout never starts more true in-flight captures");
timeoutAdapter.CompleteCapture();timeout.Poll();Check(!timeout.HasPendingCapture,"completed timed-out readback is discarded");

var faultAdapter=new TestLightingAdapter();var fault=new PortraitLightingFitSession(faultAdapter);
Check(fault.TryStart(Target(),mask),"fault fixture starts");faultAdapter.FailCapture();fault.Poll();
Check(fault.Outcome==PortraitLightingFitSessionOutcome.CaptureFailed&&!faultAdapter.Suppressed,"faulted asynchronous CPU capture is observed and cleaned up");
var wrongFrameAdapter=new TestLightingAdapter();var wrongFrame=new PortraitLightingFitSession(wrongFrameAdapter);
Check(wrongFrame.TryStart(Target(),mask),"capture identity fixture starts");wrongFrameAdapter.CompleteCapture(stale:true);wrongFrame.Poll();
Check(wrongFrame.Outcome==PortraitLightingFitSessionOutcome.CaptureFailed&&wrongFrameAdapter.CommitCount==0,"capture metadata must match the requested fixed state");
var failedWriteAdapter=new TestLightingAdapter();var failedWrite=new PortraitLightingFitSession(failedWriteAdapter);
Check(failedWrite.TryStart(Target(),mask),"write rejection fixture starts");failedWriteAdapter.RejectNextWrite=true;
failedWriteAdapter.CompleteCapture();failedWrite.Poll();
Check(failedWrite.Outcome==PortraitLightingFitSessionOutcome.AdapterFailed&&failedWriteAdapter.CaptureCount==1,"failed conditional native write cannot lead to another capture");
Check(!failedWriteAdapter.Suppressed&&failedWriteAdapter.State.Parameters.All(v=>v==100),"failed probe write cleans up at the original state");

var noResponseAdapter=new TestLightingAdapter{IgnoreLightResponse=true};var noResponse=new PortraitLightingFitSession(noResponseAdapter);
Check(noResponse.TryStart(Target(),mask),"unresponsive native lighting fixture starts");Complete(noResponse,noResponseAdapter);
Check(noResponse.Outcome==PortraitLightingFitSessionOutcome.CalibrationRejected&&noResponse.FitFailure==PortraitLightingFitFailure.InsufficientResponse,
    "unresponsive real samples reject calibration");
Check(noResponseAdapter.CaptureCount==7&&noResponseAdapter.CommitCount==0,"rejected calibration does not render or commit a fake candidate");

var invalidMaskAdapter=new TestLightingAdapter();var invalidMask=new PortraitLightingFitSession(invalidMaskAdapter);
Check(!invalidMask.TryStart(Target(),[true])&&invalidMaskAdapter.CaptureCount==0&&!invalidMaskAdapter.Suppressed,"mask must match stable capture dimensions before starting");
Check(!invalidMask.TryStart(Target(),[false,false])&&invalidMaskAdapter.CaptureCount==0,"an empty subject selection cannot start");
Check(!invalidMask.TryStart(Target(),mask,new(CaptureTimeout:TimeSpan.Zero))&&invalidMaskAdapter.CaptureCount==0,"invalid session timing cannot schedule capture");
var maskOwnership=new[]{true,true};var maskAdapter=new TestLightingAdapter();var copiedMask=new PortraitLightingFitSession(maskAdapter);
Check(copiedMask.TryStart(Target(),maskOwnership),"caller mask ownership fixture starts");Array.Fill(maskOwnership,false);Complete(copiedMask,maskAdapter);
Check(copiedMask.Outcome==PortraitLightingFitSessionOutcome.Accepted,"caller reuse cannot change the mask during calibration");
var nonFrameworkAdapter=new TestLightingAdapter{IsFrameworkThread=false};var nonFramework=new PortraitLightingFitSession(nonFrameworkAdapter);
void WrongThread(Action action,string name){try{action();Check(false,name);}catch(InvalidOperationException){Check(true,name);}}
WrongThread(()=>nonFramework.TryStart(Target(),mask),"start requires Framework thread");
WrongThread(()=>nonFramework.Poll(),"poll requires Framework thread");
WrongThread(()=>nonFramework.Cancel(),"cancel requires Framework thread");
Check(nonFrameworkAdapter.CaptureCount==0&&nonFrameworkAdapter.Writes.Count==0,"wrong-thread entry points cannot touch native state");

// A transient cleanup failure must not leave the history observer suppressed forever or allow
// another transaction while suppression is still owned by the preceding one.
var cleanupAdapter=new TestLightingAdapter{SuppressionReleaseFailures=2};var cleanup=new PortraitLightingFitSession(cleanupAdapter);
Check(cleanup.TryStart(Target(),mask),"suppression cleanup fixture starts");cleanup.Cancel();
Check(!cleanup.HistorySuppressionReleased&&cleanupAdapter.Suppressed,"a failed suppression release is exposed");
cleanupAdapter.CompleteCapture();cleanup.Poll();
Check(!cleanup.HistorySuppressionReleased,"terminal polling retries a transient suppression-release failure");
cleanup.Poll();
Check(cleanup.HistorySuppressionReleased&&!cleanupAdapter.Suppressed,"terminal polling eventually releases history suppression");

var rollbackAdapter=new TestLightingAdapter();var rollback=new PortraitLightingFitSession(rollbackAdapter);
Check(rollback.TryStart(Target(),mask),"retryable rollback fixture starts");rollbackAdapter.CompleteCapture();rollback.Poll();
rollbackAdapter.RejectWrites=2;rollback.Cancel();
Check(!rollback.RollbackComplete&&rollbackAdapter.Suppressed,"failed native rollback retains transient-history suppression");
Check(!rollback.TryStart(Target(),mask),"new calibration cannot adopt an un-restored probe as its baseline");
rollbackAdapter.CompleteCapture();rollback.Poll();
Check(rollback.RollbackComplete&&!rollbackAdapter.Suppressed&&rollbackAdapter.State.Parameters.All(v=>v==100),
    "terminal polling retries conditional restore before releasing history");
var unreadableAdapter=new TestLightingAdapter();var unreadable=new PortraitLightingFitSession(unreadableAdapter);
Check(unreadable.TryStart(Target(),mask),"temporarily unreadable editor cleanup fixture starts");unreadableAdapter.CompleteCapture();unreadable.Poll();
unreadableAdapter.Readable=false;unreadable.Cancel();
Check(!unreadable.RollbackComplete&&unreadableAdapter.Suppressed,"temporarily unreadable original editor keeps cleanup ownership");
unreadableAdapter.Readable=true;unreadableAdapter.CompleteCapture();unreadable.Poll();
Check(unreadable.RollbackComplete&&unreadableAdapter.State.Parameters.All(v=>v==100),"restored editor readability allows pending conditional cleanup");
var destroyedAdapter=new TestLightingAdapter();var destroyed=new PortraitLightingFitSession(destroyedAdapter);
Check(destroyed.TryStart(Target(),mask),"closed editor owner fixture starts");destroyedAdapter.CompleteCapture();destroyed.Poll();
destroyedAdapter.Readable=false;destroyedAdapter.OwnerDestroyed=true;var destroyedWrites=destroyedAdapter.Writes.Count;destroyed.Cancel();
Check(destroyed.RollbackComplete&&!destroyed.HasPendingCleanup&&!destroyedAdapter.Suppressed&&destroyedAdapter.Writes.Count==destroyedWrites,
    "proven original editor destruction releases cleanup without writing to any editor");

#else
Check(false,"nonblocking rendered-light sampling session is not implemented");
#endif
Console.WriteLine($"PortraitLightingFitSessionTests: {checks-failures.Count} passed, {failures.Count} failed");
foreach(var failure in failures)Console.Error.WriteLine("FAIL: "+failure);
return failures.Count==0?0:1;

#if HAS_LIGHTING_SESSION
sealed class ManualLightingTime:TimeProvider
{
    private DateTimeOffset current=DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow()=>current;
    public void Advance(TimeSpan elapsed)=>current+=elapsed;
}

sealed class TestLightingAdapter:IPortraitLightingFitSessionAdapter
{
    private TaskCompletionSource<PortraitLightingFitCapture>? pending;
    private PortraitLightingFitState? requested;
    public bool IsFrameworkThread{get;set;}=true;
    public PortraitLightingFitState State{get;private set;}=new("editor one","fixed one",2,1,
        [100,100,100,100,100,100],Enumerable.Repeat(new PortraitNativeLightRange(0,255,1),6).ToArray());
    public bool Suppressed{get;private set;}
    public bool SuppressedAtCommit{get;private set;}
    public int CaptureCount{get;private set;}
    public int CommitCount{get;private set;}
    public bool BadCandidateCapture{get;set;}
    public bool RejectCommit{get;set;}
    public bool RejectNextWrite{get;set;}
    public int RejectWrites{get;set;}
    public bool Readable{get;set;}=true;
    public bool OwnerDestroyed{get;set;}
    public bool IgnoreLightResponse{get;set;}
    public int SuppressionReleaseFailures{get;set;}
    public List<int[]> Writes{get;}=[];
    public bool HasPending=>pending is not null;
    public bool TryRead(out PortraitLightingFitState state){state=State;return Readable;}
    public bool IsEditorOwnerDestroyed(string editorIdentity)=>OwnerDestroyed;
    public bool TrySet(PortraitLightingFitState expected,IReadOnlyList<int> desired)
    {
        if(RejectNextWrite){RejectNextWrite=false;return false;}
        if(RejectWrites>0){RejectWrites--;return false;}
        if(!Same(expected,State))return false;
        State=State with{Parameters=desired.ToArray()};Writes.Add(desired.ToArray());return true;
    }
    public void SetSamplingHistorySuppressed(bool suppressed)
    {
        if(!suppressed&&SuppressionReleaseFailures>0){SuppressionReleaseFailures--;throw new InvalidOperationException("temporary release failure");}
        Suppressed=suppressed;
    }
    public Task<PortraitLightingFitCapture> CaptureAsync(PortraitLightingFitState expected)
    {
        if(pending is not null)throw new InvalidOperationException("capture pileup");
        if(!Same(expected,State))throw new InvalidOperationException("stale capture request");
        CaptureCount++;requested=expected;pending=new(TaskCreationOptions.RunContinuationsAsynchronously);return pending.Task;
    }
    public bool TryCommitHistory(PortraitLightingFitState expected,IReadOnlyList<int> baseline)
    {
        CommitCount++;SuppressedAtCommit=Suppressed;return !RejectCommit&&Same(expected,State)&&baseline.All(v=>v==100);
    }
    public void ExternalChange(string? fixedKey=null,string? identity=null,int axis=-1,int value=0)
    {
        var p=State.Parameters.ToArray();if(axis>=0)p[axis]=value;
        State=State with{EditorIdentity=identity??State.EditorIdentity,FixedStateKey=fixedKey??State.FixedStateKey,Parameters=p};
    }
    public void CompleteCapture(bool stale=false)
    {
        if(pending is null||requested is null)throw new InvalidOperationException();
        var frame=requested;var p=frame.Parameters;
        PortraitLightRgba[] pixels=IgnoreLightResponse?[new(50,50,50,255),new(100,100,100,255)]:[
            new(p[0]-50,p[1]-50,p[2]-50,255),new(p[3],p[4],p[5],255)];
        if(BadCandidateCapture&&CaptureCount==8)pixels=[new(40,40,40,255),new(90,90,90,255)];
        if(stale)frame=frame with{FixedStateKey="stale capture"};
        var complete=pending;pending=null;requested=null;complete.SetResult(new(frame,pixels));
    }
    public void FailCapture()
    {
        var complete=pending??throw new InvalidOperationException();pending=null;requested=null;complete.SetException(new InvalidOperationException("readback failed"));
    }
    private static bool Same(PortraitLightingFitState a,PortraitLightingFitState b)=>a.EditorIdentity==b.EditorIdentity&&a.FixedStateKey==b.FixedStateKey
        &&a.Width==b.Width&&a.Height==b.Height&&a.Parameters.SequenceEqual(b.Parameters)&&a.Ranges.SequenceEqual(b.Ranges);
}
#endif
