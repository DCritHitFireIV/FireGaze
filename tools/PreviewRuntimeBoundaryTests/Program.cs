using FireGaze.LocalPreview.Runtime;

var tests = new (string, Action)[] {
    ("rejected input restores preview and executes once", Rejected),
    ("queued input does not count as started", Queued),
    ("nested actual execution commits actual intent once", NestedAccepted),
    ("native exception restores preview without retry", Throws),
    ("preflight temporarily suspends then restores", Preflight),
    ("accepted cleanup failure never retries native input", CleanupThrows),
    ("guarded accepted cleanup preserves native true", GuardedAccepted),
    ("guarded suspension failure still executes original once", GuardedSuspend),
    ("guarded inspection preserves completed result", GuardedInspect),
    ("manual safe exit restores original presentation", RestoreManual),
    ("busy exit never restores stale posture or equipment", RestoreBusy),
    ("new actor exit never touches its fields", RestoreNewActor),
    ("same reused object never clears native winner", WinnerPreserved),
    ("pending mount clears only the hidden old preview", PendingMount),
    ("changed native sibling never restores stale baseline", SiblingPreserved),
    ("pending child clears old projected object only", PendingChild),
    ("external rejection restores suspended preview", ExternalRejected),
    ("external accepted same id preserves new native presentation", ExternalSameId),
    ("external transaction suppresses nested native handoff", ExternalNested),
    ("external stale binding never restores preview", ExternalStaleBinding),
    ("external invalidation cannot complete an old token", ExternalInvalidated),
    ("external cleanup failure consumes token and releases handoff", ExternalCleanupThrows),
    ("manual exit restores only a minion actually displaced by preview setup", ImplicitMinionRestore),
    ("busy accepted and incompatible previews do not resurrect displaced minion", ImplicitMinionBlocked),
    ("new native minion wins over displaced baseline", ImplicitMinionWinner),
    ("accepted winner timeline is never replayed over", AcceptedTimeline),
    ("old territory cannot restore baseline before next update", RestoreOldTerritory),
};
var failed = 0;
foreach (var (name, test) in tests) { try { test(); Console.WriteLine("PASS " + name); } catch(Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
Console.WriteLine($"{tests.Length - failed}/{tests.Length} passed");
return failed == 0 ? 0 : 1;

static void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"expected {expected}; got {actual}"); }
static void Rejected() {
 var bridge = new NativeInputHandoff<int>(); var lease = new Lease(); int runs=0;
 var ret = bridge.Run(1, () => { runs++; Equal(lease.Suspended, true); return false; }, startedFromResult: true, _ => lease);
 Equal(ret,false); Equal(runs,1); Equal(lease.Resumed,1); Equal(lease.Accepted,0);
}
static void Queued() {
 var bridge = new NativeInputHandoff<int>(); var lease = new Lease();
 Equal(bridge.Run(1, () => true, false, _ => lease),true);
 Equal(lease.Resumed,1); Equal(lease.Accepted,0);
}
static void NestedAccepted() {
 var bridge = new NativeInputHandoff<int>(); var lease = new Lease(); int outer=0,inner=0,factories=0;
 Equal(bridge.Run(1, () => { outer++; return bridge.Run(9, () => { inner++; return true; }, true, _ => { factories++; return new Lease(); }); }, false, _ => { factories++; return lease; }),true);
 Equal(outer,1); Equal(inner,1); Equal(factories,1); Equal(lease.Resumed,0); Equal(lease.Accepted,1); Equal(lease.AcceptedIntent,9);
}
static void Throws() {
 var bridge = new NativeInputHandoff<int>(); var lease = new Lease(); int runs=0;
 try { bridge.Run(1, () => { runs++; throw new InvalidOperationException("native"); }, true, _ => lease); throw new Exception("expected native exception"); } catch(InvalidOperationException ex) { Equal(ex.Message,"native"); }
 Equal(runs,1); Equal(lease.Resumed,1); Equal(lease.Accepted,0);
}
static void Preflight() {
 var bridge = new NativeInputHandoff<int>(); var lease = new Lease();
 Equal(bridge.Inspect(() => { Equal(lease.Suspended,true); return 42; }, () => lease),42);
 Equal(lease.Resumed,1); Equal(lease.Accepted,0);
}
static void CleanupThrows() {
 var bridge = new NativeInputHandoff<int>(); var lease = new Lease { ThrowOnAccept=true }; int runs=0;
 try { bridge.Run(1, () => { runs++; return true; },true,_ => lease); throw new Exception("expected cleanup failure"); } catch(InvalidOperationException ex) { Equal(ex.Message,"cleanup"); }
 Equal(runs,1); Equal(lease.Accepted,1); Equal(lease.Resumed,0);
}
static void GuardedAccepted() {
 var bridge = new NativeInputHandoff<int>(); var lease = new Lease { ThrowOnAccept=true }; int runs=0,errors=0;
 Equal(bridge.RunGuarded(1, () => { runs++; return true; },true,_ => lease,_ => errors++),true);
 Equal(runs,1); Equal(errors,1); Equal(lease.Accepted,1);
}
static void GuardedSuspend() {
 var bridge = new NativeInputHandoff<int>(); int runs=0,errors=0;
 Equal(bridge.RunGuarded(1, () => { runs++; return true; },true,_ => throw new InvalidOperationException("suspend"),_ => errors++),true);
 Equal(runs,1); Equal(errors,1);
}
static void GuardedInspect() {
 var bridge = new NativeInputHandoff<int>(); int errors=0;
 Equal(bridge.InspectGuarded(() => 21, () => new BadResumeLease(), _ => errors++),21);
 Equal(errors,1);
}
static void RestoreManual() { var result=PreviewRestorePolicy.Decide(PreviewExitReason.Manual,true,true); Equal(result.RestoreBaseline,true); Equal(result.RestoreMode,true); Equal(result.TouchActor,true); }
static void RestoreBusy() { var result=PreviewRestorePolicy.Decide(PreviewExitReason.Busy,false,true); Equal(result.RestoreBaseline,false); Equal(result.RestoreMode,false); Equal(result.TouchActor,true); }
static void RestoreNewActor() { var result=PreviewRestorePolicy.Decide(PreviewExitReason.ActorChanged,true,true); Equal(result.RestoreBaseline,false); Equal(result.RestoreMode,false); Equal(result.TouchActor,false); }
static void WinnerPreserved() { Equal(NativeOwnershipPolicy.RestoreSibling(true,52,52,0,true),false); }
static void PendingMount() { Equal(NativeOwnershipPolicy.RemovePendingMount(0,0,445,true),true); Equal(NativeOwnershipPolicy.RemovePendingMount(445,0,445,true),false); Equal(NativeOwnershipPolicy.RemovePendingMount(0,0,445,false),false); }
static void SiblingPreserved() { Equal(NativeOwnershipPolicy.RestoreSibling(false,99,52,0,true),false); Equal(NativeOwnershipPolicy.RestoreSibling(false,52,52,0,true),true); }
static void PendingChild() {
 Equal(NativeOwnershipPolicy.RemovePendingChild(0,0,52,true),true);
 Equal(NativeOwnershipPolicy.RemovePendingChild(0,0,52,false),false);
 Equal(NativeOwnershipPolicy.RemovePendingChild(52,0,52,true),false);
 Equal(NativeOwnershipPolicy.RemovePendingChild(0,0,0,true),false);
}
static void ExternalRejected() {
 var bridge = new NativeInputHandoff<int>(); var visual = new VisualState();
 long token = ExternalBegin(bridge,7,() => new VisualLease(visual));
 Equal(token > 0,true); Equal(visual.Id,0);
 Equal(ExternalComplete(bridge,token,false,true),true);
 Equal(visual.Id,52); Equal(visual.HasPreview,true);
 Equal(ExternalComplete(bridge,token,true,true),false);
}
static void ExternalSameId() {
 var bridge = new NativeInputHandoff<int>(); var visual = new VisualState();
 long token = ExternalBegin(bridge,52,() => new VisualLease(visual));
 visual.Id=52; // The external plugin accepted a new input matching the preview's numeric id.
 Equal(ExternalComplete(bridge,token,true,true),true);
 Equal(visual.Id,52); Equal(visual.HasPreview,false);
}
static void ExternalNested() {
 var bridge = new NativeInputHandoff<int>(); var visual = new VisualState(); int calls=0;
 long token = ExternalBegin(bridge,7,() => new VisualLease(visual));
 Equal(ExternalBegin(bridge,8,() => throw new Exception("nested suspension")),0L);
 Equal(bridge.Run(9,() => { calls++; visual.Id=9; return true; },true,_ => throw new Exception("duplicate suspension")),true);
 Equal(calls,1); Equal(visual.HasPreview,true);
 Equal(ExternalComplete(bridge,token,true,true),true);
 Equal(visual.Id,9); Equal(visual.HasPreview,false);
}
static void ExternalStaleBinding() {
 var bridge = new NativeInputHandoff<int>(); var visual = new VisualState();
 long token = ExternalBegin(bridge,7,() => new VisualLease(visual));
 visual.Id=99;
 Equal(ExternalComplete(bridge,token,false,false),false);
 Equal(visual.Id,99);
}
static void ExternalInvalidated() {
 var bridge = new NativeInputHandoff<int>(); var visual = new VisualState();
 long stale = ExternalBegin(bridge,7,() => new VisualLease(visual));
 ExternalInvalidate(bridge); visual.Id=99;
 long current = ExternalBegin(bridge,9,() => new VisualLease(visual));
 Equal(current > stale,true);
 Equal(ExternalComplete(bridge,stale,false,true),false);
 Equal(visual.Id,0);
 visual.Id=9; Equal(ExternalComplete(bridge,current,true,true),true); Equal(visual.Id,9);
}
static void ExternalCleanupThrows() {
 var bridge = new NativeInputHandoff<int>();
 long token=ExternalBegin(bridge,7,() => new Lease { ThrowOnAccept=true });
 try { ExternalComplete(bridge,token,true,true); throw new Exception("expected cleanup failure"); }
 catch(InvalidOperationException ex) { Equal(ex.Message,"cleanup"); }
 Equal(ExternalComplete(bridge,token,false,true),false);
 var lease=new Lease(); Equal(bridge.Run(1,() => true,true,_ => lease),true); Equal(lease.Accepted,1);
}
static void ImplicitMinionRestore() {
 var owner=NewImplicitOwner(); ImplicitBegin(owner,41,41);
 Equal(ImplicitRestore(owner,true,true,false,false,41,0),false);
 ImplicitObserve(owner,0,0);
 Equal(ImplicitRestore(owner,true,true,false,false,0,0),true);
 ImplicitRestored(owner); Equal(ImplicitRestore(owner,true,true,false,false,0,0),false);
}
static void ImplicitMinionBlocked() {
 var owner=NewImplicitOwner(); ImplicitBegin(owner,41,41); ImplicitObserve(owner,0,0);
 Equal(ImplicitRestore(owner,false,true,false,false,0,0),false);
 Equal(ImplicitRestore(owner,true,false,false,false,0,0),false);
 Equal(ImplicitRestore(owner,true,true,true,false,0,0),false);
 Equal(ImplicitRestore(owner,true,true,false,true,0,0),false);
 ImplicitBegin(owner,41,0); // A replacement mount retains the first real baseline.
 Equal(ImplicitRestore(owner,true,true,false,false,0,0),true);
}
static void ImplicitMinionWinner() {
 var owner=NewImplicitOwner(); ImplicitBegin(owner,41,41); ImplicitObserve(owner,0,99);
 Equal(ImplicitRestore(owner,true,true,false,false,0,99),false);
 ImplicitObserve(owner,0,0); Equal(ImplicitRestore(owner,true,true,false,false,0,0),false);
}
static void AcceptedTimeline() {
 Equal(RestoreTimeline(false,true,0,0,8194,8194,true),true);
 Equal(RestoreTimeline(false,true,0,0,8073,8194,true),false);
 Equal(RestoreTimeline(false,true,0,0,8073,3,true),false);
 Equal(RestoreTimeline(true,true,42,0,8194,8194,true),false);
 Equal(RestoreTimeline(false,true,0,0,8194,8194,false),false);
}
static void RestoreOldTerritory() {
 var result=PreviewRestorePolicy.Decide(PreviewExitReason.Manual,true,true,false);
 Equal(result.TouchActor,false); Equal(result.RestoreBaseline,false);
}
static ImplicitMinionOwnership NewImplicitOwner() => new();
static void ImplicitBegin(ImplicitMinionOwnership owner,ushort baseline,ushort current) => owner.Begin(baseline,current);
static void ImplicitObserve(ImplicitMinionOwnership owner,ushort current,ushort pending) => owner.Observe(current,pending);
static void ImplicitRestored(ImplicitMinionOwnership owner) => owner.Reset();
static bool ImplicitRestore(ImplicitMinionOwnership owner,bool manual,bool allowed,bool mount,bool minion,ushort current,ushort pending)
 => owner.ShouldRestore(manual,allowed,mount,minion,current,pending);
static bool RestoreTimeline(bool winner,bool active,ushort currentEmote,ushort baselineEmote,ushort timeline,ushort exposed,bool sameMode)
 => NativeOwnershipPolicy.RestorePreviousTimeline(winner,active,currentEmote,baselineEmote,timeline,exposed,sameMode);
static long ExternalBegin(NativeInputHandoff<int> bridge,int intent,Func<IPreviewSuspension<int>> suspend)
 => bridge.BeginExternal(intent,suspend);
static bool ExternalComplete(NativeInputHandoff<int> bridge,long token,bool accepted,bool current)
 => bridge.CompleteExternal(token,accepted,current);
static void ExternalInvalidate(NativeInputHandoff<int> bridge) => bridge.InvalidateExternal();
sealed class VisualState { public int Id=52; public bool HasPreview=true; }
sealed class VisualLease : IPreviewSuspension<int> {
 private readonly VisualState visual; private readonly int previous;
 public VisualLease(VisualState visual) { this.visual=visual; previous=visual.Id; visual.Id=0; }
 public void Resume() => visual.Id=previous;
 public void Accept(int intent) => visual.HasPreview=false;
}
sealed class BadResumeLease : IPreviewSuspension<int> { public void Resume() => throw new InvalidOperationException("resume"); public void Accept(int _) {} }
sealed class Lease : IPreviewSuspension<int> {
 public bool Suspended = true; public int Resumed; public int Accepted; public int AcceptedIntent; public bool ThrowOnAccept;
 public void Resume() { Resumed++; Suspended=false; }
 public void Accept(int intent) { Accepted++; AcceptedIntent=intent; Suspended=false; if(ThrowOnAccept) throw new InvalidOperationException("cleanup"); }
}
