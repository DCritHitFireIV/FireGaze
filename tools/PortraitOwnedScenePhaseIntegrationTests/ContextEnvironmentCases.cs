using FireGaze.PortraitPreview;

internal static unsafe class ContextEnvironmentCases
{
    static void Check(bool ok,string message) { if(!ok) throw new InvalidOperationException(message); }
    public static int Run()
    {
        var passed=0; var failed=0;
        foreach(var (name,run) in new(string,Action)[]{
            ("real source context mutation retires owned scene before Original", SourceMutation),
            ("unknown context cannot be read or released by the real environment", UnknownReceiver),
            ("context sampling requires an active native phase", NoPhase),
            ("real owned receiver with native constructor evidence stays pinned", OwnReceiverNoLifetime),
            ("own loading context self exemption is denied without allocation lifetime evidence", NoLifetime),
            ("own loading context with external verified allocation preserves native AL", VerifiedLoading),
            ("context actor generation ABA invalidates exact native receiver proof", GenerationChanged),
            ("saved context evidence cannot cross native frame scopes", EscapedEvidence),
        }) {
            try{run();passed++;Console.WriteLine("PASS "+name);}
            catch(Exception e){failed++;Console.Error.WriteLine("FAIL "+name+": "+e.Message);}
        }
        Console.WriteLine($"ContextEnvironmentCases: {passed} passed; {failed} failed");return failed==0?0:1;
    }
    static void SourceMutation()
    {
        using var p=new EnvironmentCases.Pair();p.RetireEnabled=true;
        p.Advance=l=>{if(l.CanCreate)p.BindInitialized(l);};p.Queue();p.Render(1);
        p.Factory.ContextOriginal=(_,_,_)=>p.Events.Add("context-original");
        Check(p.Factory.ConfigureDetour!(p.F.Source->CameraManager,3,2)==0xA7,"native AL lost");
        Check(string.Join(",",p.Events)=="retire,own-release,own-dtor:1,context-original","source context did not retire before native: "+string.Join(",",p.Events));
        Check(p.Factory.ContextCalls==1&&p.Hooks.TryDispose(),"original call count or retirement failed");
    }
    static void UnknownReceiver()
    {
        using var p=new EnvironmentCases.Pair();p.Advance=l=>{if(l.CanCreate)p.BindInitialized(l);};p.Queue();p.Render(1);
        var probed=false;p.F.Memory.AfterRead=a=>{if(a==(nint)0xDEAD)probed=true;};
        p.Factory.ConfigureDetour!(0xDEAD,6,2);
        Check(!probed&&p.RetireCalls==0&&p.Factory.ContextCalls==1&&p.Hooks.LastError is not null,"unknown receiver probed or released");
    }
    static void NoPhase()
    {
        using var p=new EnvironmentCases.Pair();p.Queue();var reads=p.F.Memory.Reads;
        Check(!p.Environment.TryReadContext(p.F.Source->CameraManager,(nint)p.F.Owned,false,out _)
            &&p.F.Memory.Reads==reads,"context proof escaped native phase");
    }
    static void OwnReceiverNoLifetime()
    {
        using var p=new EnvironmentCases.Pair();var observed=false;
        p.Advance=l=>{
            p.BindInitialized(l);p.PrepareOwned();Check(l.CanAccess((nint)p.F.Owned),"pin failed");
            observed=p.Environment.TryReadContext(p.F.Context,(nint)p.F.Owned,false,out var proof)
                &&proof.Role==PortraitOwnedSceneContextRole.Owned&&proof.CharacterGeneration==37&&proof.AllocationEpoch==0;
        };p.Queue();p.Render(1);Check(observed,"real owned receiver was not bound or an allocation generation was fabricated");
        p.Factory.ConfigureDetour!(p.F.Context,6,2);
        Check(p.RetireCalls==0&&p.Factory.ContextCalls==1,"owned Original receiver was freed");
    }
    static void NoLifetime()
    {
        using var p=new EnvironmentCases.Pair();
        p.Advance=l=>{
            p.BindInitialized(l);using var call=l.EnterOwnedCall((nint)p.F.Owned);Check(call is not null,"entry missing");
            p.F.Owned->State=5;p.F.Owned->CameraManager=p.F.Context;
            p.Factory.ConfigureDetour!(p.F.Context,6,0);
        };p.Queue();p.Render(1);
        Check(p.Factory.ContextCalls==1&&p.RetireCalls==0&&p.Hooks.LastError is not null,"unverified self context was admitted or released");
    }
    static void VerifiedLoading()
    {
        using var f=new Fixture();f.SetIdle(6);
        var phases=new PortraitOwnedSceneSlotNativePhaseSource(f.Memory,()=>true,()=>true,()=>1);
        var lifetime=new Lifetime(f);
        var env=new PortraitOwnedSceneHookEnvironment(f.Memory,phases,()=>true,unchecked((nint)0x740000000),Validate,lifetime);
        var factory=new PhaseCases.Factory();using var hooks=new PortraitOwnedSceneHooks(env);
        Check(hooks.TryInstall(factory),"install denied");var completed=false;
        Check(hooks.TryRequest(f.Phase.Source,6,l=>{
            Check(l.TryBindCreatedView((nint)f.Owned),"bind denied");
            using(var c=l.EnterCreationCall((nint)f.Owned,PortraitOwnedSceneCreationCall.Construct))Check(c is not null&&l.ConfirmConstructed((nint)f.Owned),"ctor failed");
            using(var c=l.EnterCreationCall((nint)f.Owned,PortraitOwnedSceneCreationCall.Setup)) {f.SetInitialized(6);Check(c is not null&&l.ConfirmSetup((nint)f.Owned),"setup failed");}
            using(var c=l.EnterOwnedCall((nint)f.Owned)) {
                Check(c is not null,"call denied");f.Owned->State=5;f.Owned->CameraManager=f.Context;
                Check(factory.ConfigureDetour!(f.Context,6,0)==0xA7,"AL lost");f.SetOwned(6);
                Check(l.CanAccess((nint)f.Owned),hooks.LastError??"self context proof failed");completed=true;
            }
        },_=>false),"queue denied");factory.Render((FFXIVClientStructs.FFXIV.Client.UI.Misc.CharaView*)f.Source,8);
        Check(completed&&hooks.LastError is null&&factory.ContextCalls==1&&lifetime.Calls>=2,"verified external lifetime was not used by real context reader");
    }
    static bool Validate(out string reason){reason="external native boundary fixture";return true;}
    sealed class Lifetime(Fixture f):IPortraitOwnedSceneContextAllocationSource
    {
        public int Calls;
        public bool TryGetAllocation(nint context,nint view,nint actor,uint generation,PortraitOwnedSceneSlotPhase phase,out long epoch)
        {Calls++;epoch=101;return context==f.Context&&view==(nint)f.Owned&&actor==(nint)f.Actor&&generation==37&&phase.PhaseToken>0;}
    }
    static void GenerationChanged()
    {
        using var p=new EnvironmentCases.Pair();var rejected=false;
        p.Advance=l=>{
            p.BindInitialized(l);p.PrepareOwned();Check(l.CanAccess((nint)p.F.Owned),"pin failed");
            *(uint*)((byte*)p.F.Actor+0x74)=38;
            rejected=!p.Environment.TryReadContext(p.F.Context,(nint)p.F.Owned,true,out _);
        };p.Queue();p.Render(1);Check(rejected,"actor generation replacement was adopted");
    }
    static void EscapedEvidence()
    {
        using var p=new EnvironmentCases.Pair();PortraitOwnedSceneContextEvidence saved=default;var captured=false;
        p.Advance=l=>{
            if(p.Calls==1)captured=p.Environment.TryReadContext(p.F.Source->CameraManager,(nint)p.F.Owned,false,out saved);
            else Check(!p.Environment.IsContextEvidenceCurrent(p.F.Source->CameraManager,saved),"old phase proof was reused");
        };p.Queue();p.Render(1);Check(captured,"source proof missing");
        Check(!p.Environment.IsContextEvidenceCurrent(p.F.Source->CameraManager,saved),"context proof escaped phase");p.Render(2);
        Check(p.Calls==2,"new phase did not run");
    }
}
