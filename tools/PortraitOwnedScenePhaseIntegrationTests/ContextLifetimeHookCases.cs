using FireGaze.PortraitPreview;

internal static unsafe class ContextLifetimeHookCases
{
    static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    public static int Run()
    {
        var passed=0;var failed=0;
        foreach(var(name,run) in new(string,Action)[]{
            ("context constructor and deleting destructor join the same atomic 39-entry batch",Inventory),
            ("actual default provider observes constructor then permits the loading self context",ConstructToSelf),
            ("default provider rejects a prepared context without native constructor evidence",NoCtor),
            ("context destruction revokes evidence before native deleting Original",DestroyBeforeOriginal),
            ("same context address reconstruct invalidates the pinned prepared allocation",Reconstruction),
            ("ctor pointer and deleting flags return are transparent and Original runs once",ABI),
            ("constructor Original exception is not swallowed or retried",NativeThrows),
        }) {
            try{run();passed++;Console.WriteLine("PASS "+name);}
            catch(Exception e){failed++;Console.Error.WriteLine("FAIL "+name+": "+e.Message);}
        }
        Console.WriteLine($"ContextLifetimeHookCases: {passed} passed; {failed} failed");return failed==0?0:1;
    }
    static void Inventory()
    {
        var targets=PortraitOwnedSceneHooks.FrozenTargets(unchecked((nint)0x140000000));
        Check(targets.Count==39,"expected 39 targets; got "+targets.Count);
        Check(targets.Count(t=>t.Kind==PortraitOwnedSceneNativeKind.ConstructContext&&t.Address==unchecked((nint)0x140268F20))==1,"ctor target missing");
        Check(targets.Count(t=>t.Kind==PortraitOwnedSceneNativeKind.DestroyContext&&t.Address==unchecked((nint)0x1402EDFC0))==1,"deleting dtor target missing");
    }
    static void ConstructToSelf()
    {
        using var p=new EnvironmentCases.Pair();var completed=false;
        Check(p.Factory.ConstructDetour is not null,"typed constructor not installed");
        p.Advance=l=>{
            p.BindInitialized(l);using var call=l.EnterOwnedCall((nint)p.F.Owned);Check(call is not null,"call missing");
            p.F.Owned->State=5;
            Check(p.Factory.ConstructDetour!(p.F.Context)==p.F.Context,"ctor return lost");
            p.F.Owned->CameraManager=p.F.Context;
            Check(p.Environment.TryReadContext(p.F.Context,(nint)p.F.Owned,true,out var evidence)&&evidence.AllocationEpoch>0,"default tracker did not receive native ctor evidence");
            Check(p.Factory.ConfigureDetour!(p.F.Context,6,0)==0xA7,"AL lost");p.F.SetOwned(6);
            Check(l.CanAccess((nint)p.F.Owned),p.Hooks.LastError??"post-Configure pin lost");completed=true;
        };p.Queue();p.Render(1);
        Check(completed&&p.RetireCalls==0&&p.Hooks.LastError is null&&p.Factory.ConstructCalls==1,"default constructor→configure→prepared path failed");
    }
    static void NoCtor()
    {
        using var p=new EnvironmentCases.Pair();var denied=false;
        p.Advance=l=>{p.BindInitialized(l);p.F.SetOwned(6);denied=!l.CanAccess((nint)p.F.Owned);};p.Queue();p.Render(1);
        Check(denied,"prepared context without constructor evidence was adopted");
    }
    static void Prepared(EnvironmentCases.Pair p,IPortraitOwnedSceneLease l)
    {
        p.BindInitialized(l);Check(p.Factory.ConstructDetour is not null,"typed constructor not installed");
        p.Factory.ConstructDetour!(p.F.Context);p.F.SetOwned(6);Check(l.CanAccess((nint)p.F.Owned),"prepared pin missing");
    }
    static void DestroyBeforeOriginal()
    {
        using var p=new EnvironmentCases.Pair();var revoked=false;
        p.Advance=l=>{
            Prepared(p,l);Check(p.Environment.TryReadContext(p.F.Context,(nint)p.F.Owned,true,out var evidence),"evidence missing");
            p.Factory.ContextDestroyOriginal=(context,flags)=>{revoked=!p.Environment.IsContextEvidenceCurrent(context,evidence)&&!l.CanAccess((nint)p.F.Owned);return context;};
            Check(p.Factory.ContextDestroyDetour is not null,"deleting dtor not installed");p.Factory.ContextDestroyDetour!(p.F.Context,1);
        };p.Queue();p.Render(1);Check(revoked&&p.Factory.ContextDestroyCalls==1&&p.RetireCalls==0,"context was still admitted during its native destruction");
    }
    static void Reconstruction()
    {
        using var p=new EnvironmentCases.Pair();var denied=false;
        p.Advance=l=>{Prepared(p,l);p.Factory.ConstructDetour!(p.F.Context);denied=!l.CanAccess((nint)p.F.Owned);};p.Queue();p.Render(1);
        Check(denied,"new allocation at the same context address was silently rebound");
    }
    static void ABI()
    {
        using var p=new EnvironmentCases.Pair();Check(p.Factory.ConstructDetour is not null&&p.Factory.ContextDestroyDetour is not null,"typed lifetime entry missing");
        p.Factory.ConstructOriginal=c=>c+0xFF;Check(p.Factory.ConstructDetour!(0x123456)==0x123555,"ctor pointer result changed");
        p.Factory.ContextDestroyOriginal=(c,f)=>{Check(c==0x123456&&f==0xA5,"native dtor args changed");return 0x234567;};
        Check(p.Factory.ContextDestroyDetour!(0x123456,0xA5)==0x234567,"native deleting dtor return changed");
        Check(p.Factory.ConstructCalls==1&&p.Factory.ContextDestroyCalls==1,"lifetime original count changed");
    }
    static void NativeThrows()
    {
        using var p=new EnvironmentCases.Pair();Check(p.Factory.ConstructDetour is not null,"typed constructor missing");
        p.Factory.ConstructOriginal=_=>throw new InvalidOperationException("native-ctor");var thrown=false;
        try{p.Factory.ConstructDetour!(0x123456);}catch(InvalidOperationException e){thrown=e.Message=="native-ctor";}
        Check(thrown&&p.Factory.ConstructCalls==1,"ctor exception swallowed or retried");
    }
}
