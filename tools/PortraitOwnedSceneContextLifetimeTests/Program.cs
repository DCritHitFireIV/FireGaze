using FireGaze.PortraitPreview;

var passed=0;var failed=0;
foreach(var(name,run) in new(string,Action)[]{
    ("construction does no phase read and unknown pointers never gain generation",Cases.Unknown),
    ("only completed exact native constructor creates a distinct allocation epoch",Cases.Constructor),
    ("destroy invalidates before address can be reused and reconstruct advances epoch",Cases.ABA),
    ("first known view actor allocation binds and another actor or view cannot reuse it",Cases.Binding),
    ("phase thread source session and manager identity are required for every query",Cases.Scope),
    ("stale caller phase is rejected even if live native source has a current token",Cases.StalePhase),
    ("admission loss and explicit reset permanently revoke existing allocation evidence",Cases.Reset),
    ("faulting admission and phase boundaries fail closed without throwing",Cases.Faults),
    ("simultaneous constructors keep globally distinct allocation epochs",Cases.Concurrent),
}) {
    try{run();passed++;Console.WriteLine("PASS "+name);}
    catch(Exception e){failed++;Console.Error.WriteLine("FAIL "+name+": "+e.Message);}
}
Console.WriteLine($"ContextLifetimeTests: {passed} passed; {failed} failed");return failed==0?0:1;

static class Cases
{
    const nint Context=0x1000,View=0x2000,Actor=0x3000;
    static void Check(bool ok,string m){if(!ok)throw new InvalidOperationException(m);}
    internal sealed class Source:IPortraitOwnedSceneSlotPhaseSource
    {
        public int Calls;public bool Throw,Live=true;
        public PortraitOwnedSceneSlotPhase Phase=new(new(0x11,0x22,0x33,7,0x44),new(1,2,3,4,5),1,1,1,1,Environment.CurrentManagedThreadId,true,true);
        public bool TryCapture(out PortraitOwnedSceneSlotPhase p){Calls++;if(Throw)throw new Exception("phase fault");p=Phase;return Live;}
    }
    sealed class Pair
    {
        public readonly Source Source=new();public readonly PortraitOwnedSceneContextAllocationTracker Tracker;public bool Admitted=true,Throw;
        public Pair(){Tracker=new(Source,()=>Throw?throw new Exception("admission fault"):Admitted);}
        public bool Get(out long e,nint c=Context,nint v=View,nint a=Actor,uint g=8)=>Tracker.TryGetAllocation(c,v,a,g,Source.Phase,out e);
        public long Construct(){Tracker.ObserveConstructed(Context,Context);Check(Get(out var e)&&e>0,"completed constructor did not grant a current generation");return e;}
    }
    public static void Unknown(){var p=new Pair();Check(p.Source.Calls==0,"constructor read phase");Check(!p.Get(out var e)&&e==0,"unknown pointer was adopted");}
    public static void Constructor(){var p=new Pair();p.Tracker.ObserveConstructed(Context,0);Check(!p.Get(out _),"null constructor was accepted");p.Tracker.ObserveConstructed(Context,Context+1);Check(!p.Get(out _),"foreign return accepted");p.Construct();}
    public static void ABA(){var p=new Pair();var old=p.Construct();p.Tracker.ObserveDestroying(Context);Check(!p.Get(out _),"destroy retained generation");var next=p.Construct();Check(next>old,"same-address ctor reused old epoch");}
    public static void Binding(){var p=new Pair();var e=p.Construct();Check(p.Get(out var same)&&same==e,"stable binding lost");Check(!p.Get(out _,v:View+1)&&!p.Get(out _,a:Actor+1)&&!p.Get(out _,g:9),"another view or actor generation inherited context");}
    public static void Scope(){foreach(var change in new Func<PortraitOwnedSceneSlotPhase,PortraitOwnedSceneSlotPhase>[] {x=>x with{ThreadID=x.ThreadID+1},x=>x with{SessionEpoch=2},x=>x with{ManagerGeneration=2},x=>x with{Source=x.Source with{Generation=8}},x=>x with{InNativePhase=false},x=>x with{BuildValidated=false}}){var p=new Pair();p.Construct();p.Source.Phase=change(p.Source.Phase);Check(!p.Get(out _),"changed phase or generation was accepted");}}
    public static void StalePhase(){var p=new Pair();p.Construct();var old=p.Source.Phase;p.Source.Phase=old with{PhaseToken=2,NativeFrame=2};Check(!p.Tracker.TryGetAllocation(Context,View,Actor,8,old,out _),"detached old phase authorized new native frame");Check(p.Get(out _),"valid new frame lost stable allocation");p.Source.Live=false;Check(!p.Get(out _),"out-of-scope query accepted");}
    public static void Reset(){var p=new Pair();p.Construct();p.Admitted=false;Check(!p.Get(out _),"admission loss accepted");p.Admitted=true;Check(!p.Get(out _),"readmission resurrected old tracking");p.Tracker.ObserveConstructed(Context,Context);Check(p.Get(out _),"new ctor after readmission rejected");p.Tracker.InvalidateAll();Check(!p.Get(out _),"reset preserved generation");}
    public static void Faults(){var p=new Pair();p.Construct();p.Throw=true;Check(!p.Get(out _),"faulting gate accepted");p=new();p.Construct();p.Source.Throw=true;Check(!p.Get(out _),"faulting phase accepted");}
    public static void Concurrent(){var p=new Pair();Parallel.For(0,16,i=>p.Tracker.ObserveConstructed(Context+i*8,Context+i*8));var epochs=new HashSet<long>();for(int i=0;i<16;i++){Check(p.Get(out var e,c:Context+i*8)&&epochs.Add(e),"duplicate or absent constructor epoch");}}
}
