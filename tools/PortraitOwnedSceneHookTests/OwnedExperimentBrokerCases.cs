using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

internal static unsafe class OwnedExperimentBrokerCases
{
    private static int checks;
    private static long nextModule = 0xA800000000;
    internal static readonly PortraitIdentity Source = new(101,102,103,9,104);
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    internal static int Run()
    {
        try
        {
            using (var f = new Fixture(false))
            {
                Check(!f.Controller.TryStart(f.SourceSnapshot, 3) && f.Factory.Creates == 0 && f.Runtime.Creates == 0,
                    "closed experiment ABI validation cannot create native hooks or a view");
            }
            using (var f = new Fixture(true))
            {
                Check(f.Controller.TryStart(f.SourceSnapshot, 3) && !f.Broker.ProductionAdmission && f.Factory.Creates == 46,
                    "explicit experiment queues its first scene in one joint installation without opening production");
                Check(f.Runtime.Creates == 0, "Framework request cannot create the scene");
                f.Render(1); f.Render(2);
                Check(f.Runtime.Creates == 1 && f.Runtime.Renders == 1 && f.Copies == 2,
                    "same real source return advances the controller before its copy continuation");
                f.Controller.Stop(); f.Broker.Update();
                Check(f.Runtime.Retires == 0 && !f.Broker.DrainPhysicalAsync().IsCompleted,
                    "stop keeps the owner until an actual retirement return");
                f.Render(3); f.Broker.Update();
                Check(f.Runtime.Retires == 1 && f.Broker.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(2)),
                    "normal stop drains native owner and same 46 hook installation: "+f.Runtime.Retires+" / "+f.Controller.State+" / "+f.Controller.LastError+" / "+f.Host.Snapshot());
                Check(!f.Controller.TryStart(f.SourceSnapshot,3), "a development capability cannot restart its consumed attempt");
            }
            using (var f = new Fixture(true))
            {
                Check(f.Controller.TryStart(f.SourceSnapshot,3), "expiry fixture starts"); f.Render(1); f.Render(2);
                var copies = f.Copies; f.Now=2001; f.Broker.Update();
                Check(f.Controller.ImageOwner is null && !f.Broker.DrainPhysicalAsync().IsCompleted,
                    "deadline revokes images while retaining native retirement authority");
                f.Render(3); f.Broker.Update();
                Check(f.Runtime.Renders==1 && f.Copies==copies && f.Runtime.Retires==1
                    && f.Broker.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(2)),
                    "deadline return performs retirement only, with no new render or copy");
            }
            using (var f = new Fixture(true))
            {
                Check(f.Controller.TryStart(f.SourceSnapshot,3), "disposed fixture starts");
                f.Render(1); f.Render(2); f.Controller.Dispose(); f.Broker.Dispose();
                Check(!f.Broker.DrainPhysicalAsync().IsCompleted && f.Host.Snapshot().ReservationHeld,
                    "Dispose retains exact installation while its bound scene needs a future native return");
                f.Render(3); // Feature Framework.Update is already detached: no broker Update here.
                Check(f.Runtime.Retires == 1 && f.Broker.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(2))
                    && f.Host.Snapshot() is { Hooks: 0, InFlight: 0, ReservationHeld: false, PhysicalRetired: true },
                    "disposed experiment's real source return must finish native and physical hook retirement without Update");
            }
            using(var f=new Fixture(true))
            {
                Check(f.Controller.TryStart(f.SourceSnapshot,3),"native-first expiry starts");f.Render(1);f.Render(2);
                f.Now=2001;f.Render(3);
                Check(f.Runtime.Retires==1&&f.Stops==0,"native-first expiry fixture retired before managed Update");
                f.Broker.Update();f.Broker.Update();
                Check(f.Stops==1&&f.Broker.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(2)),
                    "native-first expiry must notify the Feature once so its adapter/COM lifetime seals");
            }
            Console.WriteLine($"PASS {checks} explicit experiment broker checks"); return 0;
        }
        catch(Exception error){Console.Error.WriteLine("FAIL experiment broker: "+error);return 1;}
    }
    internal sealed class Fixture : IDisposable
    {
        internal readonly Factory Factory = new();
        internal readonly Runtime Runtime;
        internal readonly PortraitOwnedSceneExperimentBroker Broker;
        internal readonly PortraitOwnedSceneController Controller;
        internal readonly PortraitOwnedCharaViewSnapshot SourceSnapshot = new(Source,default,default,new CharaViewItem[14],0,new());
        internal long Now;
        internal int Copies,Stops;
        internal readonly PortraitOwnedSceneJointHost Host;
        internal Fixture(bool admitted, Func<bool>? allowAdvance = null, Action<IPortraitOwnedSceneLease>? afterAdvance = null,
            Func<bool>? requestCurrent = null)
        {
            var module=(nint)Interlocked.Add(ref nextModule,0x10000000);
            Check(PortraitOwnedSceneJointPlan.TryCreate(module,out var plan,out _),"experiment frozen plan");
            var env=new Env(module);Runtime=new(env);
            PortraitOwnedSceneController? controller=null;
            Host=new(env,Factory,new(),()=>new Scope(),()=>new Scope(),requestCurrent??(()=>true),
                ()=>Now,1000,()=>new(1234,Environment.CurrentManagedThreadId),(_,_)=>new TaskCompletionSource().Task);
            Broker=new(()=>true,Host,plan!,Source,7,1,new(1234,Environment.CurrentManagedThreadId),TimeSpan.FromSeconds(2),
                ()=>(admitted,admitted?"":"ABI denied"),
                lease=>{Copies++;afterAdvance?.Invoke(lease);},reason=>controller?.RevokeNativeSource(reason),reason=>{Stops++;controller?.Stop(reason);},allowAdvance);
            Controller=controller=new(Broker,()=>Runtime);
        }
        internal void Render(uint frame)=>Factory.Render!((CharaView*)Source.Model,frame);
        public void Dispose(){Controller.Dispose();Broker.Dispose();}
    }
    internal sealed class Env(nint module) : IPortraitOwnedSceneHookEnvironment,
        IPortraitOwnedSceneHookPhaseEnvironment,IPortraitOwnedSceneActiveMutationEnvironment
    {
        internal nint Owned;
        public bool IsFrameworkThread=>true;
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets=>PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool TryValidateInstallation(out string reason){reason="explicit test ABI";return true;}
        public bool QueueRequest(PortraitIdentity source,uint slot,long epoch)=>source==Source&&slot==3;
        public IDisposable? EnterSourcePhase(PortraitIdentity source,uint frame,long epoch)=>new Scope();
        public IDisposable? EnterRetirementPhase(PortraitIdentity source,uint frame,long epoch)=>new Scope();
        public void EndRequest(long epoch){}
        public bool IsSourceCurrent(PortraitIdentity source)=>source==Source;
        public bool IsSlotIdle(uint slot)=>slot==3&&Owned==0;
        public bool IsSlotOwnedBy(uint slot,nint view)=>slot==3&&view==Owned;
        public bool TryReadViewSlot(nint view,out uint slot){slot=view==Source.Model?4u:3u;return view==Source.Model||view==Owned;}
        public bool TryQualifyActiveSource(PortraitIdentity source,uint frame,long epoch,uint slot,out nint renderer){renderer=900;return source==Source&&slot==3;}
        public IDisposable? BeginOwnedCounterMutation()=>new Scope();
        public IDisposable? ResumeOwnedCounterPhase(PortraitIdentity source,uint frame,long epoch,nint renderer)=>new Scope();
    }
    internal sealed class Runtime(Env env):IPortraitOwnedSceneRuntime
    {
        internal int Creates,Renders,Retires;
        internal Action? BeforeRender { get; set; }
        internal Action? AfterRetire { get; set; }
        public bool IsReady=>Renders>0;
        public bool TryCreate(PortraitOwnedCharaViewSnapshot snapshot,IPortraitOwnedSceneLease lease)
        {
            if(!lease.TryBindCreatedView(500))return false;
            using(var ctor=lease.EnterCreationCall(500,PortraitOwnedSceneCreationCall.Construct))
                if(ctor is null||!lease.ConfirmConstructed(500))return false;
            using(var setup=lease.EnterCreationCall(500,PortraitOwnedSceneCreationCall.Setup))
            {if(setup is null)return false;env.Owned=500;if(!lease.ConfirmSetup(500))return false;}
            Creates++;return true;
        }
        public bool Advance(IPortraitOwnedSceneLease lease,uint frame){if(!lease.CanAccess(500))return false;BeforeRender?.Invoke();Renders++;return true;}
        public bool UseReadyView(IPortraitOwnedSceneLease lease,Action<nint> read){if(!lease.CanAccess(500))return false;read(500);return true;}
        public bool TryApplyObserverCamera(IPortraitOwnedSceneLease lease,PortraitOwnedSceneObserverCameraSnapshot camera)
            => lease.CanAccess(500) && camera.IsNativeSupported;
        public bool TryRetire(IPortraitOwnedSceneLease lease)
        {using var scope=lease.EnterOwnedCall(500);if(scope is null||!lease.TryBeginRetirement(500))return false;env.Owned=0;if(!lease.MarkReleased(500))return false;Retires++;AfterRetire?.Invoke();return true;}
    }
    private sealed class Scope:IDisposable{public void Dispose(){}}
    internal sealed class Factory:IPortraitOwnedSceneJointHookFactory
    {
        internal int Creates;
        private int activeRender;
        internal CharaView.Delegates.Render? Render;
        public string Identity=>"test external transport";
        public bool TryQualifyTransport(IReadOnlyList<PortraitOwnedSceneNativeTarget> targets,out string reason){reason="";return true;}
        public bool TryVerifyUnmodifiedTargets(IReadOnlyList<PortraitOwnedSceneNativeTarget> targets,out string reason){reason="";return true;}
        public bool TryQualifyJointTransport(PortraitOwnedSceneJointPlan plan,Func<nint,bool> reserved,out string reason){reason="";return true;}
        public bool TryVerifyJointTargets(PortraitOwnedSceneJointPlan plan,out string reason){reason="";return true;}
        public IPortraitOwnedSceneNativeHook<T> Create<T>(PortraitOwnedSceneNativeTarget target,T detour)where T:Delegate
        {Creates++;if(target.Kind==PortraitOwnedSceneNativeKind.Render)Render=(view,frame)=>{
            Interlocked.Increment(ref activeRender);try{((CharaView.Delegates.Render)(Delegate)detour)(view,frame);}
            finally{Interlocked.Decrement(ref activeRender);}};
            Delegate original=target.Kind==PortraitOwnedSceneNativeKind.Render?(CharaView.Delegates.Render)((_,_)=>{}):detour;
            return new Hook<T>((T)original, target.Kind==PortraitOwnedSceneNativeKind.Render?()=>Volatile.Read(ref activeRender):()=>0);}
        public IPortraitOwnedSceneNativeHook CreateJoint(PortraitNativePhaseBoundTarget target,PortraitOwnedSceneJointIngress ingress)
        {Creates++;return new Hook<Action>(()=>{},()=>0);}
        private sealed class Hook<T>(T original,Func<int> inFlight):IPortraitOwnedSceneNativeHook<T>,IPortraitOwnedScenePhysicalHook where T:Delegate
        {
            public T Original=>original;public bool IsEnabled{get;private set;}public bool IsDisposed{get;private set;}
            public int InFlight=>inFlight();public bool RetirementFaulted=>false;public void Enable()=>IsEnabled=true;
            public void Disable()=>IsEnabled=false;public void Dispose(){IsDisposed=true;IsEnabled=false;}
            public void DetachForward(){}public bool BeginRetirement(out string reason){reason="";Disable();return true;}
            public bool TryCompleteRetirement(out string reason){reason="";if(InFlight!=0)return false;Dispose();return true;}
        }
    }
}
