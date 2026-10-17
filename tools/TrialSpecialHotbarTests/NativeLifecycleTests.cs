using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Interaction;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using NativeFramework=FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using Character=FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using Slot=FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlot;
using SlotType=FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

internal static unsafe class NativeLifecycleTests
{
    internal const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static NativeFixture? disposingActor;
    private static QueuedFramework? deletingFramework;
    private static bool deletionOnFramework;
    private static bool visibleBeforeDeletion;

    public static void Run(Action<bool,string> check)
    {
        RuntimeDisposal(check);
        using(var f=new BarFixture(true))
        {
            f.Framework.Inside=true;
            check(f.Execute(0) && f.RoutedSlots.SequenceEqual(new[]{0}),
                "real production native dispatcher consumes leased Jump display slot through the local callback only");
            check(f.Module->PetHotbar.GetHotbarSlot(0)->CommandType==SlotType.GeneralAction
                && f.Module->PetHotbar.GetHotbarSlot(0)->CommandId==2 && f.Bridge.InputsConsumed==1,
                "local flight routing retains native Jump icon metadata without replacing it by a gameplay action");
            f.Framework.Inside=false;
        }
        using(var f=new BarFixture())
        {
            f.Module->PetHotbar.GetHotbarSlot(1)->CommandType=SlotType.Action;
            f.Module->PetHotbar.GetHotbarSlot(1)->CommandId=999;
            f.Refresh();
            check(f.Module->PetHotbar.GetHotbarSlot(1)->CommandId==999,"genuine native slot rewrite survives trial cleanup");
            check(f.Module->PetHotbarMode==0 && f.InputFlags==0 && !f.Addon->IsVisible,
                "one native slot update still restores unchanged trial-owned mode input and visibility");
            check(!f.Lease.Held,"native slot rewrite releases the suspended trial lease");
        }
        using(var f=new BarFixture())
        {
            f.Module->PetHotbarMode=3;f.InputFlags=0x24;
            f.Refresh();
            check(f.Module->PetHotbarMode==3 && f.InputFlags==0x24 && f.Addon->IsVisible,
                "genuine replacement UI mode retains its native input and visibility");
        }
        using(var f=new BarFixture())
        {
            f.Framework.AfterCallback=()=>f.Bridge.Publish(f.Bar);
            f.DisposeFromWorker();
            check(!f.Lease.Held && f.Module->PetHotbarMode==0 && f.InputFlags==0,
                "a framework callback between cleanup and worker wakeup cannot reacquire a disposed native bar");
            check(f.HookDisposals==1 && f.HookDisposedOnFramework && f.UnregisterCalls==2,
                "worker native-bar disposal unloads the hook and both callbacks on Framework before returning");
        }
        foreach(var failure in new[]{"resolve","unregister"})
        {
            using var f=new BarFixture();f.ThrowResolve=failure=="resolve";f.ThrowUnregister=failure=="unregister";
            f.DisposeFromWorker();
            check(f.HookDisposals==1 && f.UnregisterCalls==2 && !f.Bridge.Available,
                failure+" failure cannot skip native hook or remaining callback cleanup");
        }
    }

    private static void RuntimeDisposal(Action<bool,string> check)
    {
        using var f=new NativeFixture(null!);f.PrepareVisibility();
        f.DuringOriginal=null;f.SetupWritesCompanionObject=false;f.SetupCompanionObject=null;
        using var framework=new QueuedFramework();
        NativeFixture.Set(f.Runtime,"framework",framework.Service);
        var marker=new DisposalMarker(()=>check(framework.Inside,"runtime hook removal executes on Framework"));
        NativeFixture.Set(f.Runtime,"installedHooks",new List<IDisposable>{marker});
        NativeFixture.Set(f.Runtime,"log",NativeTestProxy.Create<IPluginLog>((_,_)=>null));
        var before=ClientObjectManager.Addresses.DeleteObjectByIndex.Value;
        disposingActor=f;deletingFramework=framework;deletionOnFramework=visibleBeforeDeletion=false;
        ClientObjectManager.Addresses.DeleteObjectByIndex.Value=(nint)(delegate* unmanaged<ClientObjectManager*,ushort,byte,void>)&Delete;
        try
        {
            var worker=Task.Run(f.Runtime.Dispose);
            framework.WaitForDispatchOrCompletion(worker);
            check(!worker.IsCompleted && f.Deletes==0 && !f.Source->DrawObject->IsVisible,
                "worker runtime Dispose waits without deleting or revealing native objects before Framework dispatch");
            framework.Drain();worker.GetAwaiter().GetResult();
            check(f.Deletes==1 && deletionOnFramework && visibleBeforeDeletion && f.Source->DrawObject->IsVisible && marker.Count==1,
                "runtime Dispose restores source visibility then deletes owned actor and unloads hooks on Framework");
        }
        finally{ClientObjectManager.Addresses.DeleteObjectByIndex.Value=before;disposingActor=null;deletingFramework=null;}
    }
    [UnmanagedCallersOnly] private static void Delete(ClientObjectManager* manager,ushort slot,byte extra)
    {
        deletionOnFramework=deletingFramework!.Inside;visibleBeforeDeletion=disposingActor!.Source->DrawObject->IsVisible;
        disposingActor.Deletes++;manager->BattleCharas[slot].BattleChara=null;
    }
    private sealed class DisposalMarker(Action action):IDisposable
    {
        public int Count;public void Dispose(){Count++;action();}
    }

    private sealed class BarFixture:IDisposable
    {
        private static BarFixture current=null!;
        private readonly List<nint> allocations=[];
        private readonly nint previousFramework=NativeFramework.Addresses.Instance.Value;
        private readonly nint previousUi=NativeFramework.Addresses.GetUIModule.Value;
        private readonly nint previousLocal=Control.Addresses.GetLocalPlayer.Value;
        private readonly nint previousSet=Slot.Addresses.Set.Value;
        public readonly QueuedFramework Framework=new();
        public readonly RaptureHotbarModule* Module;
        public readonly AtkUnitBase* Addon;
        public readonly UIModule* Ui;
        public readonly TrialNativeSpecialHotbar Bridge;
        public readonly TrialNativeHotbarLease Lease=new();
        public readonly TrialSpecialBar Bar;
        public readonly List<int> RoutedSlots=[];
        public int HookDisposals,UnregisterCalls;
        public bool HookDisposedOnFramework,ThrowResolve,ThrowUnregister;
        public ushort InputFlags{get=>*(ushort*)((byte*)Module+0x2AD4C);set=>*(ushort*)((byte*)Module+0x2AD4C)=value;}
        public BarFixture(bool flightPose=false)
        {
            Bar=flightPose?new(true,335,9,"fixture",new TrialSpecialAction?[]{new(0,2,102,"飞行姿态",0,true,TrialSpecialActionKind.FlightPose),null,null,null,null,null})
                :new(false,56,9,"fixture",new TrialSpecialAction?[]{new(0,50731,0,"fixture",13384,false),null,null,null,null,null});
            current=this;Module=Alloc<RaptureHotbarModule>();Addon=Alloc<AtkUnitBase>();Ui=Alloc<UIModule>();
            var native=Alloc<NativeFramework>();var nativePointer=Alloc<nint>();*nativePointer=(nint)native;
            var table=Alloc<UIModule.UIModuleVirtualTable>();Ui->VirtualTable=table;
            table->GetRaptureHotbarModule=&GetModule;
            NativeFramework.Addresses.Instance.Value=(nint)nativePointer;
            NativeFramework.Addresses.GetUIModule.Value=(nint)(delegate* unmanaged<NativeFramework*,UIModule*>)&GetUi;
            var source=Alloc<Character>();var local=Alloc<nint>();*local=(nint)source;
            Control.Addresses.GetLocalPlayer.Value=(nint)local;*(uint*)((byte*)source+0x74)=7;
            Slot.Addresses.Set.Value=(nint)(delegate* unmanaged<Slot*,UIModule*,SlotType,uint,void>)&SetSlot;
            Addon->Id=4;Addon->RootNode=Alloc<AtkResNode>();Addon->IsVisible=true;
            var before=new TrialNativeHotbarCommand[16];var expected=new TrialNativeHotbarCommand[16];
            for(var i=0;i<16;i++)before[i]=new((byte)SlotType.GeneralAction,(uint)(100+i));
            expected[0]=flightPose?new((byte)SlotType.GeneralAction,2):new((byte)SlotType.Action,50731);expected[6]=new((byte)SlotType.GeneralAction,23);
            Lease.Acquire(new((nint)Module,(nint)Addon,4,(nint)source,7,0,0,9),before,expected,0,false);
            for(var i=0;i<16;i++){var slot=Module->PetHotbar.GetHotbarSlot((uint)i);slot->CommandType=(SlotType)expected[i].Type;slot->CommandId=expected[i].Id;}
            Module->PetHotbarMode=14;InputFlags=4;
            Bridge=(TrialNativeSpecialHotbar)RuntimeHelpers.GetUninitializedObject(typeof(TrialNativeSpecialHotbar));
            Set(Bridge,"framework",Framework.Service);Set(Bridge,"lease",Lease);Set(Bridge,"bar",Bar);Set(Bridge,"available",true);Set(Bridge,"listenerRegistered",true);
            Set(Bridge,"activate",(Func<int,long,bool>)((slot,revision)=>{RoutedSlots.Add(slot);return revision==Bar.Revision;}));
            Set(Bridge,"gameGui",new GameGuiFixture(()=>
            {
                if(ThrowResolve)throw new InvalidOperationException("fixture addon lookup unavailable");
                return (nint)Addon;
            }));
            Set(Bridge,"lifecycle",NativeTestProxy.Create<IAddonLifecycle>((m,_)=>
            {
                if(m.Name!="UnregisterListener")throw new NotSupportedException(m.Name);
                UnregisterCalls++;if(ThrowUnregister && UnregisterCalls==1)throw new InvalidOperationException("fixture unregister failure");return null;
            }));
            Set(Bridge,"log",NativeTestProxy.Create<IPluginLog>((_,_)=>null));
            Set(Bridge,"execute",SafeHook(Bridge.GetType().GetField("execute",All)!.FieldType,
                ()=>{HookDisposals++;HookDisposedOnFramework=Framework.Inside;}));
        }
        public void Refresh()=>Bridge.GetType().GetMethod("Refresh",All)!.Invoke(Bridge,null);
        private delegate bool ExecuteBar(RaptureHotbarModule* module,Slot* slot);
        public bool Execute(int slot)=>Bridge.GetType().GetMethod("Execute",All)!.CreateDelegate<ExecuteBar>(Bridge)(Module,Module->PetHotbar.GetHotbarSlot((uint)slot));
        public void DisposeFromWorker()
        {
            var worker=Task.Run(Bridge.Dispose);Framework.WaitForDispatchOrCompletion(worker);Framework.Drain();
            try{worker.GetAwaiter().GetResult();}catch(Exception ex){Console.WriteLine("Native fixture disposal error: "+ex.GetType().Name);}
        }
        private T* Alloc<T>()where T:unmanaged{var p=(T*)NativeMemory.AllocZeroed((nuint)sizeof(T));allocations.Add((nint)p);return p;}
        [UnmanagedCallersOnly] private static UIModule* GetUi(NativeFramework* _)=>current.Ui;
        [UnmanagedCallersOnly] private static RaptureHotbarModule* GetModule(UIModule* _)=>current.Module;
        [UnmanagedCallersOnly] private static void SetSlot(Slot* slot,UIModule* _,SlotType type,uint id){slot->CommandType=type;slot->CommandId=id;}
        public void Dispose()
        {
            NativeFramework.Addresses.Instance.Value=previousFramework;NativeFramework.Addresses.GetUIModule.Value=previousUi;
            Control.Addresses.GetLocalPlayer.Value=previousLocal;Slot.Addresses.Set.Value=previousSet;
            foreach(var p in allocations)NativeMemory.Free((void*)p);Framework.Dispose();
        }
    }
    private static void Set(object target,string field,object? value)=>target.GetType().GetField(field,All)!.SetValue(target,value);

    private static object SafeHook(Type parent,Action onDispose)
    {
        // A managed-only Hook subclass overrides disposal; it never creates a backend,
        // patches executable memory or runs a game function. The actual bridge owns it.
        var assembly=AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("NativeHookFixture"+Guid.NewGuid().ToString("N")),AssemblyBuilderAccess.Run);
        var module=assembly.DefineDynamicModule("fixture");
        var ignore=module.DefineType("System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",TypeAttributes.Public,typeof(Attribute));
        var ctor=ignore.DefineConstructor(MethodAttributes.Public,CallingConventions.Standard,[typeof(string)]);
        var il=ctor.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Call,typeof(Attribute).GetConstructor(All,null,Type.EmptyTypes,null)!);il.Emit(OpCodes.Ret);
        var ignoreType=ignore.CreateType()!;
        foreach(var name in new[]{"Dalamud","FireGaze.LocalPreview"})assembly.SetCustomAttribute(new CustomAttributeBuilder(ignoreType.GetConstructor([typeof(string)])!,[name]));
        var type=module.DefineType("ManagedOnlyHook",TypeAttributes.Public,parent);
        var callback=type.DefineField("OnDispose",typeof(Action),FieldAttributes.Public);
        var dispose=type.DefineMethod("Dispose",MethodAttributes.Public|MethodAttributes.Virtual,typeof(void),Type.EmptyTypes);
        il=dispose.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,callback);il.Emit(OpCodes.Callvirt,typeof(Action).GetMethod("Invoke")!);il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Call,parent.GetMethod("Dispose")!);il.Emit(OpCodes.Ret);
        type.DefineMethodOverride(dispose,parent.GetMethod("Dispose")!);
        var construction=type.DefineConstructor(MethodAttributes.Public,CallingConventions.Standard,Type.EmptyTypes);
        il=construction.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldc_I4_0);il.Emit(OpCodes.Conv_I);il.Emit(OpCodes.Call,parent.GetConstructor(All,null,[typeof(nint)],null)!);il.Emit(OpCodes.Ret);
        var result=Activator.CreateInstance(type.CreateType()!)!;result.GetType().GetField("OnDispose")!.SetValue(result,onDispose);return result;
    }
}

internal sealed class QueuedFramework:IDisposable
{
    private readonly Queue<Action> pending=[];
    public readonly IFramework Service;
    public bool Inside;
    public Action? AfterCallback;
    public QueuedFramework()=>Service=NativeTestProxy.Create<IFramework>((m,args)=>
    {
        if(m.Name=="get_IsInFrameworkUpdateThread")return Inside;
        if(m.Name.StartsWith("remove_",StringComparison.Ordinal))return null;
        if(m.Name!="RunOnFrameworkThread")throw new NotSupportedException(m.Name);
        if(m.IsGenericMethod)return GetType().GetMethod(nameof(Schedule),BindingFlags.NonPublic|BindingFlags.Instance)!.MakeGenericMethod(m.GetGenericArguments()).Invoke(this,[args![0]]);
        return Enqueue((Action)args![0]!);
    });
    private Task<T> Schedule<T>(Func<T> action)
    {
        var completion=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(pending)pending.Enqueue(()=>{Inside=true;try{var value=action();AfterCallback?.Invoke();completion.SetResult(value);}catch(Exception ex){completion.SetException(ex);}finally{Inside=false;}});
        return completion.Task;
    }
    private Task Enqueue(Action action)=>Schedule(()=>{action();return true;});
    public void WaitForDispatchOrCompletion(Task worker)
    {
        if(!SpinWait.SpinUntil(()=>{lock(pending)return pending.Count!=0 || worker.IsCompleted;},TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Framework cleanup was neither dispatched nor completed.");
    }
    public void Drain(){while(true){Action? action;lock(pending){if(!pending.TryDequeue(out action))return;}action();}}
    public void Dispose(){ }
}



internal sealed unsafe class GameGuiFixture(Func<nint> resolve):IGameGui
{
    public bool GameUiHidden=>false;
    public ulong HoveredItem{get;set;}
    public Dalamud.Game.Gui.HoveredAction HoveredAction=>default!;
    public event EventHandler<bool> UiHideToggled{add{}remove{}}
    public event EventHandler<ulong> HoveredItemChanged{add{}remove{}}
    public event EventHandler<Dalamud.Game.Gui.HoveredAction> HoveredActionChanged{add{}remove{}}
    public event Action<Dalamud.Game.Gui.AgentUpdateFlag> AgentUpdate{add{}remove{}}
    public bool OpenMapWithMapLink(Dalamud.Game.Text.SeStringHandling.Payloads.MapLinkPayload link)=>throw new NotSupportedException();
    public bool OpenMapWithMapLink(uint territory,uint map,System.Numerics.Vector3 pos)=>throw new NotSupportedException();
    public bool WorldToScreen(System.Numerics.Vector3 pos,out System.Numerics.Vector2 screen)=>throw new NotSupportedException();
    public bool WorldToScreen(System.Numerics.Vector3 pos,out System.Numerics.Vector2 screen,out bool inView)=>throw new NotSupportedException();
    public bool ScreenToWorld(System.Numerics.Vector2 pos,out System.Numerics.Vector3 world,float distance=100000f)=>throw new NotSupportedException();
    public Dalamud.Game.NativeWrapper.UIModulePtr GetUIModule()=>throw new NotSupportedException();
    public Dalamud.Game.NativeWrapper.AtkUnitBasePtr GetAddonByName(string name,int index=1)=>new(resolve());
    public Dalamud.Game.NativeWrapper.AtkUnitBasePtr GetAddonByName(ReadOnlySpan<byte> name,int index=1)=>new(resolve());
    public T* GetAddonByName<T>(string name,int index=1)where T:unmanaged=>(T*)resolve();
    public T* GetAddonByName<T>(ReadOnlySpan<byte> name,int index=1)where T:unmanaged=>(T*)resolve();
    public Dalamud.Game.NativeWrapper.AgentInterfacePtr GetAgentById(int id)=>throw new NotSupportedException();
    public Dalamud.Game.NativeWrapper.AgentInterfacePtr FindAgentInterface(string name)=>throw new NotSupportedException();
    public Dalamud.Game.NativeWrapper.AgentInterfacePtr FindAgentInterface(ReadOnlySpan<byte> name)=>throw new NotSupportedException();
    public Dalamud.Game.NativeWrapper.AgentInterfacePtr FindAgentInterface(Dalamud.Game.NativeWrapper.AtkUnitBasePtr addon)=>throw new NotSupportedException();
}
