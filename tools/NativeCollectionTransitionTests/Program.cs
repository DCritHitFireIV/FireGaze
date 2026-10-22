using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

// Real production TryApply/Clear and deferred-dispatch methods on private native
// allocations. The simulated native mount teardown deliberately matches the live
// failing state: id zero before the old child and DismountTimer are released.
unsafe static class Program
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static GameDataFixture data = null!;
    static int setups, mountClears;
    static Action? duringSetup;
    static void Eq<T>(T got, T expected, string why) { if (!EqualityComparer<T>.Default.Equals(got, expected)) throw new Exception($"{why}: expected {expected}; got {got}"); }
    static int Main(string[] args)
    {
        if (args.Length != 1 || !Directory.Exists(args[0]))
        { Console.Error.WriteLine("Usage: NativeCollectionTransitionTests <SqpackPath>"); return 2; }
        var actionManager = (ActionManager*)NativeMemory.AllocZeroed((nuint)sizeof(ActionManager));
        var actionPointer = (ActionManager**)NativeMemory.AllocZeroed((nuint)sizeof(nint));
        *actionPointer = actionManager;
        ActionManager.Addresses.Instance.Value = (nint)actionPointer;
        using var game = new Lumina.GameData(args[0], new Lumina.LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified });
        data = new(game);
        (string, Action)[] cases = [
            ("mount73 to accessory56 waits for actual native teardown", () => Switch(PreviewSlot.Accessory, 56)),
            ("mount73 to minion1 waits for actual native teardown", () => Switch(PreviewSlot.Minion, 1)),
            ("Framework Update dispatches after teardown even while drawing is unready", FullUpdate),
            ("clearing queued accessory cannot resurrect it", () => Cancel(false)),
            ("clearing every trial cannot resurrect queued accessory", () => Cancel(true)),
            ("latest accessory replaces queued accessory", Replacement),
            ("compatible emote clear preserves queued collection", CompatibleClear),
            ("territory change prevents queued dispatch", () => Invalid("territory")),
            ("source generation reuse prevents queued dispatch", () => Invalid("source")),
            ("clone generation reuse prevents queued dispatch", () => Invalid("clone")),
            ("native accepted input cancels queued dispatch", NativeCancel),
            ("teardown lookup cancellation prevents stale dispatch", LookupCancel),
            ("companion teardown cancellation prevents stale accessory setup", SetupCancel),
            ("no previous mount applies collection immediately", Immediate),
            ("stuck native teardown expires only queued collection", Timeout),
        ];
        var failed = 0;
        foreach (var (name, run) in cases) try { run(); Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; while (e is TargetInvocationException { InnerException: { } inner }) e = inner; Console.WriteLine("FAIL " + name + ": " + e.Message); }
        Console.WriteLine($"{cases.Length - failed}/{cases.Length} native collection transition cases passed");
        NativeMemory.Free(actionPointer); NativeMemory.Free(actionManager);
        return failed == 0 ? 0 : 1;
    }
    static NativeFixture Fixture()
    {
        var f = new NativeFixture(data); setups = mountClears = 0; duringSetup = null;
        f.DuringOriginal = null; f.SetupCompanionObject = null; f.SetupWritesCompanionObject = false;
        NativeFixture.Set(f.Runtime, "log", Proxy<IPluginLog>());
        typeof(ActorPreviewRuntime).GetProperty("Capabilities")!.SetValue(f.Runtime, new RuntimeCapabilities(true, true, true, true, true, true, ""));
        f.Source->CompanionData.CompanionId = 95; f.Source->CompanionData.CompanionObject = f.NewCompanion(95);
        var snapshot = typeof(ActorPreviewRuntime).GetNestedType("ActorSnapshot", All)!;
        NativeFixture.Set(f.Runtime, "baseline", snapshot.GetMethod("Read", All)!.Invoke(null, [Pointer.Box(f.Source, typeof(Character*)), false]));
        f.Clone->Mount.MountId = 73; f.Clone->Mount.MountObject = f.Foreign; f.Clone->Mount.DismountTimer = 0;
        f.Effects[PreviewSlot.Mount] = new(PreviewSlot.Mount, 73);
        MountContainer.Addresses.CreateAndSetupMount.Value = (nint)(delegate* unmanaged<MountContainer*, short, uint, uint, uint, byte, byte, byte, void>)&Mount;
        CompanionContainer.Addresses.SetupCompanion.Value = (nint)(delegate* unmanaged<CompanionContainer*, short, uint, void>)&Companion;
        OrnamentContainer.Addresses.SetupOrnament.Value = (nint)(delegate* unmanaged<OrnamentContainer*, short, uint, void>)&Ornament;
        Character.Addresses.SetMode.Value = (nint)(delegate* unmanaged<Character*, CharacterModes, byte, void>)&Mode;
        GameObject.Addresses.IsReadyToDraw.Value = (nint)(delegate* unmanaged<GameObject*, byte>)&NotReady;
        GameObject.Addresses.SetPosition.Value = (nint)(delegate* unmanaged<GameObject*, float, float, float, void>)&Position;
        GameObject.Addresses.SetRotation.Value = (nint)(delegate* unmanaged<GameObject*, float, void>)&Rotation;
        Character.Addresses.GetTargetId.Value = (nint)(delegate* unmanaged<Character*, ulong>)&Target;
        Character.Addresses.GetSoftTargetId.Value = (nint)(delegate* unmanaged<Character*, ulong>)&SoftTarget;
        return f;
    }
    static T Proxy<T>() where T : class { var p = DispatchProxy.Create<T, ServiceProxy>(); ((ServiceProxy)(object)p).Call = (m, _) => m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null; return p; }
    static void Apply(NativeFixture f, PreviewSlot slot, ushort id) => Eq(f.Runtime.TryApply(new(slot, id), out var reason), true, "request accepted: " + reason);
    static void Poll(NativeFixture f) => typeof(ActorPreviewRuntime).GetMethod("ApplyPendingCollection", All)!.Invoke(f.Runtime, null);
    static void Ready(NativeFixture f) { f.Clone->Mount.MountObject = null; f.Clone->Mount.DismountTimer = 0; }
    static void SourceUnchanged(NativeFixture f, byte[] bytes) => Eq(new ReadOnlySpan<byte>(f.Source, sizeof(Character)).SequenceEqual(bytes), true, "all real player bytes including genuine minion95 unchanged");
    static void Switch(PreviewSlot slot, ushort id)
    {
        using var f = Fixture(); var bytes = new ReadOnlySpan<byte>(f.Source, sizeof(Character)).ToArray();
        Apply(f, slot, id); Eq(setups, 0, "no setup while native mount child/timer remain"); Eq(mountClears, 1, "one dismount request");
        for (var i = 0; i < 65; i++) Poll(f);
        Eq(setups, 0, "not dispatched during teardown"); Eq(f.Effects.ContainsKey(slot), true, "request survives beyond old 60-frame spawn wait");
        f.Clone->Mount.MountObject = null; Poll(f); Eq(setups, 0, "positive dismount timer alone blocks setup");
        f.Clone->Mount.MountObject = f.Foreign; f.Clone->Mount.DismountTimer = 0; Poll(f); Eq(setups, 0, "old child alone blocks setup");
        Ready(f); Poll(f); Eq(setups, 1, "setup exactly once after native teardown"); Poll(f); Eq(setups, 1, "no duplicate setup");
        Eq(slot == PreviewSlot.Accessory ? f.Clone->OrnamentData.OrnamentId : f.Clone->CompanionData.CompanionId, id, "requested collection applied");
        Eq(f.Effects.ContainsKey(PreviewSlot.Emote), true, "compatible emote retained"); Eq(f.Clone->EmoteController.EmoteId, (ushort)71, "emote identity retained"); SourceUnchanged(f, bytes);
    }
    static void Cancel(bool all)
    {
        using var f = Fixture(); Apply(f, PreviewSlot.Accessory, 56); if (all) f.Runtime.ClearAll(false); else f.Runtime.Clear(PreviewSlot.Accessory);
        Ready(f); var before = setups; Poll(f); Eq(setups, before, "no stale dispatch after cancellation"); Eq(f.Effects.ContainsKey(PreviewSlot.Accessory), false, "cancelled effect absent");
    }
    static void FullUpdate()
    {
        using var f = Fixture(); Apply(f, PreviewSlot.Accessory, 56);
        var update = typeof(ActorPreviewRuntime).GetMethod("Update", All)!;
        for (var i = 0; i < 65; i++) update.Invoke(f.Runtime, new object?[] { null });
        Eq(setups, 0, "unready frame does not prematurely dispatch"); Eq(f.Effects.ContainsKey(PreviewSlot.Accessory), true, "old spawn budget does not drop waiting request");
        Ready(f); update.Invoke(f.Runtime, new object?[] { null });
        Eq(setups, 1, "pending dispatch runs before unready Tick returns"); Eq(f.Clone->OrnamentData.OrnamentId, (ushort)56, "correct effect starts");
        Eq(f.Display.IsReady, false, "test genuinely exercised unready draw path");
        var waits = (Dictionary<PreviewSlot, int>)typeof(ActorPreviewRuntime).GetField("spawnWait", All)!.GetValue(f.Runtime)!;
        Eq(waits[PreviewSlot.Accessory], 60, "fresh spawn budget begins with actual setup");
    }
    static void Replacement()
    {
        using var f = Fixture(); Apply(f, PreviewSlot.Accessory, 56); Apply(f, PreviewSlot.Accessory, 5); Ready(f); var before = setups; Poll(f);
        Eq(setups, before + 1, "only latest request dispatched"); Eq(f.Clone->OrnamentData.OrnamentId, (ushort)5, "latest accessory wins");
    }
    static void CompatibleClear()
    {
        using var f = Fixture(); Apply(f, PreviewSlot.Accessory, 56); f.Runtime.Clear(PreviewSlot.Emote); Ready(f); var before = setups; Poll(f);
        Eq(setups, before + 1, "unrelated emote cancellation does not drop pending collection");
    }
    static void Invalid(string kind)
    {
        using var f = Fixture(); Apply(f, PreviewSlot.Accessory, 56); if (kind == "territory") f.Territory = 2; else NativeFixture.Generation(kind == "source" ? f.Source : f.Clone, 999);
        Ready(f); var before = setups; Poll(f); Eq(setups, before, "invalid binding cannot receive deferred setup");
    }
    static void NativeCancel()
    {
        using var f = Fixture(); Apply(f, PreviewSlot.Accessory, 56);
        f.SetupWritesCompanionObject = true; f.SetupCompanionObject = f.NewCompanion(1);
        f.Companion(&f.Source->CompanionData, 1); Ready(f); var before = setups; Poll(f);
        Eq(setups, before, "accepted native action revokes queued trial"); Eq(f.Runtime.HasActivePreview, false, "all trials ended");
    }
    static void LookupCancel()
    {
        using var f = Fixture(); Apply(f, PreviewSlot.Accessory, 56); Ready(f);
        // The production actor lookup is a native boundary and can re-enter.
        lookupCancel = f.Runtime; var old = FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager.Addresses.GetObjectByIndex.Value;
        FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager.Addresses.GetObjectByIndex.Value = (nint)(delegate* unmanaged<FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager*, ushort, Character*>)&Lookup;
        try { var before = setups; Poll(f); Eq(setups, before, "lookup cancellation prevents stale setup"); }
        finally { lookupCancel = null; FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager.Addresses.GetObjectByIndex.Value = old; }
    }
    static ActorPreviewRuntime? lookupCancel;
    static void SetupCancel()
    {
        using var f = Fixture(); Apply(f, PreviewSlot.Accessory, 56); Ready(f); f.Clone->CompanionData.CompanionId = 95;
        duringSetup = () => f.Runtime.ClearAll(false); var before = setups; Poll(f); Eq(setups, before, "companion teardown cannot continue stale ornament setup"); Eq(f.Runtime.HasActivePreview, false, "cancel preserved");
    }
    static void Immediate()
    {
        using var f = Fixture(); f.Clone->Mount.MountId = 0; f.Clone->Mount.MountObject = null; f.Effects.Remove(PreviewSlot.Mount);
        Apply(f, PreviewSlot.Accessory, 56); Eq(setups, 1, "on-foot collection has no extra delay");
    }
    static void Timeout()
    {
        using var f = Fixture(); Apply(f, PreviewSlot.Accessory, 56); for (var i = 0; i < 181; i++) Poll(f);
        Eq(f.Effects.ContainsKey(PreviewSlot.Accessory), false, "stuck request eventually released"); Eq(f.Effects.ContainsKey(PreviewSlot.Emote), true, "compatible emote survives timeout");
        Ready(f); var before = setups; Poll(f); Eq(setups, before, "timeout never revives");
    }
    [UnmanagedCallersOnly] static Character* Lookup(FFXIVClientStructs.FFXIV.Client.Game.Object.ClientObjectManager* mgr, ushort slot)
    { var cancel = lookupCancel; lookupCancel = null; cancel?.ClearAll(false); return (Character*)mgr->BattleCharas[slot].BattleChara; }
    [UnmanagedCallersOnly] static void Mount(MountContainer* c, short id, uint a, uint b, uint d, byte e, byte f, byte g)
    { if (id == 0) { mountClears++; c->MountId = 0; c->DismountTimer = 0.5194f; } else c->MountId = (ushort)id; }
    [UnmanagedCallersOnly] static void Companion(CompanionContainer* c, short id, uint p)
    { if (id == 0) { c->CompanionId = 0; c->CompanionObject = null; var action = duringSetup; duringSetup = null; action?.Invoke(); } else { setups++; if (Unmounted(c->OwnerObject)) c->CompanionId = (ushort)id; } }
    [UnmanagedCallersOnly] static void Ornament(OrnamentContainer* c, short id, uint p)
    { if (id == 0) c->OrnamentId = 0; else { setups++; if (Unmounted(c->OwnerObject)) c->OrnamentId = (ushort)id; } }
    static bool Unmounted(Character* a) => a->Mount.MountId == 0 && a->Mount.MountObject == null && a->Mount.DismountTimer <= 0;
    [UnmanagedCallersOnly] static void Mode(Character* a, CharacterModes mode, byte p) { a->Mode = mode; a->ModeParam = p; }
    [UnmanagedCallersOnly] static byte NotReady(GameObject* a) => 0;
    [UnmanagedCallersOnly] static void Position(GameObject* a, float x, float y, float z) => a->Position = new(x, y, z);
    [UnmanagedCallersOnly] static void Rotation(GameObject* a, float r) => a->Rotation = r;
    [UnmanagedCallersOnly] static ulong Target(Character* a) => a->TargetId.Id;
    [UnmanagedCallersOnly] static ulong SoftTarget(Character* a) => a->SoftTargetId.Id;
}
