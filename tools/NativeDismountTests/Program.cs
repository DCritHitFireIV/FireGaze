using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

// Executes the compiled production detours against private allocations and managed originals.
// No game process, native hook installation, or real client action is involved.
unsafe class Program
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly Type RuntimeType = typeof(ActorPreviewRuntime);
    static F current = null!;
    static void Eq<T>(T got, T expected, string why) { if (!EqualityComparer<T>.Default.Equals(got, expected)) throw new Exception($"{why}: expected {expected}; got {got}"); }
    static int Main()
    {
        (string, Action)[] cases = [
            ("general-action23 locally exits only the owned mount", () => Local(ActionType.GeneralAction,23)),
            ("mount0 locally exits only the owned mount", () => Local(ActionType.Mount,0)),
            ("nearby general actions are forwarded unchanged once", OtherGeneral),
            ("actual mounted baseline uses native dismount once", RealBaseline),
            ("new genuine mount is never swallowed", GenuineMount),
            ("old mounted baseline without real mount is not a local trial exit", OldBaseline),
            ("no owned mount does not consume general-action23", NoMount),
            ("stale territory does not consume general-action23", StaleTerritory),
            ("reused display actor cannot consume general-action23", ReusedDisplay),
            ("nested native transaction cannot become local cancellation", Nested),
            ("status checks retain native rejection and never cancel", Status),
            ("single mount cancellation releases the display actor", SingleMount),
            ("owned-call bypass does not cancel a local mount", OwnCall),
            ("display mount mismatch does not consume input", DisplayMismatch),
            ("lookup reentry cannot cancel an obsolete local preview", LookupReentry),
            ("accepted companion leaf removes all preview ownership", CompanionAccepted),
            ("rejected companion leaf preserves preview ownership", CompanionRejected),
            ("owned companion leaf bypass preserves preview ownership", CompanionOwnCall),
            ("accepted nested companion leaf commits despite outer false", CompanionNested),
            ("queued companion outer true without leaf preserves preview", CompanionQueued),
            ("companion true without normal execution evidence preserves preview", CompanionTrueWithoutExecution),
            ("companion rejection after cooldown change preserves preview", CompanionRejectedCooldown),
        ];
        int failed=0; foreach(var (name,test) in cases) try { test(); Console.WriteLine("PASS "+name); } catch(Exception e) { failed++; while(e is TargetInvocationException {InnerException:{} inner}) e=inner; Console.WriteLine("FAIL "+name+": "+e.Message); }
        Console.WriteLine($"{cases.Length-failed}/{cases.Length} production dismount boundary cases passed"); return failed==0?0:1;
    }
    static void Local(ActionType type,uint id)
    {
        using var f=new F(); byte[] real=new ReadOnlySpan<byte>(f.Source,sizeof(Character)).ToArray();
        Eq(f.Use(type,id),true,"local cancellation accepted"); Eq(f.OriginalCalls,0,"native dismount not attempted or replayed");
        Eq(f.Runtime.ActiveEffects.Count,1,"unrelated emote retained"); Eq(f.Runtime.ActiveEffects[0].Slot,PreviewSlot.Emote,"emote retained");
        Eq(f.Clone->Mount.MountId,(ushort)0,"display mount removed"); Eq(f.Clone->EmoteController.EmoteId,(ushort)71,"display emote retained");
        Eq(f.MountCalls,1,"one display teardown"); Eq(f.SourceMountCalls,0,"real container untouched");
        Eq(new ReadOnlySpan<byte>(f.Source,sizeof(Character)).SequenceEqual(real),true,"all real player bytes unchanged");
        Eq(f.NativeAccepted,0,"local exit is not reported as native acceptance"); Eq(f.Cancelled,1,"local exit reported once");
    }
    static void OtherGeneral() { using var f=new F(); foreach(uint id in new uint[]{0,1,22,24,ushort.MaxValue}) { Eq(f.Use(ActionType.GeneralAction,id),false,"native result"); Eq(f.LastId,id,"exact id"); } Eq(f.OriginalCalls,5,"each original once"); Eq(f.Runtime.ActiveEffects.Count,2,"preview retained"); }
    static void RealBaseline() { using var f=new F(42,42); f.NativeResult=true; f.NativeClearsMount=true; Eq(f.Use(ActionType.GeneralAction,23),true,"native result"); Eq(f.OriginalCalls,1,"one native dismount"); Eq(f.Source->Mount.MountId,(ushort)0,"new native state kept"); Eq(f.Runtime.ActiveEffects.Count,2,"outer true is not execution evidence"); Eq(f.MountCalls,0,"no premature display rebuild from outer true"); }
    static void GenuineMount() { using var f=new F(0,42); f.NativeResult=false; Eq(f.Use(ActionType.GeneralAction,23),false,"native rejection retained"); Eq(f.OriginalCalls,1,"real mount forwarded once"); Eq(f.Runtime.ActiveEffects.Count,2,"rejection keeps preview"); }
    static void OldBaseline() { using var f=new F(42,0); Eq(f.Use(ActionType.GeneralAction,23),false,"native rejection retained"); Eq(f.OriginalCalls,1,"old mounted baseline forwarded"); Eq(f.Runtime.ActiveEffects.Count,2,"preview retained"); }
    static void NoMount() { using var f=new F(); f.Effects.Remove(PreviewSlot.Mount); Eq(f.Use(ActionType.GeneralAction,23),false,"native rejection"); Eq(f.OriginalCalls,1,"no local mount forwarded"); }
    static void StaleTerritory() { using var f=new F(); f.Territory=2; Eq(f.Use(ActionType.GeneralAction,23),false,"native result"); Eq(f.OriginalCalls,1,"stale binding forwarded"); }
    static void ReusedDisplay() { using var f=new F(); f.Manager->BattleCharas[207].BattleChara=null; Eq(f.Use(ActionType.GeneralAction,23),false,"native result"); Eq(f.OriginalCalls,1,"lost ownership forwarded"); Eq(f.MountCalls,0,"reused slot untouched"); }
    static void Nested() { using var f=new F(); Set(f.Runtime,"nativeDepth",1); Eq(f.Use(ActionType.GeneralAction,23),false,"native result"); Eq(f.OriginalCalls,1,"nested call forwarded"); Eq(f.MountCalls,0,"nested input not consumed"); }
    static void Status() { using var f=new F(); Eq(f.Status(ActionType.GeneralAction,23),0x243u,"native not-mounted status preserved"); Eq(f.StatusCalls,1,"status original once"); Eq(f.Runtime.ActiveEffects.Count,2,"preflight never cancels"); }
    static void SingleMount() { using var f=new F(); f.Effects.Remove(PreviewSlot.Emote); Eq(f.Use(ActionType.GeneralAction,23),true,"local exit"); Eq(f.Runtime.HasActivePreview,false,"all ownership ended"); Eq(f.Deletes,1,"owned display released once"); Eq(f.OriginalCalls,0,"no native input replay"); }
    static void OwnCall() { using var f=new F(); Set(f.Runtime,"ownCall",true); Eq(f.Use(ActionType.GeneralAction,23),false,"original result"); Eq(f.OriginalCalls,1,"original once"); Eq(f.Runtime.ActiveEffects.Count,2,"owned call preserved preview"); }
    static void DisplayMismatch() { using var f=new F(); f.Clone->Mount.MountId=447; Eq(f.Use(ActionType.GeneralAction,23),false,"original result"); Eq(f.OriginalCalls,1,"original once"); Eq(f.MountCalls,0,"changed display not torn down"); }
    static void LookupReentry() { using var f=new F(); f.ReenterLookup=true; Eq(f.Use(ActionType.GeneralAction,23),false,"stale binding forwarded"); Eq(f.OriginalCalls,1,"one original"); Eq(f.MountCalls,0,"obsolete display not torn down"); }
    static void CompanionAccepted() { using var f=new F(); f.PrepareCompanion(true); Eq(f.Companion(41),true,"leaf result"); Eq(f.LeafCalls,1,"one leaf execution"); Eq(f.LastLeafId,41u,"exact companion id"); Eq(f.Runtime.HasActivePreview,false,"all previews ended"); Eq(f.NativeAccepted,1,"one accepted event"); Eq(f.Deletes,1,"display released"); }
    static void CompanionRejected() { using var f=new F(); f.PrepareCompanion(false); Eq(f.Companion(41),false,"native rejection"); Eq(f.LeafCalls,1,"one leaf execution"); Eq(f.Runtime.ActiveEffects.Count,2,"all previews retained"); Eq(f.NativeAccepted,0,"no accepted event"); }
    static void CompanionOwnCall() { using var f=new F(); f.PrepareCompanion(true); Set(f.Runtime,"ownCall",true); Eq(f.Companion(41),true,"original leaf result"); Eq(f.LeafCalls,1,"one original leaf"); Eq(f.Runtime.ActiveEffects.Count,2,"owned call preserves previews"); }
    static void CompanionNested() { using var f=new F(); f.PrepareCompanion(true); f.NestedLeafId=41; Eq(f.Use(ActionType.Companion,41),false,"outer false preserved"); Eq(f.OriginalCalls,1,"outer invoked once"); Eq(f.LeafCalls,1,"nested leaf invoked once"); Eq(f.Runtime.HasActivePreview,false,"accepted nested leaf ends previews"); Eq(f.NativeAccepted,1,"one commit"); }
    static void CompanionQueued() { using var f=new F(); f.PrepareCompanion(false); f.NativeResult=true; Eq(f.Use(ActionType.Companion,41),true,"queue result preserved"); Eq(f.OriginalCalls,1,"outer once"); Eq(f.LeafCalls,0,"leaf was not executed"); Eq(f.Runtime.ActiveEffects.Count,2,"queue acceptance does not end preview"); }
    static void CompanionTrueWithoutExecution() { using var f=new F(); f.PrepareCompanion(true,false); Eq(f.Companion(41),true,"special branch true preserved"); Eq(f.LeafCalls,1,"one original"); Eq(f.Runtime.ActiveEffects.Count,2,"no normal execution evidence retains preview"); Eq(f.NativeAccepted,0,"no false commit"); }
    static void CompanionRejectedCooldown() { using var f=new F(); f.PrepareCompanion(false,true); Eq(f.Companion(71),false,"special branch rejection preserved"); Eq(f.LeafCalls,1,"one original"); Eq(f.ActionMgr->CompanionActionCooldown>0,true,"rejection branch really changed cooldown"); Eq(f.Runtime.ActiveEffects.Count,2,"cooldown alone cannot commit"); Eq(f.NativeAccepted,0,"no false commit"); }
    static void Set(object o,string name,object? value) => o.GetType().GetField(name,All)!.SetValue(o,value);
    static object Ptr(void* p,Type t)=>Pointer.Box(p,t.MakePointerType());
    static T Proxy<T>(Func<MethodInfo,object?[]?,object?> call) where T:class { var p=DispatchProxy.Create<T,ServiceProxy>(); ((ServiceProxy)(object)p).Call=call; return p; }
    static object Hook<T>(T original) where T:Delegate
    {
        var t=typeof(Hook<>).Assembly.GetType("Dalamud.Hooking.Internal.FunctionPointerVariableHook`1")!.MakeGenericType(typeof(T));
        var hook=RuntimeHelpers.GetUninitializedObject(t); Set(hook,"originalDelegate",original); return hook;
    }
    sealed class F : IDisposable
    {
        public readonly Character* Source=Alloc<Character>(), Clone=Alloc<Character>(); public readonly ClientObjectManager* Manager=Alloc<ClientObjectManager>();
        public readonly ActionManager* ActionMgr=Alloc<ActionManager>();
        readonly nint* table=(nint*)NativeMemory.AllocZeroed(0x300); readonly Character** local=(Character**)NativeMemory.AllocZeroed((nuint)sizeof(nint)); public readonly ActorPreviewRuntime Runtime;
        public Dictionary<PreviewSlot,PreviewEffect> Effects=new(); public uint Territory=1; public int OriginalCalls,StatusCalls,MountCalls,SourceMountCalls,NativeAccepted,Cancelled,Deletes,LeafCalls; public uint LastId,LastLeafId,NestedLeafId; public bool NativeResult,NativeClearsMount,LeafResult,LeafChangesCooldown,ReenterLookup;
        public F(ushort baselineMount=0,ushort realMount=0)
        {
            current=this; *(nint**)Source=table; *(nint**)Clone=table; table[1]=(nint)(delegate* unmanaged<GameObject*,ulong>)&Identity;
            Source->EntityId=500; Source->ObjectIndex=0; Clone->EntityId=700; Clone->ObjectIndex=207; Source->Mount.OwnerObject=Source; Clone->Mount.OwnerObject=Clone;
            Source->Mount.MountId=baselineMount; Clone->Mount.MountId=446; Clone->EmoteController.EmoteId=71;
            Manager->BattleCharas[207].BattleChara=(BattleChara*)Clone;
            ClientObjectManager.Addresses.Instance.Value=(nint)Manager;
            ClientObjectManager.Addresses.GetObjectByIndex.Value=(nint)(delegate* unmanaged<ClientObjectManager*,ushort,Character*>)&Lookup;
            ClientObjectManager.Addresses.DeleteObjectByIndex.Value=(nint)(delegate* unmanaged<ClientObjectManager*,ushort,byte,void>)&Delete;
            *local=Source; Control.Addresses.GetLocalPlayer.Value=(nint)local;
            MountContainer.Addresses.CreateAndSetupMount.Value=(nint)(delegate* unmanaged<MountContainer*,short,uint,uint,uint,byte,byte,byte,void>)&Mount;
            Runtime=(ActorPreviewRuntime)RuntimeHelpers.GetUninitializedObject(RuntimeType);
            var player=Proxy<IPlayerCharacter>((m,a)=>m.Name switch {"get_Address"=>(nint)Source,"get_GameObjectId"=>500UL,_=>m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null});
            Set(Runtime,"objects",Proxy<IObjectTable>((m,a)=>m.Name=="get_LocalPlayer"?player:null));
            Set(Runtime,"clientState",Proxy<IClientState>((m,a)=>m.Name switch {"get_IsLoggedIn"=>true,"get_TerritoryType"=>Territory,_=>null}));
            Set(Runtime,"actorAddress",(nint)Source); Set(Runtime,"actorId",500UL); Set(Runtime,"territory",1u);
            Set(Runtime,"effects",Effects); Effects[PreviewSlot.Mount]=new(PreviewSlot.Mount,446); Effects[PreviewSlot.Emote]=new(PreviewSlot.Emote,71);
            Set(Runtime,"spawnWait",new Dictionary<PreviewSlot,int>()); Set(Runtime,"implicitMinion",new ImplicitMinionOwnership());
            Set(Runtime,"hmsEvidence",Activator.CreateInstance(RuntimeType.GetField("hmsEvidence",All)!.FieldType));
            var intent=RuntimeType.GetNestedType("NativeIntent",All)!; Set(Runtime,"handoff",Activator.CreateInstance(typeof(NativeInputHandoff<>).MakeGenericType(intent)));
            var snapshot=RuntimeType.GetNestedType("ActorSnapshot",All)!; Set(Runtime,"baseline",snapshot.GetMethod("Read",All)!.Invoke(null,[Ptr(Source,typeof(Character)),false]));
            Source->Mount.MountId=realMount;
            // Constructor only records pointers and ids; no native actor is created.
            Set(Runtime,"visualActor",typeof(NativePreviewActor).GetConstructors(All).Single().Invoke([Ptr(Manager,typeof(ClientObjectManager)),(ushort)207,Ptr(Clone,typeof(Character)),Ptr(Source,typeof(Character)),null]));
            ActionManager.Delegates.UseAction original=(mgr,type,id,target,extra,mode,combo,area)=> {OriginalCalls++;LastId=id;if(NativeClearsMount)Source->Mount.MountId=0;if(NestedLeafId!=0)Companion(NestedLeafId);return NativeResult;};
            ActionManager.Delegates.GetActionStatus status=(mgr,type,id,target,recast,casting,info)=>{StatusCalls++;return 0x243;};
            Set(Runtime,"useAction",Hook(original)); Set(Runtime,"actionStatus",Hook(status));
            Runtime.NativeInputAccepted+=()=>NativeAccepted++; Runtime.PreviewCancelled+=_=>Cancelled++;
        }
        public bool Use(ActionType type,uint id)=>(bool)RuntimeType.GetMethod("UseActionDetour",All)!.Invoke(Runtime,[Ptr(ActionMgr,typeof(ActionManager)),type,id,0UL,0u,ActionManager.UseActionMode.None,0u,Ptr(null,typeof(bool))])!;
        public uint Status(ActionType type,uint id)=>(uint)RuntimeType.GetMethod("ActionStatusDetour",All)!.Invoke(Runtime,[Ptr(null,typeof(ActionManager)),type,id,0UL,false,false,Ptr(null,typeof(uint))])!;
        public void PrepareCompanion(bool result,bool? changesCooldown=null)
        {
            LeafResult=result; LeafChangesCooldown=changesCooldown??result;
            var delegateType=RuntimeType.GetNestedType("UseCompanionNative",All) ?? throw new Exception("production companion leaf boundary is missing");
            var original=Delegate.CreateDelegate(delegateType,this,GetType().GetMethod(nameof(CompanionOriginal),All)!);
            var hook=typeof(Program).GetMethod(nameof(Hook),All)!.MakeGenericMethod(delegateType).Invoke(null,[original]);
            Set(Runtime,"useCompanion",hook);
        }
        bool CompanionOriginal(ActionManager* mgr,uint id) { LeafCalls++;LastLeafId=id;if(LeafChangesCooldown)mgr->CompanionActionCooldown=1;return LeafResult; }
        public bool Companion(uint id)=>(bool)(RuntimeType.GetMethod("UseCompanionDetour",All) ?? throw new Exception("production companion leaf detour is missing")).Invoke(Runtime,[Ptr(ActionMgr,typeof(ActionManager)),id])!;
        public void Dispose() { NativeMemory.Free(Source);NativeMemory.Free(Clone);NativeMemory.Free(Manager);NativeMemory.Free(table);NativeMemory.Free(local);NativeMemory.Free(ActionMgr); }
        static T* Alloc<T>() where T:unmanaged=>(T*)NativeMemory.AllocZeroed((nuint)sizeof(T));
    }
    [UnmanagedCallersOnly] static ulong Identity(GameObject* obj)=>obj->EntityId;
    [UnmanagedCallersOnly] static Character* Lookup(ClientObjectManager* mgr,ushort slot) { if(current.ReenterLookup) {current.ReenterLookup=false;current.Territory=2;} return (Character*)mgr->BattleCharas[slot].BattleChara; }
    [UnmanagedCallersOnly] static void Delete(ClientObjectManager* mgr,ushort slot,byte param) { current.Deletes++;mgr->BattleCharas[slot].BattleChara=null; }
    [UnmanagedCallersOnly] static void Mount(MountContainer* container,short id,uint top,uint body,uint legs,byte stain,byte unk6,byte unk7) {current.MountCalls++;if(container->OwnerObject==current.Source)current.SourceMountCalls++;container->MountId=(ushort)id;}
}
public class ServiceProxy : DispatchProxy
{
    public Func<MethodInfo,object?[]?,object?> Call=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>Call(method!,args);
}
