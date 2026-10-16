using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Game.Text.SeStringHandling;
using FireGaze.LocalPreview.Interaction;
using FireGaze.LocalPreview.Runtime;
using FireGaze.LocalPreview.Core;
using MenuKind = FireGaze.LocalPreview.Interaction.PreviewKind;

namespace FireGaze.LocalPreview;

using FireGaze.LocalPreview.Compatibility;

/// <summary>FireGaze-owned local preview feature and diagnostics.</summary>
public sealed class LocalPreviewFeature : IDisposable
{
    private readonly IFramework Framework;
    private readonly IClientState ClientState;
    private readonly IObjectTable Objects;
    private readonly ICondition Condition;
    private readonly IDataManager Data;
    private readonly IGameInteropProvider Interop;
    private readonly IPluginLog Log;
    private readonly IChatGui Chat;
    private readonly IGameGui GameGUI;
    private readonly IContextMenu ContextMenu;
    private readonly ICommandManager Commands;
    private readonly IAddonLifecycle AddonLifecycle;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICallGateProvider<string> diagnosticsIpc;
    private readonly Action<bool> saveEnabled;
    private HmsOriginalAdapter? hmsAdapter;
    private ActorPreviewRuntime? runtime;
    private PreviewInteractionService? interaction;
    private bool enabled;
    private bool diagnosticsRegistered;
    private bool initialized;
    private bool visible;
    private bool disposed;
    private string lastMessage = "等待插件加载阶段完成";
    private HmsCoexistenceDecision coexistence = HmsCoexistencePolicy.Decide(0, null);
    private DateTime nextCoexistenceCheck;

    public LocalPreviewFeature(IDalamudPluginInterface pluginInterface, IFramework framework,
        IClientState clientState, IObjectTable objects, ICondition conditions, IDataManager data,
        IGameInteropProvider interop, IPluginLog log, IChatGui chat, IGameGui gameGUI,
        IContextMenu contextMenu, ICommandManager commands, IAddonLifecycle addonLifecycle,
        bool initialEnabled, Action<bool> saveEnabled)
    {
        this.pluginInterface = pluginInterface;
        Framework = framework; ClientState = clientState; Objects = objects; Condition = conditions;
        Data = data; Interop = interop; Log = log; Chat = chat; GameGUI = gameGUI;
        ContextMenu = contextMenu; Commands = commands; AddonLifecycle = addonLifecycle;
        enabled = initialEnabled;
        this.saveEnabled = saveEnabled;
        diagnosticsIpc = pluginInterface.GetIpcProvider<string>("FireGaze.LocalPreview.Diagnostics");
        try
        {
            diagnosticsIpc.RegisterFunc(() => OnFramework(Diagnostics));
            diagnosticsRegistered = true;
            Framework.Update += Update;
            pluginInterface.UiBuilder.Draw += Draw;
            Log.Information("[LocalPreview] FireGaze 本地试用功能正在初始化");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Update(IFramework framework)
    {
        if (disposed) return;
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
            hmsAdapter = new HmsOriginalAdapter(pluginInterface, Commands, runtime, Log);
            RefreshCoexistence();
            interaction = new PreviewInteractionService(ContextMenu, Chat, GameGUI, Objects, Data,
                ClientState, Interop, Log, Request, () => runtime?.CurrentNativeEmote, AddonLifecycle);
            interaction.EnableNativeChatLinks();
            runtime.EmoteObserved += RecordEmote;
            runtime.NativeInputAccepted += OnNativeAccepted;
            runtime.PreviewCancelled += OnCancelled;
            lastMessage = "本地试用入口已就绪";
            if (!enabled) { interaction.Dispose(); interaction = null; }
            Log.Information("[LocalPreview] 初始化完成：" + Diagnostics());
        }
        catch (Exception ex)
        {
            DisposeComponent(interaction, "菜单入口");
            DisposeComponent(runtime, "角色显示");
            DisposeComponent(hmsAdapter, "HMS 适配");
            hmsAdapter = null;
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
        Cleanup(() => saveEnabled(value), "保存开关");
        if (!value)
        {
            Cancel();
            interaction?.Dispose();
            interaction = null;
            lastMessage = "本地试用已关闭";
        }
        else if (runtime is not null)
        {
            interaction = new PreviewInteractionService(ContextMenu, Chat, GameGUI, Objects, Data,
                ClientState, Interop, Log, Request, () => runtime?.CurrentNativeEmote, AddonLifecycle);
            interaction.EnableNativeChatLinks();
            runtime.EmoteObserved -= RecordEmote;
            runtime.EmoteObserved += RecordEmote;
            lastMessage = "本地试用已开启";
        }
    }

    private void RecordEmote(ulong actorID, ushort emoteID) => interaction?.RecordEmote(actorID, emoteID);

    private T OnFramework<T>(Func<T> action) => Framework.IsInFrameworkUpdateThread
        ? action() : Framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();

    private void RefreshCoexistence()
        => coexistence = hmsAdapter?.Refresh() ?? new(false, "兼容适配正在初始化");

    public void OnCommand(string arguments)
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
                if (parts.Length == 2 && ushort.TryParse(parts[1], out var emoteID) && emoteID != 0 &&
                    Data.GetExcelSheet<Lumina.Excel.Sheets.Emote>().TryGetRow(emoteID, out var emote))
                {
                    interaction?.EnableNativeChatProbe();
                    var text = System.Text.Encoding.UTF8.GetBytes($"[本地试用验证] 请右键这整句话：{emote.Name}");
                    Chat.Print(SeString.Parse(NativeEmoteLinkBuilder.Wrap(text, emoteID)));
                }
                else Chat.PrintError("[本地试用] 用法：/firegaze preview sampleemote <有效情感动作编号>");
                break;
            case "sampleitem":
                if (parts.Length == 2 && uint.TryParse(parts[1], out var itemID) && itemID != 0 &&
                    Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().TryGetRow(itemID, out var item))
                    Chat.Print(new SeStringBuilder().AddText("[本地试用验证] ")
                        .AddItemLink(itemID, false, item.Name.ToString()).Build());
                else Chat.PrintError("[本地试用] 用法：/firegaze preview sampleitem <有效物品编号>");
                break;
            case "mount": ParseRequest(MenuKind.Mount, parts); break;
            case "minion": ParseRequest(MenuKind.Minion, parts); break;
            case "accessory": ParseRequest(MenuKind.Accessory, parts); break;
            case "emote": ParseRequest(MenuKind.Emote, parts); break;
            default: Chat.PrintError("[本地试用] 用法：/firegaze preview status|cancel|on|off|mount|minion|accessory|emote <编号>"); break;
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
        version = "1.4.0.33", enabled, initialized, coexistence,
        loggedIn = ClientState.IsLoggedIn,
        territory = ClientState.TerritoryType,
        capabilities = runtime?.Capabilities,
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

    private void Draw()
    {
        if (!visible || disposed) return;
        if (ImGui.Begin("FireGaze 本地试用", ref visible, ImGuiWindowFlags.AlwaysAutoResize))
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
        Cleanup(() => Framework.Update -= Update, "帧回调");
        Cleanup(() => pluginInterface.UiBuilder.Draw -= Draw, "窗口回调");
        if (diagnosticsRegistered) Cleanup(diagnosticsIpc.UnregisterFunc, "诊断 IPC");
        DisposeComponent(interaction, "菜单入口");
        if (runtime is not null)
        {
            runtime.EmoteObserved -= RecordEmote;
            runtime.NativeInputAccepted -= OnNativeAccepted;
            runtime.PreviewCancelled -= OnCancelled;
            DisposeComponent(runtime, "角色显示");
        }
        DisposeComponent(hmsAdapter, "HMS 适配");
        Log.Information("[LocalPreview] FireGaze 本地试用功能已卸载");
    }

    private void DisposeComponent(IDisposable? component, string name)
        => Cleanup(() => component?.Dispose(), name);

    private void Cleanup(Action cleanup, string name)
    {
        try { cleanup(); }
        catch (Exception ex)
        {
            try { Log.Error(ex, "[LocalPreview] {Component} 清理失败", name); }
            catch { /* Continue ownership cleanup even when the host log is unavailable. */ }
        }
    }
}
