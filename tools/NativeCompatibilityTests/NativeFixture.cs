using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

// Private unmanaged allocations and managed originals only. The runtime under test
// is the compiled production assembly; this fixture neither installs hooks nor
// attaches to a game process. Adapted from the independent NativeDismount fixture.
unsafe sealed class NativeFixture : IDisposable
{
    internal const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static readonly Type RuntimeType = typeof(ActorPreviewRuntime);
    static NativeFixture current = null!;
    readonly nint* table = (nint*)NativeMemory.AllocZeroed(0x300);
    readonly Character** local = (Character**)NativeMemory.AllocZeroed((nuint)sizeof(nint));
    readonly List<nint> allocations = [];
    readonly nint previousManager = ClientObjectManager.Addresses.Instance.Value;
    readonly nint previousLookup = ClientObjectManager.Addresses.GetObjectByIndex.Value;
    readonly nint previousDelete = ClientObjectManager.Addresses.DeleteObjectByIndex.Value;
    readonly nint previousLocal = Control.Addresses.GetLocalPlayer.Value;
    public readonly Character* Source;
    public readonly Character* Clone;
    public readonly Character* Foreign;
    public readonly ClientObjectManager* Manager;
    public readonly ActorPreviewRuntime Runtime;
    public readonly NativePreviewActor Display;
    public readonly Dictionary<PreviewSlot, PreviewEffect> Effects = [];
    public uint Territory = 1;
    public bool FrameworkThread = true;
    public int OriginalCalls, Deletes, Accepted, ExternalEmotes;
    public int ObjectTableReads;
    public nint LastPointer, LastParam;
    public short LastId;
    public ushort LastIntro, LastLoop, ExternalEmote;
    public uint LastExtra;
    public ulong FlagsAtOriginal;
    public Action? DuringOriginal;
    public bool SetupAccepts = true;
    public byte TimelineNativeResult = 1;
    public bool SetupWritesCompanionId = true;
    public bool SetupWritesCompanionObject;
    public Companion* SetupCompanionObject;

    public NativeFixture(IDataManager data)
    {
        current = this;
        Source = Alloc<Character>(); Clone = Alloc<Character>(); Foreign = Alloc<Character>(); Manager = Alloc<ClientObjectManager>();
        Initialize(Source, 500, 0, 101); Initialize(Clone, 700, 207, 202); Initialize(Foreign, 900, 33, 303);
        Source->Mode = CharacterModes.Normal;
        Clone->Mode = CharacterModes.Mounted; Clone->Mount.MountId = 446; Clone->EmoteController.EmoteId = 71;
        Manager->BattleCharas[207].BattleChara = (BattleChara*)Clone;
        ClientObjectManager.Addresses.Instance.Value = (nint)Manager;
        ClientObjectManager.Addresses.GetObjectByIndex.Value = (nint)(delegate* unmanaged<ClientObjectManager*, ushort, Character*>)&Lookup;
        ClientObjectManager.Addresses.DeleteObjectByIndex.Value = (nint)(delegate* unmanaged<ClientObjectManager*, ushort, byte, void>)&Delete;
        *local = Source; Control.Addresses.GetLocalPlayer.Value = (nint)local;

        Runtime = (ActorPreviewRuntime)RuntimeHelpers.GetUninitializedObject(RuntimeType);
        var player = Proxy<IPlayerCharacter>((m, _) => m.Name switch {
            "get_Address" => (nint)Source, "get_GameObjectId" => 500UL,
            _ => m.ReturnType.IsValueType ? Activator.CreateInstance(m.ReturnType) : null });
        Set(Runtime, "objects", Proxy<IObjectTable>((m, _) => { ObjectTableReads++; return m.Name == "get_LocalPlayer" ? player : null; }));
        Set(Runtime, "clientState", Proxy<IClientState>((m, _) => m.Name switch {
            "get_IsLoggedIn" => true, "get_TerritoryType" => Territory, _ => null }));
        Set(Runtime, "conditions", Proxy<ICondition>((m, _) => m.ReturnType == typeof(bool) ? false : null));
        Set(Runtime, "framework", Proxy<IFramework>((m, _) => m.Name == "get_IsInFrameworkUpdateThread" ? FrameworkThread : null));
        Set(Runtime, "data", data); Set(Runtime, "actorAddress", (nint)Source); Set(Runtime, "actorId", 500UL); Set(Runtime, "territory", 1u);
        Effects[PreviewSlot.Mount] = new(PreviewSlot.Mount, 446); Effects[PreviewSlot.Emote] = new(PreviewSlot.Emote, 71);
        Set(Runtime, "effects", Effects); Set(Runtime, "spawnWait", new Dictionary<PreviewSlot, int>());
        Set(Runtime, "implicitMinion", new ImplicitMinionOwnership());
        Set(Runtime, "hmsEvidence", Activator.CreateInstance(RuntimeType.GetField("hmsEvidence", All)!.FieldType));
        var intent = RuntimeType.GetNestedType("NativeIntent", All)!;
        Set(Runtime, "handoff", Activator.CreateInstance(typeof(NativeInputHandoff<>).MakeGenericType(intent)));
        var snapshot = RuntimeType.GetNestedType("ActorSnapshot", All)!;
        Set(Runtime, "baseline", snapshot.GetMethod("Read", All)!.Invoke(null, [Ptr(Source, typeof(Character)), false]));
        Display = (NativePreviewActor)typeof(NativePreviewActor).GetConstructors(All).Single().Invoke(
            [Ptr(Manager, typeof(ClientObjectManager)), (ushort)207, Ptr(Clone, typeof(Character)), Ptr(Source, typeof(Character)), null]);
        Set(Runtime, "visualActor", Display);
        Runtime.NativeInputAccepted += () => Accepted++;
        Runtime.ExternalEmoteAccepted += id => { ExternalEmotes++; ExternalEmote = id; };

        Install("characterUpdate", (GameObject.Delegates.Update)(obj => {
            OriginalCalls++; LastPointer = (nint)obj; FlagsAtOriginal = obj == null ? 0 : (ulong)obj->RenderFlags; DuringOriginal?.Invoke(); }));
        Install("characterDisable", (GameObject.Delegates.DisableDraw)(obj => {
            OriginalCalls++; LastPointer = (nint)obj; DuringOriginal?.Invoke(); }));
        Install("companionSetup", (CompanionContainer.Delegates.SetupCompanion)((container, id, param) => {
            OriginalCalls++; LastPointer = (nint)container; LastId = id; LastExtra = param;
            if (SetupAccepts && container != null)
            {
                if (SetupWritesCompanionId) container->CompanionId = unchecked((ushort)id);
                if (SetupWritesCompanionObject) container->CompanionObject = SetupCompanionObject;
            }
            DuringOriginal?.Invoke(); }));
        Install("ornamentSetup", (OrnamentContainer.Delegates.SetupOrnament)((container, id, param) => {
            OriginalCalls++; LastPointer = (nint)container; LastId = id; LastExtra = param;
            if (SetupAccepts && container != null) container->OrnamentId = unchecked((ushort)id);
            DuringOriginal?.Invoke(); }));
        InstallNativeTimelineOriginal(false);
        InstallNativeTimelineOriginal(true);
    }

    void Initialize(Character* actor, uint id, ushort index, uint generation)
    {
        *(nint**)actor = table; table[1] = (nint)(delegate* unmanaged<GameObject*, ulong>)&Identity;
        actor->EntityId = id; actor->ObjectIndex = index; Generation(actor, generation);
        actor->Mount.OwnerObject = actor; actor->CompanionData.OwnerObject = actor; actor->OrnamentData.OwnerObject = actor;
        actor->DrawData.OwnerObject = actor; actor->Vfx.OwnerObject = actor;
    }

    public void PrepareVisibility()
    {
        var source = Alloc<Weapon>(); var clone = Alloc<Weapon>(); source->Flags = 0x29; clone->Flags = 0x29;
        Source->DrawObject = (DrawObject*)source; Clone->DrawObject = (DrawObject*)clone;
        if (!(bool)typeof(NativePreviewActor).GetMethod("SetSourceVisibility", All)!.Invoke(Display, [true])!)
            throw new Exception("fixture could not acquire source visibility lease");
        typeof(NativePreviewActor).GetProperty("IsReady", All)!.SetValue(Display, true);
        Set(Display, "drawRequested", true); Set(Display, "bodyWasVisible", true);
        if (Source->DrawObject->IsVisible || !Clone->DrawObject->IsVisible) throw new Exception("fixture visibility lease was not applied");
    }

    public void ChangeLocal(Character* actor) => *local = actor;
    public Companion* NewCompanion(uint baseId)
    {
        var companion = Alloc<Companion>();
        companion->BaseId = baseId;
        return companion;
    }
    public static void Generation(Character* actor, uint generation) => *(uint*)((byte*)actor + 0x74) = generation;
    public void Update(Character* actor) => Invoke("CharacterUpdateDetour", Ptr(actor, typeof(GameObject)));
    public void Disable(Character* actor) => Invoke("CharacterDisableDetour", Ptr(actor, typeof(GameObject)));
    public void Companion(CompanionContainer* container, short id, uint param = 0x12345678) => Invoke("CompanionSetupDetour", Ptr(container, typeof(CompanionContainer)), id, param);
    public void Ornament(OrnamentContainer* container, short id, uint param = 0x12345678) => Invoke("OrnamentSetupDetour", Ptr(container, typeof(OrnamentContainer)), id, param);
    public void Timeline(ActionTimelineSequencer* sequence, ushort id, nint param = 0x12345678) => Invoke("TimelinePlayDetour", Ptr(sequence, typeof(ActionTimelineSequencer)), id, Ptr((void*)param, typeof(void)));
    public void ActionTimeline(TimelineContainer* timeline, ushort intro, ushort loop, nint param = 0x12345678) => Invoke("ActionTimelinePlayDetour", Ptr(timeline, typeof(TimelineContainer)), intro, loop, Ptr((void*)param, typeof(void)));
    // Execute the compiled production detour as the native game's BOOL/U1 caller
    // does. The original is an actual unmanaged callback on private allocations;
    // no hook installation, executable patching or game process is involved.
    public byte NativeTimeline(bool actionTimeline, Character* actor, ushort intro, ushort loop = 0)
    {
        var field = actionTimeline ? "actionTimelinePlay" : "timelinePlay";
        var method = actionTimeline ? "ActionTimelinePlayDetour" : "TimelinePlayDetour";
        var delegateType = RuntimeType.GetField(field, All)!.FieldType.GetGenericArguments()[0];
        InstallNativeTimelineOriginal(actionTimeline);
        var detour = RuntimeType.GetMethod(method, All)!.CreateDelegate(delegateType, Runtime);
        var address = Marshal.GetFunctionPointerForDelegate(detour);
        byte result = actionTimeline
            ? ((delegate* unmanaged<TimelineContainer*, ushort, ushort, void*, byte>)address)(actor == null ? null : &actor->Timeline, intro, loop, (void*)0x12345678)
            : ((delegate* unmanaged<ActionTimelineSequencer*, ushort, void*, byte>)address)(actor == null ? null : &actor->Timeline.TimelineSequencer, intro, (void*)0x12345678);
        GC.KeepAlive(detour);
        return result;
    }
    void InstallNativeTimelineOriginal(bool actionTimeline)
    {
        var field = actionTimeline ? "actionTimelinePlay" : "timelinePlay";
        var delegateType = RuntimeType.GetField(field, All)!.FieldType.GetGenericArguments()[0];
        nint original = actionTimeline
            ? (nint)(delegate* unmanaged<TimelineContainer*, ushort, ushort, void*, byte>)&NativeActionTimelineOriginal
            : (nint)(delegate* unmanaged<ActionTimelineSequencer*, ushort, void*, byte>)&NativeTimelineOriginal;
        InstallDelegate(field, Marshal.GetDelegateForFunctionPointer(original, delegateType));
    }
    void Invoke(string method, params object?[] args) => RuntimeType.GetMethod(method, All)!.Invoke(Runtime, args);
    public static void Set(object target, string field, object? value) => target.GetType().GetField(field, All)!.SetValue(target, value);
    static object Ptr(void* pointer, Type type) => Pointer.Box(pointer, type.MakePointerType());
    static T Proxy<T>(Func<MethodInfo, object?[]?, object?> call) where T : class
    { var proxy = DispatchProxy.Create<T, ServiceProxy>(); ((ServiceProxy)(object)proxy).Call = call; return proxy; }
    void Install<T>(string field, T original) where T : Delegate
        => InstallDelegate(field, original);
    void InstallDelegate(string field, Delegate original)
    {
        var type = typeof(Hook<>).Assembly.GetType("Dalamud.Hooking.Internal.FunctionPointerVariableHook`1")!.MakeGenericType(original.GetType());
        var hook = RuntimeHelpers.GetUninitializedObject(type); Set(hook, "originalDelegate", original); Set(Runtime, field, hook);
    }
    T* Alloc<T>() where T : unmanaged { var pointer = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T)); allocations.Add((nint)pointer); return pointer; }
    [UnmanagedCallersOnly] static ulong Identity(GameObject* obj) => obj->EntityId;
    [UnmanagedCallersOnly] static Character* Lookup(ClientObjectManager* manager, ushort slot) => (Character*)manager->BattleCharas[slot].BattleChara;
    [UnmanagedCallersOnly] static void Delete(ClientObjectManager* manager, ushort slot, byte param) { current.Deletes++; manager->BattleCharas[slot].BattleChara = null; }
    [UnmanagedCallersOnly] static byte NativeTimelineOriginal(ActionTimelineSequencer* sequence, ushort id, void* param)
    {
        current.OriginalCalls++; current.LastPointer = (nint)sequence;
        current.LastIntro = id; current.LastParam = (nint)param;
        if (sequence != null) sequence->TimelineIds[0] = id;
        current.DuringOriginal?.Invoke();
        return current.TimelineNativeResult;
    }
    [UnmanagedCallersOnly] static byte NativeActionTimelineOriginal(TimelineContainer* timeline, ushort intro, ushort loop, void* param)
    {
        current.OriginalCalls++; current.LastPointer = (nint)timeline;
        current.LastIntro = intro; current.LastLoop = loop; current.LastParam = (nint)param;
        if (timeline != null) timeline->TimelineSequencer.TimelineIds[0] = intro;
        current.DuringOriginal?.Invoke();
        return current.TimelineNativeResult;
    }
    public void Dispose()
    {
        ClientObjectManager.Addresses.Instance.Value = previousManager; ClientObjectManager.Addresses.GetObjectByIndex.Value = previousLookup;
        ClientObjectManager.Addresses.DeleteObjectByIndex.Value = previousDelete; Control.Addresses.GetLocalPlayer.Value = previousLocal;
        foreach (var pointer in allocations) NativeMemory.Free((void*)pointer);
        NativeMemory.Free(table); NativeMemory.Free(local);
    }
}

public class ServiceProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Call = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
}

sealed class GameDataFixture(Lumina.GameData game) : IDataManager
{
    public Dalamud.Game.ClientLanguage Language => Dalamud.Game.ClientLanguage.ChineseSimplified;
    public Lumina.GameData GameData => game;
    public Lumina.Excel.ExcelModule Excel => game.Excel;
    public bool HasModifiedGameDataFiles => false;
    public Lumina.Excel.ExcelSheet<T> GetExcelSheet<T>(Dalamud.Game.ClientLanguage? language = null, string? name = null) where T : struct, Lumina.Excel.IExcelRow<T> => game.GetExcelSheet<T>(name: name)!;
    public Lumina.Excel.SubrowExcelSheet<T> GetSubrowExcelSheet<T>(Dalamud.Game.ClientLanguage? language = null, string? name = null) where T : struct, Lumina.Excel.IExcelSubrow<T> => throw new NotSupportedException();
    public Lumina.Data.FileResource? GetFile(string path) => throw new NotSupportedException();
    public T? GetFile<T>(string path) where T : Lumina.Data.FileResource => throw new NotSupportedException();
    public Task<T> GetFileAsync<T>(string path, CancellationToken token) where T : Lumina.Data.FileResource => throw new NotSupportedException();
    public bool FileExists(string path) => throw new NotSupportedException();
}
