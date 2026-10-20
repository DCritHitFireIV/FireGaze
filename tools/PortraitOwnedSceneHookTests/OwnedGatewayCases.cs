using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

internal static unsafe class OwnedGatewayCases
{
    private static int checks, originals, directs;
    private static nint address = unchecked((nint)0x630000000L);
    internal static int Run()
    {
        try
        {
            var delegates = new[] { typeof(CharaView.Delegates.Dtor), typeof(CharaView.Delegates.Release),
                typeof(CharaView.Delegates.Initialize), typeof(CharaView.Delegates.Update), typeof(CharaViewPortrait.Delegates.Setup),
                typeof(CharaView.Delegates.Render), typeof(ClientObjectManager.Delegates.CreateBattleCharacter),
                typeof(ClientObjectManager.Delegates.DeleteObjectByIndex), typeof(ConfigureContext), typeof(ConfigureDefaultContext),
                typeof(CleanupContext), typeof(ConstructContext), typeof(DestroyContext) };
            for (var abi = 0; abi < delegates.Length; abi++)
            {
                Check(PortraitNativePhaseHostGateway.TryCreateForwarded(abi, delegates[abi], ++address, out var gateway, out var reason),
                    "SDK exact forwarded ABI " + abi + ": " + reason + " Actual=" + delegates[abi].GetMethod("Invoke")
                        + " Convention=" + delegates[abi].GetCustomAttribute<UnmanagedFunctionPointerAttribute>()?.CallingConvention);
                Check(!gateway!.BridgeType.Assembly.IsCollectible
                    && AssemblyLoadContext.GetLoadContext(gateway.BridgeType.Assembly) == AssemblyLoadContext.Default,
                    "bridge and ABI remain in default context");
                Check(gateway.TryAbandonUnpublishedOwnership(), "unpublished stable entry releases ownership");
            }
            Check(!PortraitNativePhaseHostGateway.TryCreateForwarded(4, typeof(CharaView.Delegates.Render), ++address, out _, out _),
                "wrong Setup shape rejected before native entry construction");
            Check(!PortraitNativePhaseHostGateway.TryCreateForwarded(13, typeof(ConstructContext), ++address, out _, out _),
                "unknown arbitrary ABI refused");
            StackABI(); ReturnABI(); OtherABIs(); CallbackLifetime();
            Console.WriteLine($"PASS {checks} owned stable gateway checks"); return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static PortraitNativePhaseHostGateway New(int abi, Type type)
    {
        Check(PortraitNativePhaseHostGateway.TryCreateForwarded(abi,type,++address,out var gateway,out var reason),reason);
        return gateway!;
    }
    private static void StackABI()
    {
        var gateway = New(4,typeof(CharaViewPortrait.Delegates.Setup));
        gateway.PublishOriginal(Delegate.CreateDelegate(gateway.DelegateType,Method(nameof(SetupOriginal))));
        var calls=0;
        CharaViewPortrait.Delegates.Setup callback=(view,slot,data,agent,eventID,eventKind)=> {
            Check(gateway.ActiveCount==1,"full stack-argument callback is active");
            Check((nint)view==11 && slot==7 && (nint)data==22 && (nint)agent==33
                && eventID==-12345678 && eventKind==0xFFEEDDCCBBAA9988,"six native arguments preserved exactly");calls++;
        };
        gateway.SetForward(callback);
        var native=(delegate* unmanaged<CharaViewPortrait*,uint,CharaViewCharacterData*,AgentInterface*,int,ulong,void>)Marshal.GetFunctionPointerForDelegate(gateway.Detour);
        native((CharaViewPortrait*)11,7,(CharaViewCharacterData*)22,(AgentInterface*)33,-12345678,0xFFEEDDCCBBAA9988);
        gateway.DetachObserver();
        Check(gateway.TryRetireTo(Delegate.CreateDelegate(gateway.DelegateType,Method(nameof(SetupOriginal)))),"Setup fully retired");
        gateway.ReleaseOwnership();native(null,0,null,null,0,0);
        Check(calls==1 && directs==1,"late Setup entry only calls stable direct target");GC.KeepAlive(callback);
    }
    private static void ReturnABI()
    {
        var gateway=New(0,typeof(CharaView.Delegates.Dtor));
        gateway.PublishOriginal(Delegate.CreateDelegate(gateway.DelegateType,Method(nameof(DtorDirect))));
        CharaView.Delegates.Dtor callback=(view,flags)=> {Check((nint)view==99 && flags==0xED,"pointer/byte Dtor inputs preserved");return (CharaView*)0x123456789;};
        gateway.SetForward(callback);
        var native=(delegate* unmanaged<CharaView*,byte,CharaView*>)Marshal.GetFunctionPointerForDelegate(gateway.Detour);
        Check((nint)native((CharaView*)99,0xED)==0x123456789,"pointer native result preserved");
        gateway.DetachObserver();Check(gateway.TryRetireTo(Delegate.CreateDelegate(gateway.DelegateType,Method(nameof(DtorDirect)))),"Dtor fully retired");gateway.ReleaseOwnership();
        Check((nint)native(null,0)==0x987654321,"late pointer result is direct target result");GC.KeepAlive(callback);
        var context=New(8,typeof(ConfigureContext));
        context.PublishOriginal(Delegate.CreateDelegate(context.DelegateType,Method(nameof(ContextDirect))));
        ConfigureContext configuration=(_,slot,type)=>{Check(slot==7 && type==0xFFFFFFFF,"context uint args intact");return 0xFE;};context.SetForward(configuration);
        var invoke=(delegate* unmanaged[Cdecl]<nint,uint,uint,byte>)Marshal.GetFunctionPointerForDelegate(context.Detour);
        Check(invoke(7,7,uint.MaxValue)==0xFE,"context byte return intact");
        context.DetachObserver();Check(context.TryRetireTo(Delegate.CreateDelegate(context.DelegateType,Method(nameof(ContextDirect)))),"context fully retired");context.ReleaseOwnership();
        Check(invoke(0,0,0)==0xA5,"late context byte return is direct");GC.KeepAlive(configuration);
    }
    private static void CallbackLifetime()
    {
        foreach(var pause in new[]{"prefix","completion"})
        {
            originals=0;var gateway=New(5,typeof(CharaView.Delegates.Render));
            gateway.PublishOriginal(Delegate.CreateDelegate(gateway.DelegateType,Method(nameof(RenderOriginal))));
            using var entered=new ManualResetEventSlim();using var finish=new ManualResetEventSlim();
            CharaView.Delegates.Render callback=(view,frame)=> {
                if(pause=="prefix"){entered.Set();finish.Wait();}RenderOriginal(view,frame);
                if(pause=="completion"){entered.Set();finish.Wait();}
            };gateway.SetForward(callback);
            var call=(CharaView.Delegates.Render)Delegate.CreateDelegate(typeof(CharaView.Delegates.Render),gateway.Detour.Target,gateway.Detour.Method);
            var pending=Task.Run(()=>call(null,0));Check(entered.Wait(TimeSpan.FromSeconds(3)),"callback reached "+pause);
            gateway.DetachObserver();var direct=Delegate.CreateDelegate(gateway.DelegateType,Method(nameof(RenderDirect)));
            Check(gateway.ActiveCount==1 && !gateway.TryRetireTo(direct),"prefix/completion retains physical invocation: "+pause);
            Check(originals==(pause=="prefix"?0:1),"callback boundary distinct from Original depth");
            finish.Set();Check(pending.Wait(TimeSpan.FromSeconds(3)),"callback exits "+pause);
            Check(gateway.TryRetireTo(direct) && gateway.ActiveCount==0,"physical retirement follows complete callback");gateway.ReleaseOwnership();
            call(null,0);Check(originals==1,"late entry never reaches old dispatcher/Original");
        }
        var thrown=New(5,typeof(CharaView.Delegates.Render));thrown.PublishOriginal(Delegate.CreateDelegate(thrown.DelegateType,Method(nameof(RenderOriginal))));
        var failure=new InvalidOperationException("original identity");
        CharaView.Delegates.Render failing=(view,frame)=>{RenderOriginal(view,frame);throw failure;};thrown.SetForward(failing);
        var managedCall=(CharaView.Delegates.Render)Delegate.CreateDelegate(typeof(CharaView.Delegates.Render),thrown.Detour.Target,thrown.Detour.Method);var before=originals;
        try{managedCall(null,0);throw new Exception("missing failure");}catch(InvalidOperationException actual){Check(ReferenceEquals(actual,failure),"dispatcher exception preserved");}
        Check(originals==before+1 && thrown.ActiveCount==0,"exception unwinds active after exactly once Original");
        thrown.DetachObserver();Check(thrown.TryRetireTo(Delegate.CreateDelegate(thrown.DelegateType,Method(nameof(RenderDirect)))),"throwing callback drained");thrown.ReleaseOwnership();
    }

    private static void OtherABIs()
    {
        var gateways = new List<(PortraitNativePhaseHostGateway Gateway, string Method)>();
        PortraitNativePhaseHostGateway Entry(int abi, Delegate callback, string name)
        {
            var gateway = New(abi, callback.GetType());
            gateway.PublishOriginal(Delegate.CreateDelegate(gateway.DelegateType, Method(name)));
            gateway.SetForward(callback); gateways.Add((gateway, name)); return gateway;
        }
        var release = Entry(1, (CharaView.Delegates.Release)ReleaseSink, nameof(ReleaseSink));
        ((delegate* unmanaged<CharaView*, void>)Marshal.GetFunctionPointerForDelegate(release.Detour))((CharaView*)0x123456789);
        var initialize = Entry(2, (CharaView.Delegates.Initialize)InitializeSink, nameof(InitializeSink));
        ((delegate* unmanaged<CharaView*, AgentInterface*, uint, nint, void>)Marshal.GetFunctionPointerForDelegate(initialize.Detour))(
            (CharaView*)11, (AgentInterface*)22, uint.MaxValue, unchecked((nint)0x123456789L));
        var update = Entry(3, (CharaView.Delegates.Update)UpdateSink, nameof(UpdateSink));
        ((delegate* unmanaged<CharaView*, uint, Character*, void>)Marshal.GetFunctionPointerForDelegate(update.Detour))(
            (CharaView*)33, 0, (Character*)0x987654321);
        var create = Entry(6, (ClientObjectManager.Delegates.CreateBattleCharacter)CreateSink, nameof(CreateSink));
        Check(((delegate* unmanaged<ClientObjectManager*, uint, byte, uint>)Marshal.GetFunctionPointerForDelegate(create.Detour))(
            (ClientObjectManager*)44, uint.MaxValue, 0xED) == 0xFEDCBA98, "ClientObject Create uint return preserved");
        var delete = Entry(7, (ClientObjectManager.Delegates.DeleteObjectByIndex)DeleteSink, nameof(DeleteSink));
        ((delegate* unmanaged<ClientObjectManager*, ushort, byte, void>)Marshal.GetFunctionPointerForDelegate(delete.Detour))(
            (ClientObjectManager*)55, ushort.MaxValue, 0xFC);
        var defaults = Entry(9, (ConfigureDefaultContext)DefaultSink, nameof(DefaultSink));
        Check(((delegate* unmanaged[Cdecl]<nint, uint, byte>)Marshal.GetFunctionPointerForDelegate(defaults.Detour))(
            unchecked((nint)0xABCDEFFFL), uint.MaxValue) == 0xFD, "ConfigureDefault Cdecl byte return preserved");
        var cleanup = Entry(10, (CleanupContext)CleanupSink, nameof(CleanupSink));
        ((delegate* unmanaged[Cdecl]<nint, void>)Marshal.GetFunctionPointerForDelegate(cleanup.Detour))(unchecked((nint)0xBCDEFABCL));
        var construct = Entry(11, (ConstructContext)ConstructSink, nameof(ConstructSink));
        Check(((delegate* unmanaged[Cdecl]<nint, nint>)Marshal.GetFunctionPointerForDelegate(construct.Detour))(
            unchecked((nint)0x9988776655L)) == unchecked((nint)0x123987654L), "Construct Cdecl native pointer return preserved");
        var destroy = Entry(12, (DestroyContext)DestroySink, nameof(DestroySink));
        Check(((delegate* unmanaged[Cdecl]<nint, byte, nint>)Marshal.GetFunctionPointerForDelegate(destroy.Detour))(
            unchecked((nint)0x1122334455L), 0xF1) == unchecked((nint)0x123456788L), "Destroy Cdecl native pointer return preserved");
        foreach (var (gateway, name) in gateways)
        {
            Check(gateway.ActiveCount == 0, "every exact native ABI callback leaves zero active");
            gateway.DetachObserver(); Check(gateway.TryRetireTo(Delegate.CreateDelegate(gateway.DelegateType, Method(name))), "exact ABI callback retires");
            gateway.ReleaseOwnership();
        }
    }
    private static void ReleaseSink(CharaView* view) => Check((nint)view == 0x123456789, "Release pointer parameter preserved");
    private static void InitializeSink(CharaView* view, AgentInterface* agent, uint slot, nint callback)
        => Check((nint)view == 11 && (nint)agent == 22 && slot == uint.MaxValue && callback == unchecked((nint)0x123456789L), "Initialize all four native arguments preserved");
    private static void UpdateSink(CharaView* view, uint frame, Character* character)
        => Check((nint)view == 33 && frame == 0 && (nint)character == 0x987654321, "Update native frame zero and character preserved");
    private static uint CreateSink(ClientObjectManager* manager, uint index, byte flag)
    { Check((nint)manager == 44 && index == uint.MaxValue && flag == 0xED, "CreateClientObject native uint/byte arguments preserved"); return 0xFEDCBA98; }
    private static void DeleteSink(ClientObjectManager* manager, ushort index, byte flag)
        => Check((nint)manager == 55 && index == ushort.MaxValue && flag == 0xFC, "DeleteClientObject native ushort/byte arguments preserved");
    private static byte DefaultSink(nint context, uint slot)
    { Check(context == unchecked((nint)0xABCDEFFFL) && slot == uint.MaxValue, "ConfigureDefault native arguments preserved"); return 0xFD; }
    private static void CleanupSink(nint context) => Check(context == unchecked((nint)0xBCDEFABCL), "Cleanup native pointer preserved");
    private static nint ConstructSink(nint context)
    { Check(context == unchecked((nint)0x9988776655L), "Construct native pointer preserved"); return unchecked((nint)0x123987654L); }
    private static nint DestroySink(nint context, byte flags)
    { Check(context == unchecked((nint)0x1122334455L) && flags == 0xF1, "Destroy native pointer and full byte flags preserved"); return unchecked((nint)0x123456788L); }
    public static void SetupOriginal(CharaViewPortrait* view,uint slot,CharaViewCharacterData* data,AgentInterface* agent,int eventID,ulong eventKind)=>directs++;
    public static CharaView* DtorDirect(CharaView* view,byte flags)=>(CharaView*)0x987654321;
    public static byte ContextDirect(nint context,uint slot,uint type)=>0xA5;
    public static void RenderOriginal(CharaView* view,uint frame)=>Interlocked.Increment(ref originals);
    public static void RenderDirect(CharaView* view,uint frame)=>Interlocked.Increment(ref directs);
    private static MethodInfo Method(string name)=>typeof(OwnedGatewayCases).GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static)!;
    private static void Check(bool value,string reason){Interlocked.Increment(ref checks);if(!value)throw new Exception("FAIL "+reason);}
}
