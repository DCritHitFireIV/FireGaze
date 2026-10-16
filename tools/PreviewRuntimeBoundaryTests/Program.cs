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
sealed class BadResumeLease : IPreviewSuspension<int> { public void Resume() => throw new InvalidOperationException("resume"); public void Accept(int _) {} }
sealed class Lease : IPreviewSuspension<int> {
 public bool Suspended = true; public int Resumed; public int Accepted; public int AcceptedIntent; public bool ThrowOnAccept;
 public void Resume() { Resumed++; Suspended=false; }
 public void Accept(int intent) { Accepted++; AcceptedIntent=intent; Suspended=false; if(ThrowOnAccept) throw new InvalidOperationException("cleanup"); }
}
