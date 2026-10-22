using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using NativeAgentId = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId;

static unsafe class LightControlLifecycleCases
{
    public static void Run(Action<bool,string> check)
    {
        using (var f = new LightControlFixture())
        {
            var messages = new List<string>(); var trace = new PortraitLightInputTrace(messages.Add);
            trace.Enter(PortraitLightTraceStage.NativeSyncControls, f.Sync);
            check(f.Writes.SequenceEqual(new[] { (30, false), (-45, false) }) && f.DirtyCalls == 1,
                "actual LiveApi updates owned vertical/horizontal controls in order without dispatch, then dirties entry");
            check(f.FullyLoadedCalls >= 3, "actual LiveApi checks native fully-loaded readiness across setter boundaries");
            string[] phases = ["NativeSyncControls begin", "VerticalSliderSetValue begin", "VerticalSliderSetValue end",
                "HorizontalSliderSetValue begin", "HorizontalSliderSetValue end", "SetHasChanged begin", "SetHasChanged end", "NativeSyncControls end"];
            check(messages.Select(x => x[(x.IndexOf(" phase=", StringComparison.Ordinal) + 7)..]).SequenceEqual(phases),
                "qualified real LiveApi retains observable native control stages in their original order");
        }
        using (var f = new LightControlFixture())
        {
            f.FrameworkThread = false; f.Sync();
            check(f.EditorLookups == 0 && f.FullyLoadedCalls == 0 && f.Writes.Count == 0 && f.DirtyCalls == 0,
                "off-Framework synchronization stops before native editor lookup or control access");
        }
        foreach (var phase in new[] { 0, 1 })
        {
            using var f = new LightControlFixture();
            if (phase == 0) f.Addon->Flags1A1 = 0; else f.FullyLoaded = false;
            f.Sync();
            check(f.Writes.Count == 0 && f.DirtyCalls == 0,
                "unready or incompletely loaded addon never receives native slider writes");
        }
        foreach (var applied in new[] { false, true })
        {
            using var f = new LightControlFixture();
            BeginClose(f.Addon, applied);
            f.Sync();
            check(f.FullyLoadedCalls == 0 && f.Writes.Count == 0 && f.DirtyCalls == 0,
                "closing transition rejects controls before native fully-loaded readiness call");
        }
        foreach (var applied in new[] { false, true })
        {
            using var f = new LightControlFixture();
            f.AfterFullyLoaded = () => BeginClose(f.Addon, applied);
            f.Sync();
            check(f.FullyLoadedCalls == 1 && f.Writes.Count == 0 && f.DirtyCalls == 0,
                "close transition entered inside readiness callback prevents first native control write");
        }
        using (var f = new LightControlFixture())
        {
            f.AfterFullyLoaded = () => f.Editor.Editor->EditorState = null;
            f.Sync();
            check(f.Writes.Count == 0 && f.DirtyCalls == 0,
                "reentrant fully-loaded check detaching editor stops before first control write");
        }
        using (var f = new LightControlFixture())
        {
            f.AfterFullyLoaded = () => f.Addon->Flags1A1 = 0;
            f.Sync();
            check(f.Writes.Count == 0 && f.DirtyCalls == 0,
                "readiness lost inside native readiness callback is rechecked");
        }
        using (var f = new LightControlFixture())
        {
            f.AfterFullyLoaded = () => f.CurrentAddon = f.OtherAddon;
            f.Sync();
            check(f.Writes.Count == 0 && f.DirtyCalls == 0,
                "addon replacement inside native readiness callback stops before first control write");
        }
        foreach (var corruption in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 })
        {
            using var f = new LightControlFixture();
            switch (corruption)
            {
                case 0: f.Nodes[0] = null; break;
                case 1: f.Vertical->OwnerNode = f.HorizontalNode; break;
                case 2: f.VerticalInfo->ComponentType = ComponentType.Button; break;
                case 3: f.Vertical->UldManager.BaseType = AtkUldManagerBaseType.Widget; break;
                case 4: f.VerticalNode->Type = NodeType.Image; break;
                case 5: f.VerticalNode->Component = (AtkComponentBase*)f.Horizontal; break;
                case 6: f.Vertical->UldManager.Objects = null; break;
                case 7: f.Vertical->UldManager.LoadedState = AtkLoadState.ResourceLoading; break;
                case 8: f.Addon->UldManager.NodeListCount = 3; break;
                case 9: f.Addon->UldManager.NodeList = null; break;
            }
            f.Sync();
            check(f.Writes.Count == 0 && f.DirtyCalls == 0,
                "foreign node, broken owner backlink or wrong component type cannot reach slider setter");
        }
        // Native slider qualification requires both initialized and allocated arrays; LoadedState
        // alone can be stale during component teardown. Exercise the actual LiveApi write boundary.
        foreach (var vertical in new[] { true, false })
        foreach (var flags in new[] { AtkUldManagerResourceFlag.None, AtkUldManagerResourceFlag.Initialized,
            AtkUldManagerResourceFlag.ArraysAllocated })
        {
            using var f = new LightControlFixture();
            var slider = vertical ? f.Vertical : f.Horizontal;
            slider->UldManager.ResourceFlags = flags;
            f.Sync();
            check(f.Writes.Count == 0 && f.DirtyCalls == 0,
                $"uninitialized or unallocated {(vertical ? "vertical" : "horizontal")} component ({flags}) stops both slider writes");
        }
        using (var f = new LightControlFixture())
        {
            f.Vertical->UldManager.ResourceFlags |= AtkUldManagerResourceFlag.KeepUldResourceHandle;
            f.Horizontal->UldManager.ResourceFlags |= AtkUldManagerResourceFlag.ContainsClippingMaskNode;
            f.Sync();
            check(f.Writes.Count == 2 && f.DirtyCalls == 1,
                "initialized and allocated components retain unrelated native resource flags");
        }
        using (var f = new LightControlFixture())
        {
            f.AfterVertical = () => f.Horizontal->UldManager.ResourceFlags &= ~AtkUldManagerResourceFlag.ArraysAllocated;
            f.Sync();
            check(f.Writes.Count == 1 && f.DirtyCalls == 0,
                "component arrays retired by vertical setter prevent horizontal write");
        }
        using (var f = new LightControlFixture())
        {
            f.AfterHorizontal = () => f.Vertical->UldManager.ResourceFlags &= ~AtkUldManagerResourceFlag.Initialized;
            f.Sync();
            check(f.Writes.Count == 2 && f.DirtyCalls == 0,
                "component initialization retired by horizontal setter prevents dirty write");
        }
        using (var f = new LightControlFixture())
        {
            f.Addon->DirectionalLightingVerticalAngleSlider = null;
            f.Sync();
            check(f.Writes.Count == 0 && f.DirtyCalls == 0,
                "missing vertical control aborts the whole synchronization before horizontal write");
        }
        using (var f = new LightControlFixture())
        {
            f.Addon->DirectionalLightingHorizontalAngleSlider = null;
            f.Sync();
            check(f.Writes.Count == 0 && f.DirtyCalls == 0,
                "both controls are qualified before the first slider write");
        }
        foreach (var transition in new[] { 0, 1, 2, 3, 4 })
        {
            using var f = new LightControlFixture();
            f.AfterVertical = () =>
            {
                if (transition == 0) f.Addon->Flags1A1 = 0;
                if (transition == 1) f.CurrentAddon = f.OtherAddon;
                if (transition == 2) f.HorizontalNode->Component = null;
                if (transition == 3) f.Editor.Editor->EditorState = null;
                if (transition == 4) f.VerticalNode->Component = null;
            };
            f.Sync();
            check(f.Writes.Count == 1 && f.DirtyCalls == 0,
                "lifecycle or horizontal-node replacement after vertical setter stops later writes");
        }
        using (var f = new LightControlFixture())
        {
            f.AfterHorizontal = () => f.Addon->Flags1A1 = 0;
            f.Sync();
            check(f.Writes.Count == 2 && f.DirtyCalls == 0,
                "readiness loss after second setter cannot dirty a closing addon state");
        }
        foreach (var applied in new[] { false, true })
        {
            using var f = new LightControlFixture();
            f.AfterVertical = () => BeginClose(f.Addon, applied);
            f.Sync();
            check(f.FullyLoadedCalls == 1 && f.Writes.Count == 1 && f.DirtyCalls == 0,
                "close transition after vertical setter skips later readiness vcall and horizontal write");
        }
        foreach (var applied in new[] { false, true })
        {
            using var f = new LightControlFixture();
            f.AfterHorizontal = () => BeginClose(f.Addon, applied);
            f.Sync();
            check(f.FullyLoadedCalls == 2 && f.Writes.Count == 2 && f.DirtyCalls == 0,
                "close transition after horizontal setter skips later readiness vcall and dirty write");
        }
        using (var f = new LightControlFixture())
        {
            f.Addon->VisibilityState = AtkUnitBaseVisibilityState.Hide | AtkUnitBaseVisibilityState.TransitionComplete;
            f.Addon->AppliedVisibilityState = AtkUnitBaseVisibilityState.Hide | AtkUnitBaseVisibilityState.TransitionComplete;
            f.Sync();
            check(f.Writes.Count == 2 && f.DirtyCalls == 1,
                "hidden but still ready owned addon can synchronize without changing ownership rules");
        }
    }

    private static void BeginClose(AddonBannerEditor* addon, bool applied)
    {
        if (applied) addon->AppliedVisibilityState = AtkUnitBaseVisibilityState.Hide;
        else addon->VisibilityState = AtkUnitBaseVisibilityState.Hide;
    }
}

// Execute the real private LiveApi and SDK layout; only process-external leaf functions are replaced.
// All allocations are local to this offline process, never attached to a game or registered as hooks.
unsafe sealed class LightControlFixture : IDisposable
{
    private static LightControlFixture? active;
    private readonly List<nint> allocations = [];
    private readonly nint oldFramework = NativeFramework.Addresses.Instance.Value;
    private readonly nint oldUi = NativeFramework.Addresses.GetUIModule.Value;
    private readonly nint oldAgent = AgentModule.Addresses.GetAgentByInternalId.Value;
    private readonly nint oldSlider = AtkComponentSlider.Addresses.SetValue.Value;
    private readonly nint oldDirty = AgentBannerEditorState.Addresses.SetHasChanged.Value;
    private readonly IPortraitLightNativeApi live;
    private readonly NativeFramework* framework;
    private readonly UIModule* ui;
    private readonly AgentModule* module;
    public readonly EditorFixture Editor = new();
    public readonly AddonBannerEditor* Addon;
    public readonly AddonBannerEditor* OtherAddon;
    public AddonBannerEditor* CurrentAddon;
    public readonly AtkComponentSlider* Vertical;
    public readonly AtkComponentSlider* Horizontal;
    public readonly AtkComponentNode* VerticalNode;
    public readonly AtkComponentNode* HorizontalNode;
    public readonly AtkUldComponentInfo* VerticalInfo;
    public readonly AtkResNode** Nodes;
    public bool FullyLoaded = true, FrameworkThread = true;
    public int FullyLoadedCalls, DirtyCalls, EditorLookups;
    public readonly List<(int Value, bool Dispatch)> Writes = [];
    public Action? AfterFullyLoaded, AfterVertical, AfterHorizontal;
    private T* Allocate<T>() where T : unmanaged
    { var p = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T)); allocations.Add((nint)p); return p; }
    public LightControlFixture()
    {
        active = this;
        framework = Allocate<NativeFramework>(); ui = Allocate<UIModule>(); module = Allocate<AgentModule>();
        var instance = Allocate<nint>(); *instance = (nint)framework;
        var uiTable = Allocate<UIModule.UIModuleVirtualTable>(); uiTable->GetAgentModule = &GetModule; ui->VirtualTable = uiTable;
        NativeFramework.Addresses.Instance.Value = (nint)instance;
        NativeFramework.Addresses.GetUIModule.Value = (nint)(delegate* unmanaged<NativeFramework*, UIModule*>)&GetUi;
        AgentModule.Addresses.GetAgentByInternalId.Value = (nint)(delegate* unmanaged<AgentModule*, NativeAgentId, AgentInterface*>)&GetEditor;
        AtkComponentSlider.Addresses.SetValue.Value = (nint)(delegate* unmanaged<AtkComponentSlider*, int, byte, void>)&SetSlider;
        AgentBannerEditorState.Addresses.SetHasChanged.Value = (nint)(delegate* unmanaged<AgentBannerEditorState*, byte, void>)&SetDirty;
        Addon = Allocate<AddonBannerEditor>(); OtherAddon = Allocate<AddonBannerEditor>(); CurrentAddon = Addon;
        var table = Allocate<AtkUnitBase.AtkUnitBaseVirtualTable>();
        table->IsFullyLoaded = (delegate* unmanaged<AtkUnitBase*, bool>)(nint)(delegate* unmanaged<AtkUnitBase*, byte>)&Loaded;
        Addon->AtkUnitBase.VirtualTable = table; Addon->Flags1A1 = 1; Addon->Id = 987;
        Editor.Editor->AddonId = 987; Addon->UldManager.LoadedState = AtkLoadState.Loaded;
        var list = (AtkResNode**)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint)); allocations.Add((nint)list); Nodes = list;
        Addon->UldManager.NodeList = Nodes; Addon->UldManager.NodeListCount = 2; Addon->UldManager.NodeListSize = 2;
        Vertical = Allocate<AtkComponentSlider>(); Horizontal = Allocate<AtkComponentSlider>();
        VerticalNode = Allocate<AtkComponentNode>(); HorizontalNode = Allocate<AtkComponentNode>();
        VerticalInfo = Allocate<AtkUldComponentInfo>(); var horizontalInfo = Allocate<AtkUldComponentInfo>();
        Configure(Vertical, VerticalNode, VerticalInfo); Configure(Horizontal, HorizontalNode, horizontalInfo);
        Nodes[0] = (AtkResNode*)VerticalNode; Nodes[1] = (AtkResNode*)HorizontalNode;
        Addon->DirectionalLightingVerticalAngleSlider = Vertical; Addon->DirectionalLightingHorizontalAngleSlider = Horizontal;
        *OtherAddon = *Addon;
        var services = DispatchProxy.Create<IFramework, LightFrameworkProxy>();
        ((LightFrameworkProxy)(object)services).ReadFrameworkThread = () => FrameworkThread;
        var type = typeof(NativePortraitTarget).GetNestedType("LiveApi", BindingFlags.NonPublic)!;
        live = (IPortraitLightNativeApi)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [services, null, new LightGameGui(() => (nint)CurrentAddon), null], null)!;
    }
    private static void Configure(AtkComponentSlider* slider, AtkComponentNode* node, AtkUldComponentInfo* info)
    {
        node->Type = (NodeType)1001; node->Component = (AtkComponentBase*)slider; slider->OwnerNode = node;
        slider->UldManager.BaseType = AtkUldManagerBaseType.Component; slider->UldManager.LoadedState = AtkLoadState.Loaded;
        slider->UldManager.ResourceFlags = AtkUldManagerResourceFlag.Initialized | AtkUldManagerResourceFlag.ArraysAllocated;
        info->ComponentType = ComponentType.Slider; slider->UldManager.Objects = (AtkUldObjectInfo*)info;
    }
    public void Sync() => live.SyncDirectionalAngles(Editor.State, 30, -45);
    [UnmanagedCallersOnly] private static UIModule* GetUi(NativeFramework* p) => active!.ui;
    [UnmanagedCallersOnly] private static AgentModule* GetModule(UIModule* p) => active!.module;
    [UnmanagedCallersOnly] private static AgentInterface* GetEditor(AgentModule* p, NativeAgentId id)
    { active!.EditorLookups++; return (AgentInterface*)active.Editor.Editor; }
    // The external C++ bool ABI is one byte; UCO rejects managed bool parameters themselves.
    [UnmanagedCallersOnly] private static byte Loaded(AtkUnitBase* p)
    { active!.FullyLoadedCalls++; active.AfterFullyLoaded?.Invoke(); return active.FullyLoaded ? (byte)1 : (byte)0; }
    [UnmanagedCallersOnly] private static void SetSlider(AtkComponentSlider* p, int value, byte dispatch)
    {
        var a = active!; a.Writes.Add((value, dispatch != 0)); p->Value = value;
        if (p == a.Vertical) a.AfterVertical?.Invoke(); else a.AfterHorizontal?.Invoke();
    }
    [UnmanagedCallersOnly] private static void SetDirty(AgentBannerEditorState* p, byte changed) => active!.DirtyCalls++;
    public void Dispose()
    {
        NativeFramework.Addresses.Instance.Value = oldFramework; NativeFramework.Addresses.GetUIModule.Value = oldUi;
        AgentModule.Addresses.GetAgentByInternalId.Value = oldAgent; AtkComponentSlider.Addresses.SetValue.Value = oldSlider;
        AgentBannerEditorState.Addresses.SetHasChanged.Value = oldDirty;
        active = null; Editor.Dispose(); foreach (var allocation in allocations) NativeMemory.Free((void*)allocation);
    }
}

public class LightFrameworkProxy : DispatchProxy
{
    public Func<bool> ReadFrameworkThread { get; set; } = () => true;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod!.Name == "get_IsInFrameworkUpdateThread" ? ReadFrameworkThread() : throw new NotSupportedException(targetMethod.Name);
}

unsafe sealed class LightGameGui(Func<nint> resolve) : IGameGui
{
    public bool GameUiHidden => false;
    public ulong HoveredItem { get; set; }
    public Dalamud.Game.Gui.HoveredAction HoveredAction => default!;
    public event EventHandler<bool> UiHideToggled { add { } remove { } }
    public event EventHandler<ulong> HoveredItemChanged { add { } remove { } }
    public event EventHandler<Dalamud.Game.Gui.HoveredAction> HoveredActionChanged { add { } remove { } }
    public event Action<Dalamud.Game.Gui.AgentUpdateFlag> AgentUpdate { add { } remove { } }
    public bool OpenMapWithMapLink(Dalamud.Game.Text.SeStringHandling.Payloads.MapLinkPayload link) => throw new NotSupportedException();
    public bool OpenMapWithMapLink(uint territory, uint map, System.Numerics.Vector3 pos) => throw new NotSupportedException();
    public bool WorldToScreen(System.Numerics.Vector3 pos, out System.Numerics.Vector2 screen) => throw new NotSupportedException();
    public bool WorldToScreen(System.Numerics.Vector3 pos, out System.Numerics.Vector2 screen, out bool inView) => throw new NotSupportedException();
    public bool ScreenToWorld(System.Numerics.Vector2 pos, out System.Numerics.Vector3 world, float distance = 100000f) => throw new NotSupportedException();
    public Dalamud.Game.NativeWrapper.UIModulePtr GetUIModule() => throw new NotSupportedException();
    public Dalamud.Game.NativeWrapper.AtkUnitBasePtr GetAddonByName(string name, int index = 1) => new(resolve());
    public Dalamud.Game.NativeWrapper.AtkUnitBasePtr GetAddonByName(ReadOnlySpan<byte> name, int index = 1) => new(resolve());
    public T* GetAddonByName<T>(string name, int index = 1) where T : unmanaged => (T*)resolve();
    public T* GetAddonByName<T>(ReadOnlySpan<byte> name, int index = 1) where T : unmanaged => (T*)resolve();
    public Dalamud.Game.NativeWrapper.AgentInterfacePtr GetAgentById(int id) => throw new NotSupportedException();
    public Dalamud.Game.NativeWrapper.AgentInterfacePtr FindAgentInterface(string name) => throw new NotSupportedException();
    public Dalamud.Game.NativeWrapper.AgentInterfacePtr FindAgentInterface(ReadOnlySpan<byte> name) => throw new NotSupportedException();
    public Dalamud.Game.NativeWrapper.AgentInterfacePtr FindAgentInterface(Dalamud.Game.NativeWrapper.AtkUnitBasePtr addon) => throw new NotSupportedException();
}
