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
public sealed partial class LocalPreviewFeature : IDisposable
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
    private readonly Configuration config;
    private readonly Action saveConfig;
    private HmsOriginalAdapter? hmsAdapter;
    private ActorPreviewRuntime? runtime;
    private PreviewInteractionService? interaction;
    private volatile bool enabled;
    private bool diagnosticsRegistered;
    private volatile bool initialized;
    private bool preferencesInitialized;
    private volatile bool disposed;
    private PreviewEffect[] uiActiveEffects = [];
    private string lastMessage = "等待插件加载阶段完成";
    private HmsCoexistenceDecision coexistence = HmsCoexistencePolicy.Decide(0, null);
    private DateTime nextCoexistenceCheck;
    private readonly ITextureProvider? textures;
    private readonly IKeyState? keys;

    public LocalPreviewFeature(IDalamudPluginInterface pluginInterface, IFramework framework,
        IClientState clientState, IObjectTable objects, ICondition conditions, IDataManager data,
        IGameInteropProvider interop, IPluginLog log, IChatGui chat, IGameGui gameGUI,
        IContextMenu contextMenu, ICommandManager commands, IAddonLifecycle addonLifecycle,
        Configuration config, Action saveConfig, ITextureProvider? textures = null, IKeyState? keys = null)
    {
        this.pluginInterface = pluginInterface;
        Framework = framework; ClientState = clientState; Objects = objects; Condition = conditions;
        Data = data; Interop = interop; Log = log; Chat = chat; GameGUI = gameGUI;
        ContextMenu = contextMenu; Commands = commands; AddonLifecycle = addonLifecycle;
        this.config = config;
        preferences = new(config);
        enabled = config.LocalPreviewSettingsRevision != 0 && config.LocalPreviewEnabled;
        this.saveConfig = saveConfig;
        this.textures = textures;
        this.keys = keys;
        diagnosticsIpc = pluginInterface.GetIpcProvider<string>("FireGaze.LocalPreview.Diagnostics");
        try
        {
            diagnosticsIpc.RegisterFunc(() => OnFramework(Diagnostics));
            diagnosticsRegistered = true;
            Framework.Update += Update;
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
        try { UpdateCore(framework); }
        finally { PublishActiveEffects(); }
    }

    private void UpdateCore(IFramework framework)
    {
        if (disposed) return;
        if (!preferencesInitialized)
        {
            preferencesInitialized = true;
            if (Preferences.Migrate())
            {
                enabled = false;
                Cleanup(saveConfig, "保存本地试用默认设置");
            }
        }
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
        if (initialized) { UpdatePresentationExtras(); return; }
        initialized = true;
        try
        {
            runtime = new ActorPreviewRuntime(Framework, ClientState, Objects, Condition, Data, Interop, Log);
            LoadCatalog();
            hmsAdapter = new HmsOriginalAdapter(pluginInterface, Commands, runtime, Log);
            RefreshCoexistence();
            CreateInteraction();
            runtime.EmoteObserved += RecordEmote;
            runtime.NativeInputAccepted += OnNativeAccepted;
            runtime.PreviewCancelled += OnCancelled;
            InitializePresentationExtras();
            lastMessage = "本地试用入口已就绪";
            if (!enabled) { interaction?.Dispose(); interaction = null; }
            Log.Information("[LocalPreview] 初始化完成：" + Diagnostics());
        }
        catch (Exception ex)
        {
            DisposePresentationExtras();
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
        => DispatchMutation(() => RequestCore(kind, id));

    private void RequestCore(MenuKind kind, uint id)
    {
        if (disposed || !enabled || runtime is null)
        {
            Chat.PrintError("[本地试用] 功能尚未就绪或已关闭。");
            return;
        }
        if (!MenuOptions().Allows(kind, false))
        {
            Chat.PrintError("[本地试用] 该分类的试用已关闭。");
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
                lastMessage = $"已开始本地试用{CategoryName(kind)}";
                Log.Information("[LocalPreview] {Message} {Kind} #{Id}", lastMessage, kind, id);
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
        => DispatchMutation(CancelCore);

    private void CancelCore()
    {
        runtime?.ClearAll();
        lastMessage = "全部试用已结束";
    }

    private void SetEnabled(bool value)
        => DispatchMutation(() => SetEnabledCore(value));

    private void SetEnabledCore(bool value)
    {
        if (!value)
        {
            Preferences.CompleteEnable(false);
            enableConfirmationVisible = false;
        }
        if (enabled == value) return;
        enabled = value;
        config.LocalPreviewEnabled = value;
        Cleanup(saveConfig, "保存开关");
        if (!value)
        {
            CancelCore();
            interaction?.Dispose();
            interaction = null;
            lastMessage = "本地试用已关闭";
        }
        else if (runtime is not null)
        {
            CreateInteraction();
            runtime.EmoteObserved -= RecordEmote;
            runtime.EmoteObserved += RecordEmote;
            lastMessage = "本地试用已开启";
        }
    }

    private void RecordEmote(ulong actorID, ushort emoteID) => interaction?.RecordEmote(actorID, emoteID);

    // Render and menu callbacks must never wait for Framework while Framework may
    // be waiting for rendering. Re-check lifetime when queued work actually runs.
    private void DispatchMutation(Action action)
    {
        if (disposed) return;
        void Execute()
        {
            if (disposed) return;
            try { action(); }
            catch (Exception ex) { Log.Error(ex, "[LocalPreview] 操作未能执行"); }
            finally { if (!disposed) PublishActiveEffects(); }
        }
        if (Framework.IsInFrameworkUpdateThread) Execute();
        else _ = Framework.RunOnFrameworkThread(() => { Execute(); return true; });
    }

    private void PublishActiveEffects()
        => System.Threading.Volatile.Write(ref uiActiveEffects,
            disposed ? [] : runtime?.ActiveEffects.ToArray() ?? []);

    private PreviewEffect[] ReadActiveEffects()
        => System.Threading.Volatile.Read(ref uiActiveEffects) ?? [];

    private void CancelSlot(PreviewSlot slot)
        => DispatchMutation(() => runtime?.Clear(slot));

    private T OnFramework<T>(Func<T> action) => Framework.IsInFrameworkUpdateThread
        ? action() : Framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();

    private void RefreshCoexistence()
        => coexistence = hmsAdapter?.Refresh() ?? new(false, "兼容适配正在初始化");

    public void OnCommand(string arguments)
        => DispatchMutation(() => OnCommandCore(arguments));

    private void OnCommandCore(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return; // Main window navigation is owned by Plugin.
        switch (parts[0].ToLowerInvariant())
        {
            case "status": Chat.Print("[本地试用] " + Diagnostics()); break;
            case "cancel": Cancel(); break;
            case "on":
                if (pluginInterface?.IsDev == true) SetEnabled(true);
                else
                {
                    RequestEnabled(true);
                    Chat.Print("[本地试用] 请打开「坐骑试用」页，阅读说明后确认开启。");
                }
                break;
            case "off": SetEnabled(false); break;
            case "option":
                if (pluginInterface?.IsDev != true)
                {
                    Chat.PrintError("[本地试用] 四类试用统一开启，请在「坐骑试用」页设置。");
                    break;
                }
                if (!PreviewOptionCommand.TryParse(parts, out var option))
                {
                    Chat.PrintError("[本地试用] 用法：/firegaze preview option mount|minion|accessory|emote|items on|off");
                    break;
                }
                switch (option.Option)
                {
                    case PreviewOption.Mount: SetCategory(MenuKind.Mount, option.Enabled); break;
                    case PreviewOption.Minion: SetCategory(MenuKind.Minion, option.Enabled); break;
                    case PreviewOption.Accessory: SetCategory(MenuKind.Accessory, option.Enabled); break;
                    case PreviewOption.Emote: SetCategory(MenuKind.Emote, option.Enabled); break;
                    case PreviewOption.Items: SetItemMenus(option.Enabled); break;
                }
                var optionName = option.Option switch
                {
                    PreviewOption.Mount => "坐骑", PreviewOption.Minion => "宠物",
                    PreviewOption.Accessory => "时尚配饰", PreviewOption.Emote => "情感动作",
                    _ => "物品右键试穿",
                };
                Chat.Print($"[本地试用] {optionName}已{(option.Enabled ? "开启" : "关闭")}。");
                break;
            case "chatlinks":
                if (pluginInterface?.IsDev == true && parts.Length == 2 && parts[1] is "on" or "off")
                    SetChatLinks(parts[1] == "on");
                break;
            case "native":
                if (pluginInterface.IsDev) ExecuteDevelopmentNative(parts);
                break;
            case "special":
                if (pluginInterface.IsDev && parts.Length==2 && int.TryParse(parts[1],out var slot)
                    && slot is >=0 and <=TrialSpecialHotbarModel.ExitSlot)
                    OnFramework(() => { PublishSpecialBar(); return specialHotbarModel?.Current is { } bar && ActivateSpecialSlot(slot,bar.Revision); });
                break;
            case "photo":
                if (pluginInterface.IsDev && parts.Length==2 && parts[1] is "enter" or "exit")
                    SetDevelopmentPhoto(parts[1]=="enter");
                break;
            case "chatprobe":
                lastMessage = interaction?.EnableNativeChatProbe() == true
                    ? "情感提示整句点击已开启，请使用原生情感动作并左键点击新提示。"
                    : "聊天探针未能开启，请查看诊断。";
                Chat.Print("[本地试用] " + lastMessage);
                break;
            case "sampleemote":
                if (parts.Length == 2 && ushort.TryParse(parts[1], out var emoteID) && emoteID != 0 &&
                    Data.GetExcelSheet<Lumina.Excel.Sheets.Emote>().TryGetRow(emoteID, out var emote))
                {
                    interaction?.EnableNativeChatProbe();
                    var text = System.Text.Encoding.UTF8.GetBytes($"[本地试用验证] 左键点击这整句话试用：{emote.Name}");
                    var linked = interaction?.WrapEmoteMessage(text, emoteID) ?? [];
                    if (linked.Length != 0) Chat.Print(SeString.Parse(linked));
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
            default: Chat.PrintError("[本地试用] 用法：/firegaze preview status|cancel|on|off|mount|minion|accessory|emote <编号>；option <分类或 items> on|off"); break;
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
        version = typeof(LocalPreviewFeature).Assembly.GetName().Version?.ToString(), enabled, initialized, coexistence, options = MenuOptions(),
        chatLinksPreference = config.LocalPreviewChatLinksEnabled,
        chatLinksEffective = enabled && Preferences.CanLinkEmoteMessages,
        enableConfirmationPending = Preferences.ConfirmationPending,
        catalogCounts = Enum.GetValues<MenuKind>().ToDictionary(k => k.ToString(), k => catalog.Search(k, "").Count),
        loggedIn = ClientState.IsLoggedIn,
        territory = ClientState.TerritoryType,
        capabilities = runtime?.Capabilities,
        timelineObservers = new { play = runtime?.TimelinePlayStatus, action = runtime?.ActionTimelinePlayStatus },
        weaponVisibility = new { installed = runtime?.WeaponVisibilityGatesInstalled,
            regularSuppressed = runtime?.WeaponVisibilitySuppressions, extraSuppressed = runtime?.ExtraWeaponVisibilitySuppressions },
        nativeRequestsConsumed = runtime?.NativeRequestsConsumed,
        hmsVisibilityRepairs = runtime?.HmsVisibilityRepairs,
        specialBar = specialHotbarModel?.Current,
        flightPoseActive = runtime?.FlightPoseActive ?? false,
        activePreviewEmoteId = runtime?.GetActivePreviewEmote()?.EmoteId ?? 0,
        specialKeyboardAvailable = specialHotbarView?.KeyboardAvailable,
        nativeSpecialBar = specialHotbarView is null ? null : new { available=specialHotbarView.NativeAvailable,
            active=specialHotbarView.NativeActive, detail=specialHotbarView.NativeStatus, inputsConsumed=specialHotbarView.NativeInputsConsumed },
        gposeMemory = gposeEmotes is null ? null : new { id=gposeEmotes.CurrentEmoteId, installed=gposeEmotes.Installed, replays=gposeEmotes.ReplayCount, detail=gposeEmotes.Detail },
        interaction = interaction?.Capabilities,
        observedEmotes = interaction?.ObservedEmotes,
        chatMessagesLinked = interaction?.ChatMessagesLinked,
        chatMessagesWithoutExactContext = interaction?.ChatMessagesWithoutExactContext,
        nativeEmoteMenusObserved = interaction?.NativeEmoteMenusObserved,
        nativeEmoteLinkEventsObserved = interaction?.NativeEmoteLinkEventsObserved,
        emoteSentenceClickEnabled = interaction?.Capabilities.DirectEmoteClick,
        nativeLogCaptureEnabled = interaction?.NativeLogCaptureEnabled,
        nativeLogMessagesLinked = interaction?.NativeLogMessagesLinked,
        uniqueEmoteLogTemplates = interaction?.UniqueEmoteLogTemplates,
        ambiguousEmoteLogTemplates = interaction?.AmbiguousEmoteLogTemplates,
        unmatchedNativeLogFormats = interaction?.UnmatchedNativeLogFormats,
        nativeLogCaptureDetail = interaction?.NativeLogCaptureDetail,
        itemMenusResolved = interaction?.ItemMenusResolved,
        lastMenuContext = interaction?.LastMenuContext,
        lastNativeEmoteEvent = interaction?.LastNativeEmoteEvent,
        active = runtime?.ActiveEffects,
        lastMessage,
    });

    public void Dispose()
        => OnFramework(() => { DisposeCore(); return true; });

    private void DisposeCore()
    {
        if (disposed) return;
        disposed = true;
        System.Threading.Volatile.Write(ref uiActiveEffects, []);
        Cleanup(() => Framework.Update -= Update, "帧回调");
        DisposePresentationExtras();
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
