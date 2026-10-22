using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

internal static unsafe class ContextHookCases
{
    static readonly PortraitIdentity Source = new(0x100,0x200,0x300,7,0x400);
    const nint Owned = 0x500, IndependentContext = 0x800, OwnedContext = 0x900;
    static void Check(bool ok,string message) { if(!ok) throw new InvalidOperationException(message); }
    public static int Run()
    {
        var passed=0; var failed=0;
        foreach(var (name,run) in new (string,Action)[] {
            ("all three context entry points join the atomic 39-address inventory", Inventory),
            ("one reserved context address prevents all owned Hook creation", ContextReservation),
            ("Configure retires before touching the old active owned slot", OldSlot),
            ("Configure retires before requesting an owned slot", NewSlot),
            ("ConfigureDefault has an independent typed byte ABI", DefaultAL),
            ("Cleanup retires for its old slot even when Active is false", InactiveCleanup),
            ("unknown context calls native once and never releases an owned view", Unknown),
            ("external mutation of owned receiver never frees its native Original receiver", OwnExternal),
            ("self context with exact allocation phase token survives transient owner state", OwnTransient),
            ("foreign context reentry cannot Release the still-active owned native call", SelfForeign),
            ("stale context generation loses retirement authority while native continues", StaleIdentity),
            ("observer exception preserves native AL and calls Original once", ReadThrows),
            ("native exception is propagated once without retry", NativeThrows),
            ("unrelated validated context leaves the active request running", Unrelated),
        }) {
            try { run(); passed++; Console.WriteLine("PASS "+name); }
            catch(Exception e) { failed++; Console.Error.WriteLine("FAIL "+name+": "+e.Message); }
        }
        Console.WriteLine($"ContextHookCases: {passed} passed; {failed} failed"); return failed==0?0:1;
    }
    static void Inventory()
    {
        var targets=PortraitOwnedSceneHooks.FrozenTargets(unchecked((nint)0x140000000));
        Check(targets.Count==39,"expected 39 targets, got "+targets.Count);
        foreach(var (kind,rva) in new[] {
            (PortraitOwnedSceneNativeKind.ConfigureContext,0x269020),
            (PortraitOwnedSceneNativeKind.ConfigureDefaultContext,0x268F60),
            (PortraitOwnedSceneNativeKind.CleanupContext,0x2690B0) })
            Check(targets.Count(t=>t.Kind==kind&&t.Address==unchecked((nint)0x140000000)+rva)==1,"missing exact context target "+kind);
    }
    static void ContextReservation()
    {
        var reservations=new PortraitNativePhaseHostReservations();
        using var p=new Pair(false,reservations);
        Check(reservations.TryAcquireBatch([p.Environment.Module+0x269020],out var held,out _),"pre-reserve failed");
        try { Check(!p.Hooks.TryInstall(p.Factory)&&p.Factory.Created==0,"partial Hook creation ignored context reservation"); }
        finally { Check(held!.TryRelease(out _),"release failed"); }
    }
    static void OldSlot()
    {
        using var p=new Pair();p.Active(); p.Environment.Evidence=new(new(6,2,true),PortraitOwnedSceneContextRole.Independent,0x600,0x700,9,1,1,55,2);
        Check(p.Configure(IndependentContext,3,4)==0xA7,"AL was narrowed");p.AssertRetired("configure");
    }
    static void NewSlot()
    {
        using var p=new Pair();p.Active(); p.Environment.Evidence=new(new(3,2,false),PortraitOwnedSceneContextRole.Independent,0x600,0x700,9,1,1,55,2);
        Check(p.Configure(IndependentContext,6,uint.MaxValue)==0xA7,"AL mismatch");p.AssertRetired("configure");
        Check(p.Factory.LastSlot==6&&p.Factory.LastType==uint.MaxValue,"original arguments changed");
    }
    static void DefaultAL()
    {
        using var p=new Pair();p.Active(); p.Environment.Evidence=new(new(3,2,true),PortraitOwnedSceneContextRole.Independent,0x600,0x700,9,1,1,55,2);
        Check(p.Factory.Get<ConfigureDefaultContext>(PortraitOwnedSceneNativeKind.ConfigureDefaultContext)(IndependentContext,6)==0xB9,"Default AL mismatch");
        p.AssertRetired("default");
    }
    static void InactiveCleanup()
    {
        using var p=new Pair();p.Active(); p.Environment.Evidence=new(new(6,2,false),PortraitOwnedSceneContextRole.Independent,0x600,0x700,9,1,1,55,2);
        p.Factory.Get<CleanupContext>(PortraitOwnedSceneNativeKind.CleanupContext)(IndependentContext);p.AssertRetired("cleanup");
    }
    static void Unknown()
    {
        using var p=new Pair();p.Active();p.Environment.Known=false;
        Check(p.Configure(0xDEAD,6,2)==0xA7,"native result lost");
        Check(p.RetireCalls==0&&p.Factory.ContextOriginalCalls==1&&p.Hooks.LastError is not null,"unknown context granted Release or swallowed native");
    }
    static void OwnExternal()
    {
        using var p=new Pair();p.Active();p.Environment.Evidence=p.Environment.OwnEvidence;
        p.Configure(OwnedContext,6,2);
        Check(p.RetireCalls==0&&p.Factory.ContextOriginalCalls==1&&p.Hooks.LastError is not null,"external own receiver was destroyed before native Original");
    }
    static void OwnTransient()
    {
        using var p=new Pair();
        p.Advance=l=> {
            p.Bind(l);using var call=l.EnterOwnedCall(Owned);Check(call is not null,"call token missing");
            p.Environment.Evidence=p.Environment.OwnEvidence;p.Environment.Transient=true;
            Check(p.Configure(OwnedContext,6,2)==0xA7,"native AL mismatch");p.Environment.Transient=false;
            Check(l.CanAccess(Owned),"self context transient invalidated the owned call");
        };
        p.Active();Check(p.RetireCalls==0&&p.Hooks.LastError is null,"self call tried to retire");
    }
    static void SelfForeign()
    {
        using var p=new Pair();p.Advance=l=> {
            p.Bind(l);using var call=l.EnterOwnedCall(Owned);Check(call is not null,"call token missing");
            p.Environment.Evidence=new(new(6,2,true),PortraitOwnedSceneContextRole.Independent,0x600,0x700,9,1,1,55,2);
            p.Configure(IndependentContext,4,2);
        };
        p.Active();Check(p.RetireCalls==0&&p.Factory.ContextOriginalCalls==1&&p.Hooks.LastError is not null,"foreign context inherited self permission or released the active view");
    }
    static void StaleIdentity()
    {
        using var p=new Pair();p.Active();p.Environment.Current=false;
        p.Configure(IndependentContext,6,2);
        Check(p.RetireCalls==0&&p.Factory.ContextOriginalCalls==1&&p.Hooks.LastError is not null,"stale identity granted Release");
    }
    static void ReadThrows()
    {
        using var p=new Pair();p.Active();p.Environment.ThrowRead=true;
        Check(p.Configure(IndependentContext,6,2)==0xA7&&p.RetireCalls==0&&p.Factory.ContextOriginalCalls==1,"reader exception escaped or swallowed native");
    }
    static void NativeThrows()
    {
        using var p=new Pair();p.Factory.ThrowNative=true;
        var thrown=false;try {p.Configure(IndependentContext,6,2);}catch(InvalidOperationException e) {thrown=e.Message=="native-original-fault";}
        Check(thrown&&p.Factory.ContextOriginalCalls==1,"native exception retried or swallowed");
    }
    static void Unrelated()
    {
        using var p=new Pair();p.Active();p.Environment.Evidence=new(new(3,2,true),PortraitOwnedSceneContextRole.Independent,0x600,0x700,9,1,1,55,2);
        p.Configure(IndependentContext,2,2);p.Render(2);
        Check(p.RetireCalls==0&&p.Advances==2&&p.Hooks.LastError is null,"unrelated context cancelled active request");
    }
    sealed class Pair:IDisposable
    {
        public readonly Env Environment=new();public readonly Factory Factory=new();public readonly PortraitOwnedSceneHooks Hooks;
        public Action<IPortraitOwnedSceneLease>? Advance;public int RetireCalls,Advances; public readonly List<string> Events=[];
        public Pair(bool install=true,PortraitNativePhaseHostReservations? reservations=null) {
            Hooks=new(Environment,reservations);Factory.Events=Events;
            if(install)Check(Hooks.TryInstall(Factory),Hooks.LastError??"install failed");
        }
        public void Bind(IPortraitOwnedSceneLease l) {
            if(!l.CanCreate)return;Check(l.TryBindCreatedView(Owned),"bind failed");
            using(var c=l.EnterCreationCall(Owned,PortraitOwnedSceneCreationCall.Construct))Check(c is not null&&l.ConfirmConstructed(Owned),"construct failed");
            using(var c=l.EnterCreationCall(Owned,PortraitOwnedSceneCreationCall.Setup)) {Check(c is not null,"setup denied");Environment.Owner=Owned;Check(l.ConfirmSetup(Owned),"setup failed");}
        }
        public void Active() {
            Check(Hooks.TryRequest(Source,6,l=>{Advances++;if(Advance is null)Bind(l);else Advance(l);},l=>{
                RetireCalls++;Events.Add("retire");using var c=l.EnterOwnedCall(Owned);
                Check(c is not null&&l.TryBeginRetirement(Owned),"retire denied");Environment.Owner=0;return l.MarkReleased(Owned);
            }),"queue failed");Render(1);
        }
        public void Render(uint frame)=>Factory.Get<CharaView.Delegates.Render>(PortraitOwnedSceneNativeKind.Render)((CharaView*)Source.Model,frame);
        public byte Configure(nint context,uint slot,uint type)=>Factory.Get<ConfigureContext>(PortraitOwnedSceneNativeKind.ConfigureContext)(context,slot,type);
        public void AssertRetired(string original)=>Check(RetireCalls==1&&Factory.ContextOriginalCalls==1&&string.Join(",",Events)=="retire,"+original,"retirement order or count: "+string.Join(",",Events));
        public void Dispose(){Hooks.RequestStop();if(Environment.Owner==Owned&&Hooks.LastError is null)Render(99);Hooks.TryDispose();}
    }
    sealed class Env:IPortraitOwnedSceneHookEnvironment,IPortraitOwnedSceneHookPhaseEnvironment,IPortraitOwnedSceneContextEnvironment
    {
        static long module=0x840000000;public nint Module=(nint)Interlocked.Add(ref module,0x1000000);
        public nint Owner;public bool Known=true,Current=true,ThrowRead,Transient,Active;
        public PortraitOwnedSceneContextEvidence OwnEvidence=>new(new(6,2,true),PortraitOwnedSceneContextRole.Owned,Owned,0xA00,9,1,1,55,3);
        public PortraitOwnedSceneContextEvidence Evidence=new(new(6,2,true),PortraitOwnedSceneContextRole.Independent,0x600,0x700,9,1,1,55,2);
        public bool IsFrameworkThread=>true;public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets=>PortraitOwnedSceneHooks.FrozenTargets(Module);
        public bool TryValidateInstallation(out string reason){reason="test native boundary";return true;}
        public bool IsSourceCurrent(PortraitIdentity s)=>Active&&s==Source;
        public bool IsSlotIdle(uint s)=>Active&&s==6&&Owner==0;
        public bool IsSlotOwnedBy(uint s,nint v)=>Active&&!Transient&&s==6&&Owner==v;
        public bool TryReadViewSlot(nint v,out uint s){s=v==Source.Model?4u:6u;return Active;}
        public bool QueueRequest(PortraitIdentity s,uint slot,long epoch)=>s==Source&&slot==6;
        public IDisposable? EnterSourcePhase(PortraitIdentity s,uint frame,long epoch){Active=true;return new Scope(this);}
        public IDisposable? EnterRetirementPhase(PortraitIdentity s,uint frame,long epoch){var was=Active;Active=true;return new Scope(this,was);}
        public void EndRequest(long epoch){}
        public bool TryReadContext(nint c,nint view,bool ownCall,out PortraitOwnedSceneContextEvidence e){if(ThrowRead)throw new InvalidOperationException("read-fault");e=Evidence;return Active&&Known;}
        public bool IsContextEvidenceCurrent(nint c,PortraitOwnedSceneContextEvidence e)=>Active&&Current&&e==Evidence;
        sealed class Scope(Env e,bool was=false):IDisposable{public void Dispose()=>e.Active=was;}
    }
    sealed class Factory:IPortraitOwnedSceneHookFactory
    {
        readonly Dictionary<PortraitOwnedSceneNativeKind,Delegate> detours=[];public int Created,ContextOriginalCalls;
        public uint LastSlot,LastType;public bool ThrowNative;public List<string> Events=[];
        public T Get<T>(PortraitOwnedSceneNativeKind k)where T:Delegate {Check(detours.ContainsKey(k),"typed context detour is not installed: "+k);return(T)detours[k];}
        byte Configure(nint c,uint s,uint t){ContextOriginalCalls++;Events.Add("configure");LastSlot=s;LastType=t;if(ThrowNative)throw new InvalidOperationException("native-original-fault");return 0xA7;}
        public IPortraitOwnedSceneNativeHook<T> Create<T>(PortraitOwnedSceneNativeTarget target,T detour)where T:Delegate {
            Created++;detours[target.Kind]=detour;
            Delegate original=target.Kind switch {
                PortraitOwnedSceneNativeKind.Render=>(CharaView.Delegates.Render)((_,_)=>{}),
                PortraitOwnedSceneNativeKind.ConfigureContext=>(ConfigureContext)Configure,
                PortraitOwnedSceneNativeKind.ConfigureDefaultContext=>(ConfigureDefaultContext)((_,s)=>{ContextOriginalCalls++;LastSlot=s;Events.Add("default");return 0xB9;}),
                PortraitOwnedSceneNativeKind.CleanupContext=>(CleanupContext)(_=>{ContextOriginalCalls++;Events.Add("cleanup");}),
                _=>detour,
            };return new Hook<T>((T)original);
        }
        sealed class Hook<T>(T original):IPortraitOwnedSceneNativeHook<T>where T:Delegate {
            public T Original=>original;public bool IsEnabled{get;private set;}public bool IsDisposed{get;private set;}
            public void Enable()=>IsEnabled=true;public void Disable()=>IsEnabled=false;public void Dispose(){IsEnabled=false;IsDisposed=true;}
        }
    }
}
