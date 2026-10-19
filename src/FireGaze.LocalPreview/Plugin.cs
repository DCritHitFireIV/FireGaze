using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Game.Text.SeStringHandling;
using FireGaze.LocalPreview.Interaction;
using FireGaze.LocalPreview.Runtime;
using FireGaze.LocalPreview.Core;
using MenuKind = FireGaze.LocalPreview.Interaction.PreviewKind;

namespace FireGaze.LocalPreview;

/// <summary>Isolated local host. The existing FireGaze plugin is not patched or replaced.</summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IObjectTable Objects { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IDataManager Data { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider Interop { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IChatGui Chat { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] public static ICommandManager Commands { get; private set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly DateTime loadedAt = DateTime.UtcNow;
    private readonly ICallGateProvider<string> diagnosticsIpc;
    private readonly ICallGateProvider<string> captureProjectionIpc;
    private readonly ICallGateProvider<int, uint, long> beginExternalIpc;
    private readonly ICallGateProvider<long, bool, bool> endExternalIpc;
    private readonly ICallGateProvider<object> hmsLifecycleIpc;
    private readonly ICallGateSubscriber<int> hmsApi;
    private readonly ICallGateSubscriber<int, object> hmsHostState;
    private ActorPreviewRuntime? runtime;
    private PreviewInteractionService? interaction;
    private bool enabled = true;
    private bool initialized;
    private bool visible;
    private bool disposed;
    private string lastMessage = "等待插件加载阶段完成";
    private HmsCoexistenceDecision coexistence = HmsCoexistencePolicy.Decide(0, null);
    private DateTime nextCoexistenceCheck;
    private int loadedHmsCount;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        Commands.AddHandler("/fgpreview", new CommandInfo(OnCommand)
        {
            HelpMessage = "本地试用验证：status / cancel / on / off / mount|minion|accessory|emote <编号>",
        });
        diagnosticsIpc = pluginInterface.GetIpcProvider<string>("FireGaze.LocalPreview.Diagnostics");
        diagnosticsIpc.RegisterFunc(() => OnFramework(Diagnostics));
        hmsApi = pluginInterface.GetIpcSubscriber<int>("HMSync.LocalPreview.ApiVersion");
        hmsHostState = pluginInterface.GetIpcSubscriber<int, object>("HMSync.LocalPreview.HostState");
        captureProjectionIpc = pluginInterface.GetIpcProvider<string>("FireGaze.LocalPreview.GetCaptureProjection");
        captureProjectionIpc.RegisterFunc(() => OnFramework(() => runtime is null
            ? "{\"ApiVersion\":1,\"Active\":false}"
            : JsonSerializer.Serialize(runtime.GetCaptureProjection())));
        beginExternalIpc = pluginInterface.GetIpcProvider<int, uint, long>("FireGaze.LocalPreview.BeginExternalPresentation");
        beginExternalIpc.RegisterFunc((kind, id) => OnFramework(() => runtime?.BeginExternalPresentation(kind, id) ?? 0));
        endExternalIpc = pluginInterface.GetIpcProvider<long, bool, bool>("FireGaze.LocalPreview.EndExternalPresentation");
        endExternalIpc.RegisterFunc((token, accepted) => OnFramework(() => runtime?.EndExternalPresentation(token, accepted) == true));
        hmsLifecycleIpc = pluginInterface.GetIpcProvider<object>("FireGaze.LocalPreview.BeforeHmsLifecycle");
        hmsLifecycleIpc.RegisterAction(() => OnFramework(() => { runtime?.BeforeHmsLifecycle(); return true; }));
        NotifyHmsHostState(1);
        Framework.Update += Update;
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenUi;
        pluginInterface.UiBuilder.OpenConfigUi += OpenUi;
        Log.Information("[LocalPreview] 独立宿主已加载，版本 0.1.0.1");
    }

    private void Update(IFramework framework)
    {
        if (disposed || DateTime.UtcNow - loadedAt < TimeSpan.FromSeconds(3)) return;
        if (!initialized || runtime?.HasActivePreview == true || DateTime.UtcNow >= nextCoexistenceCheck)
        {
            RefreshCoexistence();
            nextCoexistenceCheck = DateTime.UtcNow.AddSeconds(1);
        }
        if (!coexistence.Allowed && runtime?.HasActivePreview == true)
        {
            runtime.BeforeHmsLifecycle();
            lastMessage = coexistence.Reason;
        }
        if (initialized) return;
        initialized = true;
        try
        {
            runtime = new ActorPreviewRuntime(Framework, ClientState, Objects, Condition, Data, Interop, Log);
            interaction = new PreviewInteractionService(ContextMenu, Chat, GameGui, Objects, Data,
                ClientState, Interop, Log, Request, () => runtime?.CurrentNativeEmote, AddonLifecycle);
            runtime.EmoteObserved += RecordEmote;
            runtime.NativeInputAccepted += OnNativeAccepted;
            runtime.PreviewCancelled += OnCancelled;
            lastMessage = "本地试用入口已就绪";
            if (!enabled) { interaction.Dispose(); interaction = null; }
            Log.Information("[LocalPreview] 初始化完成：" + Diagnostics());
        }
        catch (Exception ex)
        {
            interaction?.Dispose();
            runtime?.Dispose();
            interaction = null;
            runtime = null;
            lastMessage = "初始化失败，查看卫月日志";
            Log.Error(ex, "[LocalPreview] 初始化失败");
        }
    }

    private void Request(MenuKind kind, uint id)
    {
        if (disposed || !enabled || runtime is null)
        {
            Chat.PrintError("[本地试用] 功能尚未就绪或已关闭。");
            return;
        }
        RefreshCoexistence();
        if (!coexistence.Allowed)
        {
            lastMessage = coexistence.Reason;
            Chat.PrintError("[本地试用] " + lastMessage);
            return;
        }
        if (id == 0 || id > ushort.MaxValue)
        {
            Chat.PrintError("[本地试用] 无效的试用编号。");
            return;
        }
        var slot = kind switch
        {
            MenuKind.Mount => PreviewSlot.Mount,
            MenuKind.Minion => PreviewSlot.Minion,
            MenuKind.Accessory => PreviewSlot.Accessory,
            _ => PreviewSlot.Emote,
        };
        try
        {
            // Invalidate any previous clean-unload marker before native display is changed.
            if (!NotifyHmsHostState(1) && loadedHmsCount != 0)
            {
                lastMessage = "HMS 兼容桥尚未就绪，请稍后再试。";
                Chat.PrintError("[本地试用] " + lastMessage);
                return;
            }
            if (runtime.TryApply(new PreviewEffect(slot, (ushort)id), out var reason))
            {
                lastMessage = $"已本地试用 {kind} #{id}";
                Log.Information("[LocalPreview] " + lastMessage);
            }
            else
            {
                lastMessage = reason;
                Chat.PrintError("[本地试用] " + reason);
            }
        }
        catch (Exception ex)
        {
            lastMessage = "试用失败，查看卫月日志";
            Log.Error(ex, "[LocalPreview] 试用请求失败");
            Chat.PrintError("[本地试用] " + lastMessage);
        }
    }

    private void OnNativeAccepted()
    {
        lastMessage = "原生操作已被客户端接受，全部试用已结束";
    }

    private void OnCancelled(string reason)
    {
        lastMessage = reason;
    }

    private void Cancel()
    {
        runtime?.ClearAll();
        lastMessage = "全部试用已结束";
    }

    private void SetEnabled(bool value)
    {
        if (enabled == value) return;
        enabled = value;
        if (!value)
        {
            Cancel();
            interaction?.Dispose();
            interaction = null;
            lastMessage = "本地试用已关闭";
        }
        else if (runtime is not null)
        {
            interaction = new PreviewInteractionService(ContextMenu, Chat, GameGui, Objects, Data,
                ClientState, Interop, Log, Request, () => runtime?.CurrentNativeEmote, AddonLifecycle);
            runtime.EmoteObserved -= RecordEmote;
            runtime.EmoteObserved += RecordEmote;
            lastMessage = "本地试用已开启";
        }
    }

    private void RecordEmote(ulong actorId, ushort emoteId) => interaction?.RecordEmote(actorId, emoteId);

    private static T OnFramework<T>(Func<T> action) => Framework.IsInFrameworkUpdateThread
        ? action() : Framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();

    private void RefreshCoexistence()
    {
        var count = pluginInterface.InstalledPlugins.Count(p => p.IsLoaded &&
            (p.InternalName.Equals("HMSync", StringComparison.OrdinalIgnoreCase) ||
             p.InternalName.Equals("HMSyncDev", StringComparison.OrdinalIgnoreCase)));
        int? apiVersion = null;
        if (count == 1)
        {
            try { apiVersion = hmsApi.InvokeFunc(); }
            catch { /* Missing compatibility provider is not an idle HMS state. */ }
        }
        coexistence = HmsCoexistencePolicy.Decide(count, apiVersion);
        loadedHmsCount = count;
    }

    private bool NotifyHmsHostState(int state)
    {
        try { hmsHostState.InvokeAction(state); return true; }
        catch { return false; }
    }

    private void OnCommand(string command, string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) { visible = !visible; return; }
        switch (parts[0].ToLowerInvariant())
        {
            case "status": Chat.Print("[本地试用] " + Diagnostics()); break;
            case "cancel": Cancel(); break;
            case "on": SetEnabled(true); break;
            case "off": SetEnabled(false); break;
            case "chatprobe":
                lastMessage = interaction?.EnableNativeChatProbe() == true
                    ? "情感提示整句原生右键探针已开启，请使用原生情感动作并右键提示。"
                    : "聊天探针未能开启，请查看诊断。";
                Chat.Print("[本地试用] " + lastMessage);
                break;
            case "sampleemote":
                if (parts.Length == 2 && ushort.TryParse(parts[1], out var emoteId) && emoteId != 0 &&
                    Data.GetExcelSheet<Lumina.Excel.Sheets.Emote>().TryGetRow(emoteId, out var emote))
                {
                    interaction?.EnableNativeChatProbe();
                    var text = System.Text.Encoding.UTF8.GetBytes($"[本地试用验证] 左键点击这整句话试用：{emote.Name}");
                    var linked = interaction?.WrapEmoteMessage(text, emoteId) ?? [];
                    if (linked.Length != 0) Chat.Print(SeString.Parse(linked));
                }
                else Chat.PrintError("[本地试用] 用法：/fgpreview sampleemote <有效情感动作编号>");
                break;
            case "sampleitem":
                if (parts.Length == 2 && uint.TryParse(parts[1], out var itemId) && itemId != 0 &&
                    Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(itemId, out var item))
                    Chat.Print(new SeStringBuilder().AddText("[本地试用验证] ")
                        .AddItemLink(itemId, false, item.Name.ToString()).Build());
                else Chat.PrintError("[本地试用] 用法：/fgpreview sampleitem <有效物品编号>");
                break;
            case "mount": ParseRequest(MenuKind.Mount, parts); break;
            case "minion": ParseRequest(MenuKind.Minion, parts); break;
            case "accessory": ParseRequest(MenuKind.Accessory, parts); break;
            case "emote": ParseRequest(MenuKind.Emote, parts); break;
            default: Chat.PrintError("[本地试用] 用法：/fgpreview status|cancel|on|off|mount|minion|accessory|emote <编号>"); break;
        }
    }

    private void ParseRequest(MenuKind kind, string[] parts)
    {
        if (parts.Length != 2 || !uint.TryParse(parts[1], out var id))
        {
            Chat.PrintError("[本地试用] 请提供一个有效编号。");
            return;
        }
        Request(kind, id);
    }

    private string Diagnostics() => JsonSerializer.Serialize(new
    {
        version = "0.1.0.1", enabled, initialized, coexistence,
        loggedIn = ClientState.IsLoggedIn,
        territory = ClientState.TerritoryType,
        capabilities = runtime?.Capabilities,
        timelineObservers = new { play = runtime?.TimelinePlayStatus, action = runtime?.ActionTimelinePlayStatus },
        weaponVisibility = new { installed = runtime?.WeaponVisibilityGatesInstalled,
            regularSuppressed = runtime?.WeaponVisibilitySuppressions, extraSuppressed = runtime?.ExtraWeaponVisibilitySuppressions },
        interaction = interaction?.Capabilities,
        observedEmotes = interaction?.ObservedEmotes,
        chatMessagesLinked = interaction?.ChatMessagesLinked,
        chatMessagesWithoutExactContext = interaction?.ChatMessagesWithoutExactContext,
        nativeEmoteMenusObserved = interaction?.NativeEmoteMenusObserved,
        nativeEmoteLinkEventsObserved = interaction?.NativeEmoteLinkEventsObserved,
        nativeLogCaptureEnabled = interaction?.NativeLogCaptureEnabled,
        nativeLogMessagesLinked = interaction?.NativeLogMessagesLinked,
        uniqueEmoteLogTemplates = interaction?.UniqueEmoteLogTemplates,
        ambiguousEmoteLogTemplates = interaction?.AmbiguousEmoteLogTemplates,
        unmatchedNativeLogFormats = interaction?.UnmatchedNativeLogFormats,
        nativeLogCaptureDetail = interaction?.NativeLogCaptureDetail,
        itemMenusResolved = interaction?.ItemMenusResolved,
        lastMenuContext = interaction?.LastMenuContext,
        active = runtime?.ActiveEffects,
        lastMessage,
    });

    private void OpenUi() => visible = true;

    private void Draw()
    {
        if (!visible || disposed) return;
        if (ImGui.Begin("FireGaze 本地试用验证", ref visible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var currentEnabled = enabled;
            if (ImGui.Checkbox("启用本地试用", ref currentEnabled)) SetEnabled(currentEnabled);
            ImGui.TextWrapped(lastMessage);
            if (!coexistence.Allowed) ImGui.TextWrapped(coexistence.Reason);
            if (runtime is not null)
                foreach (var effect in runtime.ActiveEffects) ImGui.TextUnformatted($"{effect.Slot}: {effect.Id}");
            if (ImGui.Button("结束全部试用")) Cancel();
            ImGui.SameLine();
            if (ImGui.Button("输出诊断")) Chat.Print("[本地试用] " + Diagnostics());
        }
        ImGui.End();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Framework.Update -= Update;
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= OpenUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenUi;
        Commands.RemoveHandler("/fgpreview");
        diagnosticsIpc.UnregisterFunc();
        interaction?.Dispose();
        if (runtime is not null)
        {
            runtime.EmoteObserved -= RecordEmote;
            runtime.NativeInputAccepted -= OnNativeAccepted;
            runtime.PreviewCancelled -= OnCancelled;
            runtime.Dispose();
        }
        // Only a completed cleanup can release HMS's conservative capture pause on unload.
        NotifyHmsHostState(0);
        captureProjectionIpc.UnregisterFunc();
        beginExternalIpc.UnregisterFunc();
        endExternalIpc.UnregisterFunc();
        hmsLifecycleIpc.UnregisterAction();
        Log.Information("[LocalPreview] 独立宿主已卸载");
    }
}
