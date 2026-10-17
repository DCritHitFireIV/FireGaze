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
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
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
            ("first native mount input is consumed and second input dispatches once", MountIntentRejected),
            ("native collection switch consumes first input during accessory trial", AccessoryFirstInput),
            ("minion trial clears only incompatible display accessory before setup", MinionDisplayConflict),
            ("accessory trial clears live display minion even when container id is zero", () => AccessoryDisplayConflict(0,true,255)),
            ("accessory trial clears occupied display minion with no resolved base id", () => AccessoryDisplayConflict(0,true,0)),
            ("accessory trial clears pending display minion before setup", () => AccessoryDisplayConflict(255,false,0)),
            ("accessory trial skips minion teardown when display container is empty", () => AccessoryDisplayConflict(0,false,0)),
            ("queued acknowledgement of active user mount intent still ends every trial", MountIntentQueued),
            ("macro requested mount ends every trial without inventing native acceptance", MountIntentMacro),
            ("automatic queued mount replay does not cancel a later trial on rejection", MountQueueReplay),
            ("queued mount replay reaching a rejected location leaf preserves later trial", MountQueueLocationRejected),
            ("rejected immediate mount location leaf preserves every trial", MountLocationRejected),
            ("mount roulette9 without a valid candidate ends every trial", () => RandomMountRejected(9)),
            ("flying mount roulette24 without a valid candidate ends every trial", () => RandomMountRejected(24)),
            ("random mount dispatcher and nested mount paths cancel only once", RandomMountNested),
            ("queued random selector with default-mode nested mount preserves later trials on rejection", RandomMountQueueNestedRejected),
            ("internal rejected mount dispatch cannot cancel a user trial", MountIntentNested),
            ("owned mount dispatch cannot cancel a user trial", MountIntentOwned),
            ("mount request without an active mount trial keeps other rejected trials", MountIntentWithoutTrial),
            ("stale territory mount intent cannot clear unrelated preview ownership", MountIntentStale),
            ("lost display ownership does not authorize mount intent cancellation", MountIntentReused),
            ("nonzero mount status preflight never cancels a user trial", MountIntentStatus),
            ("status inspection reentering rejected mount paths cannot imply user intent", MountIntentStatusReentry),
            ("owned actor lookup reentry cannot steal outer mount intent cancellation", MountIntentLookupReentry),
            ("owned actor lookup invalidation revokes mount intent cancellation", MountIntentLookupStale),
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
            ("real container dismount clears clone without resurrecting mounted baseline", NativeSetupDismount),
            ("real container dismount releases last display slot", NativeSetupLastSlot),
            ("real container callback cannot clear a replacement preview", NativeSetupReplacement),
            ("real container callback cannot clear a stale territory preview", NativeSetupTerritory),
            ("real container lookup reentry cannot clear obsolete display", NativeSetupLookup),
            ("real container dismount never tears down a changed display mount", NativeSetupChangedDisplay),
            ("owned container call does not relinquish preview", NativeSetupOwned),
            ("accepted mount0 handoff clears only clone mount without restoring baseline", LocationDismount),
            ("rejected mount0 handoff retains display preview", LocationRejected),
            ("accepted nonzero mount handoff still ends all preview slots", LocationMountAccepted),
            ("display teardown reentry cannot erase a new clone mount", DisplayTeardownReplacement),
            ("display teardown reentry cannot erase a new real mount", DisplayTeardownNativeWinner),
            ("display teardown preserves an unrelated emote posture", DisplayTeardownEmoteMode),
            ("display teardown uses current real mode instead of mounted baseline", DisplayTeardownCurrentMode),
            ("native dismount followed by another trial never restores historical mount", DismountThenTrial),
            ("native dismount refreshes posture and timeline used by later emote clear", DismountThenEmoteClear),
            ("transitional mounted source normalizes snapshot without writing real state", DismountTransitionalMode),
            ("weapon visibility gate suppresses each leased source slot after original exactly once", WeaponGateOwned),
            ("extra weapon visibility gate maps index zero to the fourth leased slot", ExtraWeaponGateOwned),
            ("weapon visibility gates preserve native false results", WeaponGateNativeFalse),
            ("weapon visibility gates preserve clone foreign and invalid-index queries", WeaponGateForeign),
            ("weapon visibility gates forward without a visual actor", WeaponGateNoActor),
            ("weapon visibility gates forward when runtime is disposed", WeaponGateDisposedRuntime),
            ("weapon visibility gates forward when display actor is disposed", WeaponGateDisposedActor),
            ("weapon visibility gates forward before display readiness", WeaponGateUnready),
            ("weapon visibility gates revalidate ownership after original executes", WeaponGateOriginalInvalidation),
            ("worker collection input passes original without cancelling a trial", WorkerCollection),
            ("worker dismount input cannot tear down the display", WorkerDismount),
            ("worker special input never calls the local action handler", WorkerSpecial),
            ("worker companion execution leaves preview ownership unchanged", WorkerCompanion),
            ("worker native mount setup never releases the display", WorkerSetup),
            ("worker location acceptance does not mutate preview state", WorkerLocation),
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
    static void WorkerCollection()
    {
        using var f=new F();f.FrameworkThread=false;
        Eq(f.Use(ActionType.Mount,1),false,"native result preserved");
        Eq(f.OriginalCalls,1,"original invoked exactly once");
        Eq(f.Runtime.ActiveEffects.Count,2,"trial remains owned");
        Eq(f.Runtime.NativeRequestsConsumed,0L,"worker call is not deliberate user input");
        Eq(f.Deletes,0,"no display teardown");
    }
    static void WorkerDismount()
    {
        using var f=new F();f.FrameworkThread=false;
        Eq(f.Use(ActionType.GeneralAction,23),false,"native rejection retained");
        Eq(f.OriginalCalls,1,"original invoked exactly once");
        Eq(f.Runtime.ActiveEffects.Count,2,"worker never edits effects");
        Eq(f.MountCalls,0,"display mount not changed");
    }
    static void WorkerSpecial()
    {
        using var f=new F();f.FrameworkThread=false;var localCalls=0;
        f.Runtime.SpecialActionInput=_=>{localCalls++;return true;};
        Eq(f.Use(ActionType.Action,20122),false,"native result retained");
        Eq(localCalls,0,"no Framework-only local handler invocation");
        Eq(f.OriginalCalls,1,"original invoked exactly once");
    }
    static void WorkerCompanion()
    {
        using var f=new F();f.FrameworkThread=false;f.PrepareCompanion(true);
        Eq(f.Companion(255),true,"native leaf accepted");
        Eq(f.LeafCalls,1,"original leaf invoked exactly once");
        Eq(f.Runtime.ActiveEffects.Count,2,"worker acceptance cannot change effects");
        Eq(f.NativeAccepted,0,"no worker mutation notification");
    }
    static void WorkerSetup()
    {
        using var f=new F();f.FrameworkThread=false;f.Setup(0);
        Eq(f.SetupCalls,1,"native setup invoked exactly once");
        Eq(f.Runtime.ActiveEffects.Count,2,"worker setup cannot clear preview ownership");
        Eq(f.MountCalls,0,"no local mount teardown");
    }
    static void WorkerLocation()
    {
        using var f=new F();f.FrameworkThread=false;f.LocationResult=true;
        Eq(f.Location(ActionType.Mount,1),true,"native location accepted");
        Eq(f.LocationCalls,1,"original invoked exactly once");
        Eq(f.Runtime.ActiveEffects.Count,2,"worker location does not suspend or restore");
        Eq(f.NativeAccepted,0,"no worker acceptance notification");
    }
    static void OtherGeneral() { using var f=new F(); foreach(uint id in new uint[]{0,1,22,ushort.MaxValue}) { Eq(f.Use(ActionType.GeneralAction,id),false,"native result"); Eq(f.LastId,id,"exact id"); } Eq(f.OriginalCalls,4,"each original once"); Eq(f.Runtime.ActiveEffects.Count,2,"preview retained"); }
    static void RealBaseline() { using var f=new F(42,42); f.NativeResult=true; f.NativeClearsMount=true; Eq(f.Use(ActionType.GeneralAction,23),true,"native result"); Eq(f.OriginalCalls,1,"one native dismount"); Eq(f.Source->Mount.MountId,(ushort)0,"new native state kept"); Eq(f.Runtime.ActiveEffects.Count,2,"outer true is not execution evidence"); Eq(f.MountCalls,0,"no premature display rebuild from outer true"); }
    static void GenuineMount() { using var f=new F(0,42); f.NativeResult=false; Eq(f.Use(ActionType.GeneralAction,23),false,"native rejection retained"); Eq(f.OriginalCalls,1,"real mount forwarded once"); Eq(f.Runtime.ActiveEffects.Count,2,"rejection keeps preview"); }
    static void OldBaseline() { using var f=new F(42,0); Eq(f.Use(ActionType.GeneralAction,23),false,"native rejection retained"); Eq(f.OriginalCalls,1,"old mounted baseline forwarded"); Eq(f.Runtime.ActiveEffects.Count,2,"preview retained"); }
    static void NoMount() { using var f=new F(); f.Effects.Remove(PreviewSlot.Mount); Eq(f.Use(ActionType.GeneralAction,23),false,"native rejection"); Eq(f.OriginalCalls,1,"no local mount forwarded"); }
    static void StaleTerritory() { using var f=new F(); f.Territory=2; Eq(f.Use(ActionType.GeneralAction,23),false,"native result"); Eq(f.OriginalCalls,1,"stale binding forwarded"); }
    static void ReusedDisplay() { using var f=new F(); f.Manager->BattleCharas[207].BattleChara=null; Eq(f.Use(ActionType.GeneralAction,23),false,"native result"); Eq(f.OriginalCalls,1,"lost ownership forwarded"); Eq(f.MountCalls,0,"reused slot untouched"); }
    static void Nested() { using var f=new F(); Set(f.Runtime,"nativeDepth",1); Eq(f.Use(ActionType.GeneralAction,23),false,"native result"); Eq(f.OriginalCalls,1,"nested call forwarded"); Eq(f.MountCalls,0,"nested input not consumed"); }
    static void Status() { using var f=new F(); Eq(f.Status(ActionType.GeneralAction,23),0x243u,"native not-mounted status preserved"); Eq(f.StatusCalls,1,"status original once"); Eq(f.Runtime.ActiveEffects.Count,2,"preflight never cancels"); }
    static void MountIntentRejected()
    {
        using var f=new F(42,0);byte[] real=new ReadOnlySpan<byte>(f.Source,sizeof(Character)).ToArray();
        Eq(f.Use(ActionType.Mount,55,ActionManager.UseActionMode.None,987UL,17,29),true,"first input consumed locally");
        Eq(f.OriginalCalls,0,"first input cannot reach native mount execution");
        Eq(f.Runtime.HasActivePreview,false,"first input ends all trial slots");Eq(f.Deletes,1,"display released once");Eq(f.NativeAccepted,0,"consumed input is not native acceptance");Eq(f.Cancelled,1,"cancellation reported once");
        Eq(new ReadOnlySpan<byte>(f.Source,sizeof(Character)).SequenceEqual(real),true,"cancellation leaves all source bytes unchanged");
        Eq(f.Use(ActionType.Mount,55,ActionManager.UseActionMode.None,987UL,17,29),false,"second input gets native rejection");
        Eq(f.OriginalCalls,1,"second input dispatched exactly once");Eq(f.LastType,ActionType.Mount,"action type preserved");Eq(f.LastId,55u,"requested mount preserved");
        Eq(f.LastTarget,987UL,"native target preserved");Eq(f.LastExtra,17u,"extra parameter preserved");Eq(f.LastCombo,29u,"combo parameter preserved");Eq(f.LastMode,ActionManager.UseActionMode.None,"mode preserved");
        Eq(f.Runtime.HasActivePreview,false,"rejected user intent ends all trial slots");Eq(f.Deletes,1,"display released once");Eq(f.NativeAccepted,0,"rejection is not native acceptance");Eq(f.Cancelled,1,"intent cancellation reported once");
        Eq(new ReadOnlySpan<byte>(f.Source,sizeof(Character)).SequenceEqual(real),true,"intent cancellation cannot restore historical mounted snapshot over real source");Eq(f.SourceMountCalls,0,"real mount container never modified");
    }
    static void AccessoryFirstInput()
    {
        foreach(var type in new[]{ActionType.Mount,ActionType.Ornament,ActionType.Companion})
        {
            using var f=new F();f.Effects.Remove(PreviewSlot.Mount);f.Effects[PreviewSlot.Accessory]=new(PreviewSlot.Accessory,5);
            Eq(f.Use(type,17),true,"first collection input consumed while trying accessory");Eq(f.OriginalCalls,0,"native use not invoked");Eq(f.Runtime.HasActivePreview,false,"all previews ended");
            Eq(f.Use(type,17),false,"second collection input gets native result");Eq(f.OriginalCalls,1,"second input native once");
        }
    }
    static void MinionDisplayConflict()
    {
        using var f=new F();f.Effects.Clear();f.Clone->Mount.MountId=0;f.Clone->OrnamentData.OwnerObject=f.Clone;f.Clone->OrnamentData.OrnamentId=41;
        f.Source->OrnamentData.OrnamentId=41;f.Clone->CompanionData.OwnerObject=f.Clone;
        CompanionContainer.Addresses.SetupCompanion.Value=(nint)(delegate* unmanaged<CompanionContainer*,short,uint,void>)&TrialCompanion;
        OrnamentContainer.Addresses.SetupOrnament.Value=(nint)(delegate* unmanaged<OrnamentContainer*,short,uint,void>)&TrialOrnament;
        RuntimeType.GetMethod("Apply",All)!.Invoke(f.Runtime,[Ptr(f.Clone,typeof(Character)),new PreviewEffect(PreviewSlot.Minion,255)]);
        Eq(f.Clone->OrnamentData.OrnamentId,(ushort)0,"incompatible display accessory removed");
        Eq(f.Clone->CompanionData.CompanionId,(ushort)255,"native minion setup accepts after display accessory removal");
        Eq(f.Source->OrnamentData.OrnamentId,(ushort)41,"genuine accessory unchanged");
    }
    [UnmanagedCallersOnly] static void TrialCompanion(CompanionContainer* c,short id,uint p) { if(c->OwnerObject->OrnamentData.OrnamentId==0)c->CompanionId=(ushort)id; }
    [UnmanagedCallersOnly] static void TrialOrnament(OrnamentContainer* c,short id,uint p) { c->OrnamentId=(ushort)id; }
    static void AccessoryDisplayConflict(ushort metadata,bool hasObject,uint baseId)
    {
        using var f=new F();f.Effects.Clear();f.Clone->Mount.MountId=0;
        f.Source->CompanionData.OwnerObject=f.Source;f.Clone->CompanionData.OwnerObject=f.Clone;f.Clone->OrnamentData.OwnerObject=f.Clone;
        f.Source->CompanionData.CompanionObject=(Companion*)f.VisibilityForeignCharacter();f.Source->CompanionData.CompanionObject->BaseId=255;
        f.Clone->CompanionData.CompanionId=metadata;
        if(hasObject) { f.Clone->CompanionData.CompanionObject=(Companion*)f.VisibilityForeignCharacter();f.Clone->CompanionData.CompanionObject->BaseId=baseId; }
        byte[] real=new ReadOnlySpan<byte>(f.Source,sizeof(Character)).ToArray();
        nint setupCompanion=CompanionContainer.Addresses.SetupCompanion.Value,setupOrnament=OrnamentContainer.Addresses.SetupOrnament.Value;
        try
        {
            CompanionContainer.Addresses.SetupCompanion.Value=(nint)(delegate* unmanaged<CompanionContainer*,short,uint,void>)&ClearDisplayCompanion;
            OrnamentContainer.Addresses.SetupOrnament.Value=(nint)(delegate* unmanaged<OrnamentContainer*,short,uint,void>)&OrnamentAfterCompanion;
            RuntimeType.GetMethod("Apply",All)!.Invoke(f.Runtime,[Ptr(f.Clone,typeof(Character)),new PreviewEffect(PreviewSlot.Accessory,56)]);
            Eq(f.Clone->CompanionData.CompanionObject==null,true,"incompatible display minion object removed");
            Eq(f.Clone->CompanionData.CompanionId,(ushort)0,"incompatible display minion metadata removed");
            Eq(f.CompanionClearCalls,hasObject||metadata!=0?1:0,"exactly one teardown only for an occupied display container");
            Eq(f.Clone->OrnamentData.OrnamentId,(ushort)56,"accessory setup accepts after incompatible display minion removal");
            Eq(new ReadOnlySpan<byte>(f.Source,sizeof(Character)).SequenceEqual(real),true,"genuine source minion and all source bytes unchanged");
        }
        finally { CompanionContainer.Addresses.SetupCompanion.Value=setupCompanion;OrnamentContainer.Addresses.SetupOrnament.Value=setupOrnament; }
    }
    [UnmanagedCallersOnly] static void ClearDisplayCompanion(CompanionContainer* c,short id,uint p)
    {
        if(id!=0)return;current.CompanionClearCalls++;c->CompanionId=0;c->CompanionObject=null;
    }
    [UnmanagedCallersOnly] static void OrnamentAfterCompanion(OrnamentContainer* c,short id,uint p)
    {
        if(c->OwnerObject->CompanionData.CompanionObject==null&&c->OwnerObject->CompanionData.CompanionId==0)c->OrnamentId=(ushort)id;
    }
    static void MountIntentQueued()
    {
        using var f=new F();f.NativeResult=true;
        Eq(f.Use(ActionType.Mount,55),true,"native queued acknowledgement preserved");Eq(f.OriginalCalls,0,"first input cannot queue native use");
        Eq(f.Runtime.HasActivePreview,false,"active user intent ends trials before queued execution");Eq(f.Deletes,1,"display released once");Eq(f.NativeAccepted,0,"outer queued true cannot invent execution evidence");
    }
    static void MountIntentMacro()
    {
        using var f=new F();Eq(f.Use(ActionType.Mount,55,ActionManager.UseActionMode.Macro),true,"macro input consumed");
        Eq(f.OriginalCalls,0,"macro not dispatched");Eq(f.Runtime.HasActivePreview,false,"macro is active user mount intent");Eq(f.NativeAccepted,0,"rejected macro is not accepted input");
    }
    static void MountQueueReplay()
    {
        foreach(bool result in new[]{false,true})
        {
            using var f=new F();f.NativeResult=result;
            Eq(f.Use(ActionType.Mount,55,ActionManager.UseActionMode.Queue),result,"queued replay result preserved");Eq(f.OriginalCalls,1,"queued replay dispatched once");Eq(f.LastMode,ActionManager.UseActionMode.Queue,"queue mode preserved");
            Eq(f.Runtime.ActiveEffects.Count,2,"automatic replay cannot cancel a new trial from intent alone");Eq(f.Deletes,0,"automatic replay keeps current display");Eq(f.NativeAccepted,0,"queued outer return is not execution proof");Eq(f.Cancelled,0,"automatic replay does not report active user cancellation");
        }
    }
    static void MountQueueLocationRejected()
    {
        using var f=new F();f.NestedLocationId=55;
        Eq(f.Use(ActionType.Mount,55,ActionManager.UseActionMode.Queue),false,"queued replay native rejection preserved");
        Eq(f.OriginalCalls,1,"outer replay once");Eq(f.LocationCalls,1,"nested native location leaf once");Eq(f.Runtime.ActiveEffects.Count,2,"rejected queued location cannot erase a later user trial");Eq(f.Deletes,0,"display retained after rejected replay");Eq(f.NativeAccepted,0,"no false native acceptance");
    }
    static void MountLocationRejected()
    {
        using var f=new F(42,0);byte[] real=new ReadOnlySpan<byte>(f.Source,sizeof(Character)).ToArray();
        f.LocationBefore=()=>Eq(f.Runtime.ActiveEffects.Count,2,"immediate leaf alone does not establish active user intent");
        Eq(f.Location(ActionType.Mount,55),false,"native location rejection preserved");Eq(f.LocationCalls,1,"native location original once");
        Eq(f.Runtime.ActiveEffects.Count,2,"rejected immediate leaf preserves every trial");Eq(f.Deletes,0,"rejected immediate leaf retains display");Eq(f.NativeAccepted,0,"rejection is not accepted input");Eq(f.Cancelled,0,"immediate leaf does not invent user cancellation");
        Eq(new ReadOnlySpan<byte>(f.Source,sizeof(Character)).SequenceEqual(real),true,"rejected direct location leaves every real source byte unchanged");
    }
    static void RandomMountRejected(uint id)
    {
        using var f=new F();f.NativeBefore=()=>Eq(f.Runtime.HasActivePreview,false,"random mount intent clears trials before candidate selection");
        Eq(f.Use(ActionType.GeneralAction,id),true,"random first input consumed");Eq(f.OriginalCalls,0,"random selector not dispatched");
        Eq(f.Runtime.HasActivePreview,false,"random mount user intent ends all trials even without candidate");Eq(f.Deletes,1,"random selector releases display once");Eq(f.Cancelled,1,"one random intent cancellation");Eq(f.NativeAccepted,0,"rejected random selector is not native acceptance");
    }
    static void RandomMountNested()
    {
        foreach(uint id in new uint[]{9,24})
        {
            using var f=new F();f.RandomNestedMountId=55;f.NestedLocationId=55;
            Eq(f.Use(ActionType.GeneralAction,id),true,"first random mount input consumed");Eq(f.OriginalCalls,0,"selector not dispatched");Eq(f.LocationCalls,0,"no location dispatch");
            Eq(f.Runtime.HasActivePreview,false,"random user intent ends every trial");Eq(f.Deletes,1,"nested concrete mount cannot delete display twice");Eq(f.Cancelled,1,"random and resolved mount share one intent cancellation");Eq(f.NativeAccepted,0,"nested rejected originals cannot invent acceptance");
        }
    }
    static void RandomMountQueueNestedRejected()
    {
        foreach(uint id in new uint[]{9,24})
        {
            using var f=new F();f.RandomNestedMountId=55;f.NestedLocationId=55;var dispatches=new List<(ActionType Type,uint Id,ActionManager.UseActionMode Mode)>();
            f.NativeBefore=()=>dispatches.Add((f.LastType,f.LastId,f.LastMode));
            Eq(f.Use(ActionType.GeneralAction,id,ActionManager.UseActionMode.Queue),false,"queued random selector rejection preserved");Eq(f.OriginalCalls,2,"queued selector and resolved mount originals each once");Eq(f.LocationCalls,1,"resolved immediate mount leaf once");
            Eq(dispatches.Count,2,"both queued selector and nested mount dispatched");Eq(dispatches[0],(ActionType.GeneralAction,id,ActionManager.UseActionMode.Queue),"outer queued random request forwarded unchanged");Eq(dispatches[1],(ActionType.Mount,55u,ActionManager.UseActionMode.None),"native selector can reset mode while dispatching its mount");
            Eq(f.Runtime.ActiveEffects.Count,2,"queued selector's nested default mode cannot masquerade as new user intent");Eq(f.Deletes,0,"rejected queued random replay preserves later display");Eq(f.Cancelled,0,"queued replay does not cancel user trials");Eq(f.NativeAccepted,0,"rejected immediate leaf does not report acceptance");
        }
    }
    static void MountIntentNested()
    {
        using var f=new F();Set(f.Runtime,"nativeDepth",1);
        Eq(f.Use(ActionType.Mount,55),false,"nested dispatcher rejection preserved");Eq(f.Location(ActionType.Mount,55),false,"nested location rejection preserved");
        Eq(f.OriginalCalls,1,"nested dispatch original once");Eq(f.LocationCalls,1,"nested location original once");Eq(f.Runtime.ActiveEffects.Count,2,"internal native paths do not imply new user intent");Eq(f.Deletes,0,"internal rejection retains display");Eq(f.Cancelled,0,"internal rejection not reported as user cancellation");
    }
    static void MountIntentOwned()
    {
        using var f=new F();Set(f.Runtime,"ownCall",true);
        Eq(f.Use(ActionType.Mount,55),false,"owned native result preserved");Eq(f.Location(ActionType.Mount,55),false,"owned location result preserved");
        Eq(f.OriginalCalls,1,"owned dispatcher once");Eq(f.LocationCalls,1,"owned location once");Eq(f.Runtime.ActiveEffects.Count,2,"owned native calls preserve user trials");Eq(f.Deletes,0,"owned calls cannot release current display");
    }
    static void MountIntentWithoutTrial()
    {
        using var f=new F();f.Effects.Remove(PreviewSlot.Mount);
        Eq(f.Use(ActionType.Mount,55),false,"native dispatcher rejection");Eq(f.Location(ActionType.Mount,55),false,"native location rejection");
        Eq(f.OriginalCalls,1,"dispatcher once");Eq(f.LocationCalls,1,"location once");Eq(f.Runtime.ActiveEffects.Count,1,"rejected mount request preserves unrelated emote when no mount is being tried");Eq(f.Runtime.ActiveEffects[0].Slot,PreviewSlot.Emote,"emote trial retained");Eq(f.Cancelled,0,"no mount intent cancellation without mount trial");
    }
    static void MountIntentStale()
    {
        using var f=new F();f.Territory=2;
        Eq(f.Use(ActionType.Mount,55),false,"stale dispatcher result");Eq(f.Location(ActionType.Mount,55),false,"stale location result");
        Eq(f.OriginalCalls,1,"stale dispatcher once");Eq(f.LocationCalls,1,"stale location once");Eq(f.Runtime.ActiveEffects.Count,2,"obsolete binding is not cancellation authority");Eq(f.Deletes,0,"stale display not touched");
    }
    static void MountIntentReused()
    {
        using var f=new F();f.Manager->BattleCharas[207].BattleChara=null;
        Eq(f.Use(ActionType.Mount,55),false,"unowned dispatcher result");Eq(f.Location(ActionType.Mount,55),false,"unowned location result");
        Eq(f.OriginalCalls,1,"unowned dispatcher once");Eq(f.LocationCalls,1,"unowned location once");Eq(f.Runtime.ActiveEffects.Count,2,"lost display identity does not authorize intent cleanup");Eq(f.Deletes,0,"unowned native slot not deleted");
    }
    static void MountIntentStatus()
    {
        using var f=new F();Eq(f.Status(ActionType.Mount,55),0x243u,"nonzero mount native status unchanged");Eq(f.StatusCalls,1,"native preflight once");
        Eq(f.Runtime.ActiveEffects.Count,2,"status query is not active user mount intent");Eq(f.Deletes,0,"status query preserves display");Eq(f.Cancelled,0,"status query does not report cancellation");Eq(f.NativeAccepted,0,"status query cannot accept native input");
    }
    static void MountIntentStatusReentry()
    {
        using var f=new F();f.StatusNestedId=55;
        Eq(f.Status(ActionType.Mount,55),0x243u,"native status result preserved after nested probes");Eq(f.StatusCalls,1,"status original once");
        Eq(f.OriginalCalls,1,"status nested dispatcher once");Eq(f.LocationCalls,1,"status nested location once");Eq(f.LastId,55u,"status nested mount request preserved");
        Eq(f.Runtime.ActiveEffects.Count,2,"status-inspection native calls cannot cancel current trial as user intent");Eq(f.Deletes,0,"status reentry preserves owned display");Eq(f.Cancelled,0,"status reentry is not active user cancellation");Eq(f.NativeAccepted,0,"rejected status probes do not accept native input");
    }
    static void MountIntentLookupReentry()
    {
        using var f=new F();bool nestedResult=true;int lookups=0;var order=new List<string>();var observations=new List<(uint Id,bool Active)>();
        f.LookupBefore=()=>{lookups++;nestedResult=f.Use(ActionType.Mount,56);};
        f.NativeBefore=()=>{observations.Add((f.LastId,f.Runtime.HasActivePreview));order.Add("native"+f.LastId);};
        f.Runtime.PreviewCancelled+=_=>order.Add("cancel");
        Eq(f.Use(ActionType.Mount,55),true,"outer input consumed");Eq(nestedResult,false,"lookup-nested rejection preserved");Eq(lookups,1,"lookup reentry callback consumed once");
        Eq(f.OriginalCalls,1,"only nested original dispatched");Eq(observations.Count,1,"only nested dispatch state recorded");
        Eq(observations[0],(56u,true),"lookup-nested native dispatch must see the current trials still active");
        Eq(string.Join(",",order),"native56,cancel","outer input cancels without native replay");
        Eq(f.Runtime.HasActivePreview,false,"outer mount intent ends all trials");Eq(f.Cancelled,1,"one outer intent cancellation");Eq(f.Deletes,1,"one display release");Eq(f.NativeAccepted,0,"neither rejected original reports native acceptance");
    }
    static void MountIntentLookupStale()
    {
        using var f=new F();f.ReenterLookup=true;bool? activeDuringOriginal=null;f.NativeBefore=()=>activeDuringOriginal=f.Runtime.HasActivePreview;
        Eq(f.Use(ActionType.Mount,55),false,"original rejection preserved after lookup invalidation");Eq(f.ReenterLookup,false,"ownership lookup actually reentered");Eq(f.Territory,2u,"lookup changed current territory");
        Eq(f.OriginalCalls,1,"stale outer original forwarded once");Eq(activeDuringOriginal,true,"invalidated ownership cannot clear trials before original dispatch");
        Eq(f.Runtime.ActiveEffects.Count,2,"lookup invalidation leaves unrelated ownership intact");Eq(f.Deletes,0,"invalidated lookup cannot delete display");Eq(f.Cancelled,0,"stale binding cannot report intent cancellation");Eq(f.NativeAccepted,0,"rejected stale original cannot accept input");
    }
    static void SingleMount() { using var f=new F(); f.Effects.Remove(PreviewSlot.Emote); Eq(f.Use(ActionType.GeneralAction,23),true,"local exit"); Eq(f.Runtime.HasActivePreview,false,"all ownership ended"); Eq(f.Deletes,1,"owned display released once"); Eq(f.OriginalCalls,0,"no native input replay"); }
    static void OwnCall() { using var f=new F(); Set(f.Runtime,"ownCall",true); Eq(f.Use(ActionType.GeneralAction,23),false,"original result"); Eq(f.OriginalCalls,1,"original once"); Eq(f.Runtime.ActiveEffects.Count,2,"owned call preserved preview"); }
    static void DisplayMismatch() { using var f=new F(); f.Clone->Mount.MountId=447; Eq(f.Use(ActionType.GeneralAction,23),false,"original result"); Eq(f.OriginalCalls,1,"original once"); Eq(f.MountCalls,0,"changed display not torn down"); }
    static void LookupReentry() { using var f=new F(); f.ReenterLookup=true; Eq(f.Use(ActionType.GeneralAction,23),false,"stale binding forwarded"); Eq(f.OriginalCalls,1,"one original"); Eq(f.MountCalls,0,"obsolete display not torn down"); }
    static void CompanionAccepted() { using var f=new F(); f.PrepareCompanion(true); Eq(f.Companion(41),true,"leaf result"); Eq(f.LeafCalls,1,"one leaf execution"); Eq(f.LastLeafId,41u,"exact companion id"); Eq(f.Runtime.HasActivePreview,false,"all previews ended"); Eq(f.NativeAccepted,1,"one accepted event"); Eq(f.Deletes,1,"display released"); }
    static void CompanionRejected() { using var f=new F(); f.PrepareCompanion(false); Eq(f.Companion(41),false,"native rejection"); Eq(f.LeafCalls,1,"one leaf execution"); Eq(f.Runtime.ActiveEffects.Count,2,"all previews retained"); Eq(f.NativeAccepted,0,"no accepted event"); }
    static void CompanionOwnCall() { using var f=new F(); f.PrepareCompanion(true); Set(f.Runtime,"ownCall",true); Eq(f.Companion(41),true,"original leaf result"); Eq(f.LeafCalls,1,"one original leaf"); Eq(f.Runtime.ActiveEffects.Count,2,"owned call preserves previews"); }
    static void CompanionNested() { using var f=new F(); f.Effects.Remove(PreviewSlot.Mount); f.PrepareCompanion(true); f.NestedLeafId=41; Eq(f.Use(ActionType.Companion,41),false,"outer false preserved"); Eq(f.OriginalCalls,1,"outer invoked once"); Eq(f.LeafCalls,1,"nested leaf invoked once"); Eq(f.Runtime.HasActivePreview,false,"accepted nested leaf ends previews"); Eq(f.NativeAccepted,1,"one commit"); }
    static void CompanionQueued() { using var f=new F(); f.Effects.Remove(PreviewSlot.Mount); f.PrepareCompanion(false); f.NativeResult=true; Eq(f.Use(ActionType.Companion,41),true,"queue result preserved"); Eq(f.OriginalCalls,1,"outer once"); Eq(f.LeafCalls,0,"leaf was not executed"); Eq(f.Runtime.ActiveEffects.Count,1,"queue acceptance does not end preview"); }
    static void CompanionTrueWithoutExecution() { using var f=new F(); f.PrepareCompanion(true,false); Eq(f.Companion(41),true,"special branch true preserved"); Eq(f.LeafCalls,1,"one original"); Eq(f.Runtime.ActiveEffects.Count,2,"no normal execution evidence retains preview"); Eq(f.NativeAccepted,0,"no false commit"); }
    static void CompanionRejectedCooldown() { using var f=new F(); f.PrepareCompanion(false,true); Eq(f.Companion(71),false,"special branch rejection preserved"); Eq(f.LeafCalls,1,"one original"); Eq(f.ActionMgr->CompanionActionCooldown>0,true,"rejection branch really changed cooldown"); Eq(f.Runtime.ActiveEffects.Count,2,"cooldown alone cannot commit"); Eq(f.NativeAccepted,0,"no false commit"); }
    static void NativeSetupDismount()
    {
        using var f=new F(42,42); f.Clone->Mount.MountObject=f.Clone;
        f.Setup(0);
        Eq(f.SetupCalls,1,"real native original once"); Eq(f.Source->Mount.MountId,(ushort)0,"real native dismount retained");
        Eq(f.Clone->Mount.MountId,(ushort)0,"clone mount removed rather than old baseline 42 restored"); Eq(f.Clone->Mount.MountObject==null,true,"clone mount child torn down");
        Eq(f.Clone->Mode,CharacterModes.Normal,"mounted pose released"); Eq(f.Clone->EmoteController.EmoteId,(ushort)71,"unrelated emote retained");
        Eq(f.Runtime.ActiveEffects.Count,1,"only mount ownership ended"); Eq(f.Runtime.ActiveEffects[0].Slot,PreviewSlot.Emote,"emote ownership kept");
        Eq(f.Spawn.ContainsKey(PreviewSlot.Mount),false,"pending mount removed"); Eq(f.MountCalls,1,"one clone teardown"); Eq(f.SourceMountCalls,0,"real original not repeated"); Eq(f.NativeAccepted,0,"container notification does not invent accepted-input event");
    }
    static void NativeSetupLastSlot() { using var f=new F(42,42); f.Effects.Remove(PreviewSlot.Emote); f.Setup(0); Eq(f.Runtime.HasActivePreview,false,"last preview ended"); Eq(f.Deletes,1,"owned clone disposed once"); Eq(f.SetupCalls,1,"one native original"); }
    static void NativeSetupReplacement() { using var f=new F(42,42); f.SetupAfter=()=>{ f.Effects[PreviewSlot.Mount]=new(PreviewSlot.Mount,447); f.Clone->Mount.MountId=447; Set(f.Runtime,"revision",1L); }; f.Setup(0); Eq(f.Clone->Mount.MountId,(ushort)447,"replacement clone preserved"); Eq(f.Runtime.ActiveEffects.Count,2,"replacement ownership preserved"); Eq(f.MountCalls,0,"no stale display teardown"); Eq(f.SetupCalls,1,"native original once"); }
    static void NativeSetupTerritory() { using var f=new F(42,42); f.SetupAfter=()=>f.Territory=2; f.Setup(0); Eq(f.Runtime.ActiveEffects.Count,2,"stale preview not touched"); Eq(f.MountCalls,0,"stale clone not touched"); }
    static void NativeSetupLookup() { using var f=new F(42,42); f.ReenterLookup=true; f.Setup(0); Eq(f.Runtime.ActiveEffects.Count,2,"lookup invalidation keeps new ownership"); Eq(f.MountCalls,0,"obsolete clone not touched"); }
    static void NativeSetupChangedDisplay() { using var f=new F(42,42); f.Clone->Mount.MountId=447; f.Setup(0); Eq(f.Clone->Mount.MountId,(ushort)447,"unowned display mount preserved"); Eq(f.Runtime.ActiveEffects.Count,2,"mismatched ownership preserved"); Eq(f.MountCalls,0,"no unrelated teardown"); }
    static void NativeSetupOwned() { using var f=new F(42,42); Set(f.Runtime,"ownCall",true); f.Setup(0); Eq(f.Runtime.ActiveEffects.Count,2,"own call not cancelled"); Eq(f.Clone->Mount.MountId,(ushort)446,"display unchanged"); Eq(f.SetupCalls,1,"real original once"); }
    static void LocationDismount() { using var f=new F(42,42); f.LocationResult=true; f.LocationSetup=0; Eq(f.Location(ActionType.Mount,0),true,"native true preserved"); Eq(f.LocationCalls,1,"native location once"); Eq(f.SetupCalls,1,"native container original once"); Eq(f.Source->Mount.MountId,(ushort)0,"real native state preserved"); Eq(f.Clone->Mount.MountId,(ushort)0,"old baseline must not return"); Eq(f.Clone->Mode,CharacterModes.Normal,"mounted pose released"); Eq(f.Runtime.ActiveEffects.Count,1,"emote retained"); Eq(f.NativeAccepted,1,"accepted input reported once"); Eq(f.MountCalls,1,"one display teardown"); }
    static void LocationRejected() { using var f=new F(42,42); Eq(f.Location(ActionType.Mount,0),false,"native rejection preserved"); Eq(f.LocationCalls,1,"native once"); Eq(f.Clone->Mount.MountId,(ushort)446,"rejected input retains display"); Eq(f.Runtime.ActiveEffects.Count,2,"all ownership retained"); Eq(f.NativeAccepted,0,"no accepted event"); }
    static void LocationMountAccepted() { using var f=new F(42,42); f.LocationResult=true; f.LocationSetup=55; Eq(f.Location(ActionType.Mount,55),true,"native acceptance preserved"); Eq(f.Source->Mount.MountId,(ushort)55,"native winner retained"); Eq(f.Runtime.HasActivePreview,false,"all trial ownership ended"); Eq(f.Deletes,1,"display released once"); Eq(f.NativeAccepted,1,"accepted event once"); Eq(f.LocationCalls,1,"native once"); }
    static void DisplayTeardownReplacement() { using var f=new F(42,42); f.DisplayAfter=()=>{f.Clone->Mount.MountId=447;f.Clone->Mode=CharacterModes.Mounted;}; f.Setup(0); Eq(f.Clone->Mount.MountId,(ushort)447,"reentrant display winner preserved"); Eq(f.Clone->Mode,CharacterModes.Mounted,"reentrant display posture preserved"); Eq(f.Runtime.ActiveEffects.Count,2,"stale teardown cannot erase ownership"); }
    static void DisplayTeardownNativeWinner() { using var f=new F(42,42); f.DisplayAfter=()=>{f.Source->Mount.MountId=55;f.Source->Mode=CharacterModes.Mounted;}; f.Setup(0); Eq(f.Source->Mount.MountId,(ushort)55,"new real native winner preserved"); Eq(f.Runtime.ActiveEffects.Count,2,"dismount cleanup no longer has current evidence"); }
    static void DisplayTeardownEmoteMode() { using var f=new F(42,42); f.Clone->Mode=CharacterModes.EmoteLoop;f.Clone->ModeParam=3; f.Setup(0); Eq(f.Clone->Mode,CharacterModes.EmoteLoop,"emote posture survives container teardown"); Eq(f.Clone->ModeParam,(byte)3,"emote mode argument preserved"); Eq(f.Runtime.ActiveEffects.Count,1,"emote preview preserved"); }
    static void DisplayTeardownCurrentMode() { using var f=new F(42,42); f.SetupAfter=()=>{f.Source->Mode=CharacterModes.Crafting;f.Source->ModeParam=5;}; f.Setup(0); Eq(f.Clone->Mode,CharacterModes.Crafting,"current native mode wins over historical mounted mode"); Eq(f.Clone->ModeParam,(byte)5,"current mode argument copied"); Eq(f.Source->Mode,CharacterModes.Crafting,"real mode untouched"); }
    static void DismountThenTrial() { using var f=new F(42,42); f.Setup(0); f.ApplyDisplayMount(447); f.Runtime.Clear(PreviewSlot.Mount); Eq(f.Clone->Mount.MountId,(ushort)0,"later clear must not resurrect the historical real mount"); Eq(f.Clone->Mode,CharacterModes.Normal,"later clear uses unmounted posture"); Eq(f.Source->Mount.MountId,(ushort)0,"real dismount preserved throughout"); Eq(f.Runtime.ActiveEffects.Count,1,"emote still retained"); }
    static void DismountThenEmoteClear() { using var f=new F(42,42,555); f.SetupAfter=()=>f.Source->Timeline.TimelineSequencer.TimelineIds[0]=13; f.Setup(0); f.Clone->EmoteController.EmoteId=0; f.Runtime.Clear(PreviewSlot.Emote); Eq(f.LastTimeline,(ushort)13,"later clear restores current unmounted timeline, never historical mounted timeline 555"); Eq(f.Clone->Mode,CharacterModes.Normal,"later emote clear never restores mounted posture"); Eq(f.Source->Mode,CharacterModes.Normal,"real mode remains native"); }
    static void DismountTransitionalMode() { using var f=new F(42,42,555); f.SetupAfter=()=>{f.Source->Mode=CharacterModes.Mounted;f.Source->ModeParam=7;}; f.Setup(0); byte[] real=new ReadOnlySpan<byte>(f.Source,sizeof(Character)).ToArray(); f.Clone->EmoteController.EmoteId=0; f.Runtime.Clear(PreviewSlot.Emote); Eq(f.Clone->Mode,CharacterModes.Normal,"snapshot of mount0 must not retain transitional mounted posture"); Eq(f.Clone->ModeParam,(byte)0,"snapshot mode parameter normalized"); Eq(f.LastTimeline,(ushort)0,"transitional mounted timeline is not replayed"); Eq(new ReadOnlySpan<byte>(f.Source,sizeof(Character)).SequenceEqual(real),true,"all real source bytes preserved"); }
    static void AssertVisibilityCall(F f,DrawDataContainer* container,uint index,int count)
    {
        Eq(f.VisibilityCalls,count,"native visibility original called exactly once for each query");
        Eq(f.LastVisibilityContainer,(nint)container,"visibility original received exact container");
        Eq(f.LastVisibilityIndex,index,"visibility original received exact unsigned slot/index");
    }
    static void WeaponGateOwned()
    {
        using var f=new F();f.PrepareVisibility();f.PrepareVisibilityHook(false);
        byte[] source=new ReadOnlySpan<byte>(f.Source,sizeof(Character)).ToArray();
        for(uint slot=0;slot<3;slot++)
        {
            Eq(f.Visibility(false,&f.Source->DrawData,slot),false,"native true is suppressed for current hidden source lease");
            AssertVisibilityCall(f,&f.Source->DrawData,slot,(int)slot+1);
        }
        Eq(new ReadOnlySpan<byte>(f.Source,sizeof(Character)).SequenceEqual(source),true,"visibility detour wrote real Character bytes");
        Eq(f.Runtime.ActiveEffects.Count,2,"visibility query cannot cancel preview effects");Eq(f.Deletes,0,"visibility query cannot tear down display actor");
    }
    static void ExtraWeaponGateOwned()
    {
        using var f=new F();f.PrepareVisibility();f.PrepareVisibilityHook(true);
        Eq(f.Visibility(true,&f.Source->DrawData,0),false,"extra index zero suppresses leased fourth source weapon");
        AssertVisibilityCall(f,&f.Source->DrawData,0,1);
        Eq(f.Runtime.ActiveEffects.Count,2,"extra visibility query retains preview effects");
    }
    static void WeaponGateNativeFalse()
    {
        foreach(bool extra in new[]{false,true})
        {
            using var f=new F();f.PrepareVisibility();f.PrepareVisibilityHook(extra);f.VisibilityResult=false;
            Eq(f.Visibility(extra,&f.Source->DrawData,0),false,"native rejection must remain false");AssertVisibilityCall(f,&f.Source->DrawData,0,1);
            Eq(f.Runtime.ActiveEffects.Count,2,"native visibility rejection cannot cancel previews");
        }
    }
    static void WeaponGateForeign()
    {
        foreach(bool extra in new[]{false,true})
        {
            using var f=new F();f.PrepareVisibility();f.PrepareVisibilityHook(extra);var foreign=f.VisibilityForeignCharacter();
            Eq(f.Visibility(extra,&f.Clone->DrawData,0),true,"display clone must retain its native weapon visibility");AssertVisibilityCall(f,&f.Clone->DrawData,0,1);
            Eq(f.Visibility(extra,&foreign->DrawData,0),true,"foreign character with aliased render pointers must retain native visibility");AssertVisibilityCall(f,&foreign->DrawData,0,2);
            uint invalid=extra?1u:3u;
            Eq(f.Visibility(extra,&f.Source->DrawData,invalid),true,"out-of-range gate index must pass through");AssertVisibilityCall(f,&f.Source->DrawData,invalid,3);
            Eq(f.Visibility(extra,&f.Source->DrawData,uint.MaxValue),true,"unsigned extreme index must pass through without wraparound");AssertVisibilityCall(f,&f.Source->DrawData,uint.MaxValue,4);
        }
    }
    static void WeaponGateNoActor()
    {
        foreach(bool extra in new[]{false,true})
        {
            using var f=new F();f.PrepareVisibility();f.PrepareVisibilityHook(extra);Set(f.Runtime,"visualActor",null);
            Eq(f.Visibility(extra,&f.Source->DrawData,0),true,"no display actor must preserve native true");AssertVisibilityCall(f,&f.Source->DrawData,0,1);
        }
    }
    static void WeaponGateDisposedRuntime()
    {
        foreach(bool extra in new[]{false,true})
        {
            using var f=new F();f.PrepareVisibility();f.PrepareVisibilityHook(extra);Set(f.Runtime,"disposed",true);
            Eq(f.Visibility(extra,&f.Source->DrawData,0),true,"disposed runtime cannot suppress native visibility");AssertVisibilityCall(f,&f.Source->DrawData,0,1);
        }
    }
    static void WeaponGateDisposedActor()
    {
        foreach(bool extra in new[]{false,true})
        {
            using var f=new F();f.PrepareVisibility();f.PrepareVisibilityHook(extra);f.Display.Dispose();
            Eq(f.Visibility(extra,&f.Source->DrawData,0),true,"disposed display actor cannot suppress native visibility");AssertVisibilityCall(f,&f.Source->DrawData,0,1);
        }
    }
    static void WeaponGateUnready()
    {
        foreach(bool extra in new[]{false,true})
        {
            using var f=new F();f.PrepareVisibilityHook(extra);
            Eq(f.Visibility(extra,&f.Source->DrawData,0),true,"unready display actor cannot suppress native visibility");AssertVisibilityCall(f,&f.Source->DrawData,0,1);
        }
    }
    static void WeaponGateOriginalInvalidation()
    {
        foreach(bool extra in new[]{false,true})
        {
            using var f=new F();f.PrepareVisibility();f.PrepareVisibilityHook(extra);
            f.VisibilityAfter=()=>*(uint*)((byte*)f.Source+0x74)=9876;
            Eq(f.Visibility(extra,&f.Source->DrawData,0),true,"source generation changed inside original revokes old suppression lease");AssertVisibilityCall(f,&f.Source->DrawData,0,1);
            Eq(*(uint*)((byte*)f.Source+0x74),9876u,"native original side effect preserved");
        }
    }
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
        public bool FrameworkThread=true;
        public readonly Character* Source=Alloc<Character>(), Clone=Alloc<Character>(); public readonly ClientObjectManager* Manager=Alloc<ClientObjectManager>();
        public readonly ActionManager* ActionMgr=Alloc<ActionManager>();
        readonly nint* table=(nint*)NativeMemory.AllocZeroed(0x300); readonly Character** local=(Character**)NativeMemory.AllocZeroed((nuint)sizeof(nint)); readonly ActionManager** localAction=(ActionManager**)NativeMemory.AllocZeroed((nuint)sizeof(nint)); public readonly ActorPreviewRuntime Runtime;
        public Dictionary<PreviewSlot,PreviewEffect> Effects=new(); public uint Territory=1; public int OriginalCalls,StatusCalls,MountCalls,SourceMountCalls,NativeAccepted,Cancelled,Deletes,LeafCalls; public uint LastId,LastLeafId,NestedLeafId,NestedLocationId,RandomNestedMountId,StatusNestedId,LastExtra,LastCombo; public ulong LastTarget; public ActionType LastType; public ActionManager.UseActionMode LastMode; public bool NativeResult,NativeClearsMount,LeafResult,LeafChangesCooldown,ReenterLookup;
        public readonly Dictionary<PreviewSlot,int> Spawn=new() { [PreviewSlot.Mount]=60 }; public int SetupCalls,LocationCalls,CompanionClearCalls; public ushort LastTimeline; public bool LocationResult; public short? LocationSetup; public Action? SetupAfter,DisplayAfter,NativeBefore,LocationBefore,LookupBefore;
        readonly List<nint> visibilityAllocations=[];
        public int VisibilityCalls;public nint LastVisibilityContainer;public uint LastVisibilityIndex;public bool VisibilityResult=true;public Action? VisibilityAfter;
        public NativePreviewActor Display=>(NativePreviewActor)RuntimeType.GetField("visualActor",All)!.GetValue(Runtime)!;
        public F(ushort baselineMount=0,ushort realMount=0,ushort baselineTimeline=0)
        {
            current=this; *(nint**)Source=table; *(nint**)Clone=table; table[1]=(nint)(delegate* unmanaged<GameObject*,ulong>)&Identity;
            Source->EntityId=500; Source->ObjectIndex=0; Clone->EntityId=700; Clone->ObjectIndex=207; Source->Mount.OwnerObject=Source; Clone->Mount.OwnerObject=Clone;
            Source->Vfx.OwnerObject=Source; Clone->Vfx.OwnerObject=Clone;
            Source->Mount.MountId=baselineMount; Source->Mode=baselineMount==0?CharacterModes.Normal:CharacterModes.Mounted; Clone->Mount.MountId=446; Clone->EmoteController.EmoteId=71; Clone->Mode=CharacterModes.Mounted;
            Source->Timeline.TimelineSequencer.TimelineIds[0]=baselineTimeline;
            Manager->BattleCharas[207].BattleChara=(BattleChara*)Clone;
            ClientObjectManager.Addresses.Instance.Value=(nint)Manager;
            ClientObjectManager.Addresses.GetObjectByIndex.Value=(nint)(delegate* unmanaged<ClientObjectManager*,ushort,Character*>)&Lookup;
            ClientObjectManager.Addresses.DeleteObjectByIndex.Value=(nint)(delegate* unmanaged<ClientObjectManager*,ushort,byte,void>)&Delete;
            *local=Source; Control.Addresses.GetLocalPlayer.Value=(nint)local;
            *localAction=ActionMgr; ActionManager.Addresses.Instance.Value=(nint)localAction;
            MountContainer.Addresses.CreateAndSetupMount.Value=(nint)(delegate* unmanaged<MountContainer*,short,uint,uint,uint,byte,byte,byte,void>)&Mount;
            Character.Addresses.SetMode.Value=(nint)(delegate* unmanaged<Character*,CharacterModes,byte,void>)&SetMode;
            ActionTimelineSequencer.Addresses.PlayTimeline.Value=(nint)(delegate* unmanaged<ActionTimelineSequencer*,ushort,void*,void>)&Play;
            Runtime=(ActorPreviewRuntime)RuntimeHelpers.GetUninitializedObject(RuntimeType);
            Set(Runtime,"framework",Proxy<IFramework>((m,a)=>m.Name=="get_IsInFrameworkUpdateThread"?FrameworkThread:null));
            var player=Proxy<IPlayerCharacter>((m,a)=>m.Name switch {"get_Address"=>(nint)Source,"get_GameObjectId"=>500UL,_=>m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null});
            Set(Runtime,"objects",Proxy<IObjectTable>((m,a)=>m.Name=="get_LocalPlayer"?player:null));
            Set(Runtime,"clientState",Proxy<IClientState>((m,a)=>m.Name switch {"get_IsLoggedIn"=>true,"get_TerritoryType"=>Territory,_=>null}));
            Set(Runtime,"conditions",Proxy<ICondition>((m,a)=>m.ReturnType==typeof(bool)?false:null));
            Set(Runtime,"actorAddress",(nint)Source); Set(Runtime,"actorId",500UL); Set(Runtime,"territory",1u);
            Set(Runtime,"effects",Effects); Effects[PreviewSlot.Mount]=new(PreviewSlot.Mount,446); Effects[PreviewSlot.Emote]=new(PreviewSlot.Emote,71);
            Set(Runtime,"spawnWait",Spawn); Set(Runtime,"implicitMinion",new ImplicitMinionOwnership());
            Set(Runtime,"hmsEvidence",Activator.CreateInstance(RuntimeType.GetField("hmsEvidence",All)!.FieldType));
            var intent=RuntimeType.GetNestedType("NativeIntent",All)!; Set(Runtime,"handoff",Activator.CreateInstance(typeof(NativeInputHandoff<>).MakeGenericType(intent)));
            var snapshot=RuntimeType.GetNestedType("ActorSnapshot",All)!; Set(Runtime,"baseline",snapshot.GetMethod("Read",All)!.Invoke(null,[Ptr(Source,typeof(Character)),false]));
            Source->Mount.MountId=realMount;
            // Constructor only records pointers and ids; no native actor is created.
            Set(Runtime,"visualActor",typeof(NativePreviewActor).GetConstructors(All).Single().Invoke([Ptr(Manager,typeof(ClientObjectManager)),(ushort)207,Ptr(Clone,typeof(Character)),Ptr(Source,typeof(Character)),null]));
            ActionManager.Delegates.UseAction original=(mgr,type,id,target,extra,mode,combo,area)=> {OriginalCalls++;LastType=type;LastId=id;LastTarget=target;LastExtra=extra;LastMode=mode;LastCombo=combo;NativeBefore?.Invoke();if(NativeClearsMount)Source->Mount.MountId=0;if(NestedLeafId!=0)Companion(NestedLeafId);if(type==ActionType.GeneralAction&&RandomNestedMountId!=0)Use(ActionType.Mount,RandomNestedMountId);if(NestedLocationId!=0&&(RandomNestedMountId==0||type==ActionType.Mount))Location(ActionType.Mount,NestedLocationId);return NativeResult;};
            ActionManager.Delegates.GetActionStatus status=(mgr,type,id,target,recast,casting,info)=>{StatusCalls++;if(StatusNestedId!=0){Use(ActionType.Mount,StatusNestedId);Location(ActionType.Mount,StatusNestedId);}return 0x243;};
            MountContainer.Delegates.CreateAndSetupMount setup=(container,id,top,body,legs,stain,unk6,unk7)=>{ SetupCalls++; MountEffect(container,id); SetupAfter?.Invoke(); };
            ActionManager.Delegates.UseActionLocation location=(mgr,type,id,target,pos,extra,a7)=>{ LocationCalls++; LocationBefore?.Invoke();if(LocationSetup is {} mount)Setup(mount); return LocationResult; };
            Set(Runtime,"useAction",Hook(original)); Set(Runtime,"actionStatus",Hook(status));
            Set(Runtime,"mountSetup",Hook(setup)); Set(Runtime,"useLocation",Hook(location));
            Runtime.NativeInputAccepted+=()=>NativeAccepted++; Runtime.PreviewCancelled+=_=>Cancelled++;
        }
        public bool Use(ActionType type,uint id,ActionManager.UseActionMode mode=ActionManager.UseActionMode.None,ulong target=0,uint extra=0,uint combo=0)=>(bool)RuntimeType.GetMethod("UseActionDetour",All)!.Invoke(Runtime,[Ptr(ActionMgr,typeof(ActionManager)),type,id,target,extra,mode,combo,Ptr(null,typeof(bool))])!;
        public uint Status(ActionType type,uint id)=>(uint)RuntimeType.GetMethod("ActionStatusDetour",All)!.Invoke(Runtime,[Ptr(null,typeof(ActionManager)),type,id,0UL,false,false,Ptr(null,typeof(uint))])!;
        public void Setup(short id)=>RuntimeType.GetMethod("MountSetupDetour",All)!.Invoke(Runtime,[Ptr(&Source->Mount,typeof(MountContainer)),id,0u,0u,0u,(byte)0,(byte)0,(byte)0]);
        public bool Location(ActionType type,uint id)=>(bool)RuntimeType.GetMethod("UseLocationDetour",All)!.Invoke(Runtime,[Ptr(ActionMgr,typeof(ActionManager)),type,id,0UL,Ptr(null,typeof(System.Numerics.Vector3)),0u,(byte)0])!;
        public void ApplyDisplayMount(ushort id) { RuntimeType.GetMethod("Apply",All)!.Invoke(Runtime,[Ptr(Clone,typeof(Character)),new PreviewEffect(PreviewSlot.Mount,id)]); Effects[PreviewSlot.Mount]=new(PreviewSlot.Mount,id); Spawn[PreviewSlot.Mount]=60; }
        public void PrepareCompanion(bool result,bool? changesCooldown=null)
        {
            LeafResult=result; LeafChangesCooldown=changesCooldown??result;
            var delegateType=RuntimeType.GetNestedType("UseCompanionNative",All) ?? throw new Exception("production companion leaf boundary is missing");
            var original=Delegate.CreateDelegate(delegateType,this,GetType().GetMethod(nameof(CompanionOriginal),All)!);
            var hook=typeof(Program).GetMethod(nameof(Hook),All)!.MakeGenericMethod(delegateType).Invoke(null,[original]);
            Set(Runtime,"useCompanion",hook);
        }
        DrawObject* VisibilityDraw()
        {
            var draw=Alloc<Weapon>();draw->Flags=0x29;visibilityAllocations.Add((nint)draw);return (DrawObject*)draw;
        }
        public void PrepareVisibility()
        {
            Source->DrawData.OwnerObject=Source;Clone->DrawData.OwnerObject=Clone;
            Source->DrawObject=VisibilityDraw();Clone->DrawObject=VisibilityDraw();
            for(int i=0;i<4;i++)
            {
                var sourceDraw=VisibilityDraw();var cloneDraw=VisibilityDraw();
                ref var source=ref (i<3?ref Source->DrawData.WeaponData[i]:ref Source->DrawData.UnkWeaponData[0]);
                ref var clone=ref (i<3?ref Clone->DrawData.WeaponData[i]:ref Clone->DrawData.UnkWeaponData[0]);
                source.Weapon=(Weapon*)sourceDraw;source.DrawData.DrawObject=sourceDraw;
                clone.Weapon=(Weapon*)cloneDraw;clone.DrawData.DrawObject=cloneDraw;
            }
            Eq((bool)typeof(NativePreviewActor).GetMethod("SetSourceVisibility",All)!.Invoke(Display,[true])!,true,"fixture acquired real source visibility lease");
            typeof(NativePreviewActor).GetProperty("IsReady",All)!.SetValue(Display,true);
            Set(Display,"drawRequested",true);Set(Display,"bodyWasVisible",true);
            Eq(Source->DrawObject->IsVisible,false,"fixture source is hidden by production lease");Eq(Clone->DrawObject->IsVisible,true,"fixture clone has a visible draw");
        }
        public Character* VisibilityForeignCharacter()
        {
            var actor=Alloc<Character>();visibilityAllocations.Add((nint)actor);
            *(nint**)actor=table;actor->EntityId=900;actor->ObjectIndex=33;actor->DrawData=Source->DrawData;actor->DrawData.OwnerObject=actor;return actor;
        }
        public void PrepareVisibilityHook(bool extra)
        {
            string name=extra?"extraWeaponVisibility":"weaponVisibility";
            var field=RuntimeType.GetField(name,All)??throw new Exception("production "+name+" hook boundary is missing");
            var delegateType=field.FieldType.GetGenericArguments().Single();
            var original=Delegate.CreateDelegate(delegateType,this,GetType().GetMethod(nameof(VisibilityOriginal),All)!);
            var hook=typeof(Program).GetMethod(nameof(Hook),All)!.MakeGenericMethod(delegateType).Invoke(null,[original]);field.SetValue(Runtime,hook);
        }
        bool VisibilityOriginal(DrawDataContainer* container,uint index)
        {
            VisibilityCalls++;LastVisibilityContainer=(nint)container;LastVisibilityIndex=index;VisibilityAfter?.Invoke();return VisibilityResult;
        }
        public bool Visibility(bool extra,DrawDataContainer* container,uint index)
        {
            string method=extra?"ExtraWeaponVisibilityDetour":"WeaponVisibilityDetour";
            return (bool)(RuntimeType.GetMethod(method,All)??throw new Exception("production "+method+" is missing")).Invoke(Runtime,[Ptr(container,typeof(DrawDataContainer)),index])!;
        }
        bool CompanionOriginal(ActionManager* mgr,uint id) { LeafCalls++;LastLeafId=id;if(LeafChangesCooldown)mgr->CompanionActionCooldown=1;return LeafResult; }
        public bool Companion(uint id)=>(bool)(RuntimeType.GetMethod("UseCompanionDetour",All) ?? throw new Exception("production companion leaf detour is missing")).Invoke(Runtime,[Ptr(ActionMgr,typeof(ActionManager)),id])!;
        public void Dispose() { foreach(nint allocation in visibilityAllocations)NativeMemory.Free((void*)allocation);NativeMemory.Free(Source);NativeMemory.Free(Clone);NativeMemory.Free(Manager);NativeMemory.Free(table);NativeMemory.Free(local);NativeMemory.Free(localAction);NativeMemory.Free(ActionMgr); }
        static T* Alloc<T>() where T:unmanaged=>(T*)NativeMemory.AllocZeroed((nuint)sizeof(T));
    }
    [UnmanagedCallersOnly] static ulong Identity(GameObject* obj)=>obj->EntityId;
    [UnmanagedCallersOnly] static Character* Lookup(ClientObjectManager* mgr,ushort slot) { var before=current.LookupBefore;current.LookupBefore=null;before?.Invoke();if(current.ReenterLookup) {current.ReenterLookup=false;current.Territory=2;} return (Character*)mgr->BattleCharas[slot].BattleChara; }
    [UnmanagedCallersOnly] static void Delete(ClientObjectManager* mgr,ushort slot,byte param) { current.Deletes++;mgr->BattleCharas[slot].BattleChara=null; }
    static void MountEffect(MountContainer* container,short id) { container->MountId=(ushort)id; if(id==0){container->MountObject=null;container->OwnerObject->Mode=CharacterModes.Normal;container->OwnerObject->ModeParam=0;} }
    [UnmanagedCallersOnly] static void Mount(MountContainer* container,short id,uint top,uint body,uint legs,byte stain,byte unk6,byte unk7) {current.MountCalls++;if(container->OwnerObject==current.Source)current.SourceMountCalls++;MountEffect(container,id);current.DisplayAfter?.Invoke();}
    [UnmanagedCallersOnly] static void SetMode(Character* actor,CharacterModes mode,byte param) { actor->Mode=mode;actor->ModeParam=param; }
    [UnmanagedCallersOnly] static void Play(ActionTimelineSequencer* sequencer,ushort id,void* unk) { current.LastTimeline=id;sequencer->TimelineIds[0]=id; }
}
public class ServiceProxy : DispatchProxy
{
    public Func<MethodInfo,object?[]?,object?> Call=null!;
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>Call(method!,args);
}
