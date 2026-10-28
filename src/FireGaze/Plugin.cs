using System.Diagnostics;
using System.Reflection;
using System.Timers;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FireGaze.Internal.Configuration;
using FireGaze.Lifecycle;
using FireGaze.RepoAudit;
using FireGaze.Translate;
using FireGaze.UI;
using Timer = System.Timers.Timer;

namespace FireGaze;

/// <summary>
///     FireGaze —— 卫月一体化工具箱：
///       ① 插件简介汉化（名字 / 一行简介 / 详情 × 原版 / 中文 / 双语）
///       ② 第三方仓库体检（扫描死链、内容不合规，支持停用 / 删除 + 备份 + 撤回）
///       ③ 拦住插件安装器的自动刷新（并可选地记住列表浏览位置）
/// </summary>
public sealed partial class Plugin : IAsyncDalamudPlugin
{
    // 卸载时置空（Dispose 末段）：热重载时旧实例不再被静态字段钉住。读取方一律走可空。
    private static Plugin? instance;

    [PluginService] public static IPluginLog Log { get; private set; } = null!;

    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService] public static IChatGui Chat { get; private set; } = null!;

    [PluginService] public static IFramework Framework { get; private set; } = null!;

    /// <summary>
    ///     纹理服务（图标落盘缓存 → 纹理用；卫月注入）。
    /// </summary>
    [PluginService] public static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] public static ITextureReadbackProvider TextureReadback { get; private set; } = null!;

    /// <summary>
    ///     游戏数据（「插件汉化」的 FF14 术语表读 Lumina 表用；不联网）。
    /// </summary>
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;

    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IObjectTable Objects { get; private set; } = null!;
    [PluginService] public static ICondition Conditions { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider Interop { get; private set; } = null!;
    [PluginService] public static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] public static IGameGui GameGUI { get; private set; } = null!;
    [PluginService] public static INamePlateGui NamePlateGUI { get; private set; } = null!;
    [PluginService] public static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] public static IAgentLifecycle AgentLifecycle { get; private set; } = null!;
    [PluginService] public static IKeyState Keys { get; private set; } = null!;
    [PluginService] public static IUnlockState UnlockState { get; private set; } = null!;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly WindowSystem windowSystem = new("FireGaze");
    private MainWindow window = null!;
    private UI.UITextEditorWindow uiTextEditorWindow = null!;
    private UI.UITextSettingsWindow uiTextSettingsWindow = null!;
    private UI.QuotaNoticeWindow quotaNoticeWindow = null!;
    private UI.UITextTab uiTextTab = null!;
    private UIText.UITextRunLock uiTextRunLock = null!;
    private UIText.UITextPatchManager uiTextPatchManager = null!;
    private Timer translateTimer = null!;

    private readonly object saveLock = new();
    private DateTime lastSave = DateTime.MinValue;

    private int tableUpdateBusy;
    private readonly UI.InstallerListScroll installerListScroll = new();
    private readonly UI.InstallerIconIndexRefresh iconWarmUpIndex = new();
    private bool startupInitDone;
    private LocalPreview.LocalPreviewFeature? localPreview;
    private PortraitPreview.PortraitPreviewFeature? portraitPreview;
    private DateTime loadedAt;
    private readonly HashSet<string> registeredCommands = new(StringComparer.Ordinal);
    private readonly PluginWorkLifetime workLifetime = new();
    private readonly List<Exception> detachErrors = new();
    private IUiBuilder? subscribedUIBuilder;
    private bool drawWindowsRegistered;
    private bool installerTickRegistered;
    private bool openConfigUIRegistered;
    private bool openMainUIRegistered;
    private bool activePluginsChangedRegistered;
    private bool startupTickRegistered;
    private Task? portraitManagedDrain;
    private Task? portraitPhysicalDrain;
    private Task? localPreviewPhysicalDrain;
    private bool nativeInitializationUnknown;
    private int disposed;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        instance = this;
        this.pluginInterface = pluginInterface;
    }

    private void InitializeCore()
    {

        // 全新安装（配置文件还不存在）才标记额度公告资格；升级上来的用户不看新用户公告
        var freshInstall = pluginInterface.ConfigFile?.Exists != true;
        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (freshInstall)
        {
            Config.QuotaNoticeForNewUser = true;
        }

        // 配置迁移走单步迁移器（Internal/Configuration），缺台阶直接抛错；只在真的迁移过时保存一次
        var previousConfigVersion = ConfigurationMigrator.Migrate(Config);
        if (previousConfigVersion < ConfigurationMigrator.LatestVersion || freshInstall)
        {
            pluginInterface.SavePluginConfig(Config);
        }

        ConfigDirectory = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(ConfigDirectory);

        // 诊断日志：内存环形缓冲 + 后台落盘（绝不在渲染线程写文件）；同步一份到卫月日志
        Diagnostics.ActivityLog.Initialize(ConfigDirectory, (level, line) =>
        {
            switch (level)
            {
                case Diagnostics.ActivityLevel.Trace:
                    Log?.Verbose(line);
                    break;
                case Diagnostics.ActivityLevel.Debug:
                    Log?.Debug(line);
                    break;
                case Diagnostics.ActivityLevel.Warning:
                    Log?.Warning(line);
                    break;
                case Diagnostics.ActivityLevel.Error:
                    Log?.Error(line);
                    break;
                case Diagnostics.ActivityLevel.Critical:
                    Log?.Fatal(line);
                    break;
                default:
                    Log?.Information(line);
                    break;
            }
        });
        Diagnostics.ActivityLog.Info("启动", $"FireGaze v{typeof(Plugin).Assembly.GetName().Version} 已加载；配置目录 {ConfigDirectory}");

        Icons = new UI.IconStore(ConfigDirectory, () => Config.IconCacheEnabled);

        Repos = new DalamudRepos(Path.Combine(ConfigDirectory, "backups"));

        var pluginDirectory = pluginInterface.AssemblyLocation.Directory?.FullName
                              ?? Path.GetDirectoryName(pluginInterface.AssemblyLocation.FullName)
                              ?? ".";
        UIText.FFXIVGlossary.PluginDirectory = pluginDirectory;
        UIText.UITextRules.PluginDirectory = pluginDirectory;
        UIText.UITextRules.WarningSink = (e, m) => Log.Warning(e, m);
        UIText.UITextRules.DebugSink = (e, m) => Log.Debug(e, m);
        Table = new TranslationTable(ConfigDirectory, pluginDirectory);
        Contributions = new ContributionsStore(ConfigDirectory);
        DurableDataDirectory = UIText.UITextDataRoot.Resolve(ConfigDirectory);
        TextPacks = new UIText.UITextStore(ConfigDirectory, UIText.UITextDataRoot.PackMirror(DurableDataDirectory));
        TextLibrary = new UIText.UITextLibrary(this);
        uiTextPatchManager = new UIText.UITextPatchManager(this);
        Patcher = new ManifestPatcher(() => Config, Table, m => Log.Warning("[FireGaze] " + m));
        PluginLogFallback.Sink = m => Log.Warning("[FireGaze] " + m);

        uiTextRunLock = new UIText.UITextRunLock();
        uiTextEditorWindow = new UI.UITextEditorWindow(this, TextPacks, uiTextPatchManager, uiTextRunLock);
        windowSystem.AddWindow(uiTextEditorWindow);
        uiTextSettingsWindow = new UI.UITextSettingsWindow(this);
        windowSystem.AddWindow(uiTextSettingsWindow);
        quotaNoticeWindow = new UI.QuotaNoticeWindow(this);
        windowSystem.AddWindow(quotaNoticeWindow);
        window = new MainWindow(this, uiTextTab = new UI.UITextTab(this, uiTextEditorWindow, uiTextSettingsWindow, TextPacks, uiTextPatchManager, uiTextRunLock));
        windowSystem.AddWindow(window);
        subscribedUIBuilder = pluginInterface.UiBuilder;
        subscribedUIBuilder.Draw += DrawWindows;
        drawWindowsRegistered = true;
        subscribedUIBuilder.Draw += TickInstallerListScroll;
        installerTickRegistered = true;
        subscribedUIBuilder.OpenConfigUi += ToggleWindow;
        openConfigUIRegistered = true;
        subscribedUIBuilder.OpenMainUi += OpenMainWindow;
        openMainUIRegistered = true;

        // 插件装/卸/启/停（公开事件，无钩子）：让「插件汉化」页的行状态立即跟上——
        // 用户在卫月安装器里手动启用后，不用等定期的 30 秒兜底
        pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        activePluginsChangedRegistered = true;

        // 重活（词表、挂钩、定时器）一律延后到「所有插件加载完」之后：
        // 加壳插件的模块初始化器会在加载阶段扫描/改写进程内存，我们需要在那个窗口里保持安静。
        loadedAt = DateTime.UtcNow;
        Framework.Update += OnStartupTick;
        startupTickRegistered = true;

        Log.Information($"[FireGaze] 已加载 v{typeof(Plugin).Assembly.GetName().Version}（初始化将等插件加载阶段结束后进行）");
        if (UIText.UITextDataRoot.IsDedicated(DurableDataDirectory, ConfigDirectory))
        {
            Log.Information($"[FireGaze] 持久数据目录：{DurableDataDirectory}（补丁原始备份与译文包镜像；重置插件配置不会丢）");
        }
        else
        {
            Log.Warning($"[FireGaze] 解析不出启动器根目录，持久数据退回配置目录：{ConfigDirectory}");
        }

        AddCommand(
            "/firegaze",
            new CommandInfo(OnCommand)
            {
                HelpMessage = "打开 FireGaze 窗口",
            });
        AddCommand(
            "/nar",
            new CommandInfo((_, _) => ToggleWindow())
            {
                HelpMessage = "（旧命令）打开 FireGaze 窗口",
                ShowInHelp = false,
            });
        AddCommand(
            "/pdz",
            new CommandInfo((_, _) =>
            {
                ReloadTranslationTable(out _);
                Patcher.ApplyAll();
                Chat.Print("[FireGaze] 已重新加载词表并应用");
            })
            {
                HelpMessage = "（旧命令）重新加载简介汉化词表并应用",
                ShowInHelp = false,
            });

        translateTimer = new Timer(10_000) { AutoReset = true };
        translateTimer.Elapsed += (_, _) =>
        {
            if (!workLifetime.TryAcquire(out var lease, out _)) return;
            using (lease)
            {
                lease!.TryExecute(() =>
                {
                    // Error reporting also uses plugin services and belongs
                    // inside the operation that the host drains on close.
                    try
                    {
                        ApplyTranslationsQuiet();
                        if (lease.IsCurrent) MaybeAutoUpdateTable();
                    }
                    catch (Exception e)
                    {
                        Log.Warning(e, "[FireGaze] 定时任务出错（已拦下，不影响游戏）");
                    }
                });
            }
        };
    }

    /// <summary>
    ///     延迟初始化：等所有插件都不在加载中（或超时 30 秒）再动手。
    ///     加壳插件（.NET Reactor 系）在模块初始化器里会扫描/改写进程内存，
    ///     我们在这个窗口里不做任何重活、不开定时器，避免把它们搞崩。
    /// </summary>
    private void OnStartupTick(IFramework framework)
    {
        if (!workLifetime.TryAcquire(out var lease, out _)) return;
        using (lease) lease!.TryExecute(() => OnStartupTickCore(framework));
    }

    private void OnStartupTickCore(IFramework framework)
    {
        if (startupInitDone)
        {
            return;
        }

        if (AnyPluginLoading() && DateTime.UtcNow - loadedAt < TimeSpan.FromSeconds(30))
        {
            return;
        }

        startupInitDone = true;
        Framework.Update -= OnStartupTick;

        try
        {
            ReloadTranslationTable(out _);
            TrackFirstSeen();

            // 补丁复核：启动时按已记录的加载回执重试确认；未确认的保留恢复凭据
            uiTextPatchManager.VerifyOnStartup();

            translateTimer.Start();

            Log.Information(
                $"[FireGaze] 初始化完成（插件加载阶段已结束）：词表 {Table.Count} 条；" +
                $"汉化 = {(Config.TranslateEnabled ? "开" : "关")}");
            Log.Information("[FireGaze] 页签顺序：插件汉化 / 插件发现 / 仓库体检 / 简介汉化 / 插件安装器 / 坐骑试用 / 肖像同步预览");
        }
        catch (Exception e)
        {
            Log.Error(e, "[FireGaze] 延迟初始化失败");
        }

        if (workLifetime.State == PluginWorkLifetimeState.Running) InitializePreviewFeatures();
    }

    private void InitializePreviewFeatures()
    {
        if (nativeInitializationUnknown) return;
        try
        {
            localPreview = new LocalPreview.LocalPreviewFeature(pluginInterface, Framework, ClientState,
                Objects, Conditions, DataManager, Interop, Log, Chat, GameGUI, ContextMenu,
                CommandManager, AddonLifecycle, Config, () => SaveConfig(force: true), Textures, Keys, NamePlateGUI);
        }
        catch (Exception e)
        {
            ObserveNativeStartupFailure(e);
            Log.Error(e, "[FireGaze] 本地试用初始化失败");
        }
        if (nativeInitializationUnknown) return;
        try
        {
            portraitPreview = new PortraitPreview.PortraitPreviewFeature(pluginInterface, ConfigDirectory,
                () => localPreview?.ReadTrialSafetyState() ?? new(false, false, 0));
        }
        catch (Exception e)
        {
            ObserveNativeStartupFailure(e);
            Log.Error(e, "[FireGaze] 肖像同步预览初始化失败");
        }
        if (localPreview is not null && portraitPreview is not null && !Config.PreviewFeaturesAnnounced)
            Framework.Update += AnnouncePreviewFeatures;
    }

    private bool ObserveNativeStartupFailure(Exception error)
    {
        if (nativeInitializationUnknown) return true;
        var pending = new Stack<Exception>();
        var observed = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var owners = new HashSet<IDisposable>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        while (pending.TryPop(out var current))
        {
            if (!observed.Add(current)) continue;
            if (current is LocalPreview.Runtime.NativePhysicalCloseException native) owners.Add(native.Owner);
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        if (owners.Count == 0) return false;
        nativeInitializationUnknown = true;
        // A Feature can already own delayed, known retirement work when its constructor
        // fails. Preserve those real drains; only a raw native owner has no completion
        // signal. No constructor rollback is retried here.
        var unknownOwners = new List<IDisposable>();
        foreach (var owner in owners)
        {
            if (owner is PortraitPreview.PortraitPreviewFeature portrait)
            {
                portraitManagedDrain = PreservePhysicalDrain(this, portraitManagedDrain, portrait.DrainManagedAsync());
                portraitPhysicalDrain = PreservePhysicalDrain(this, portraitPhysicalDrain, portrait.DrainPhysicalAsync());
            }
            else if (owner is LocalPreview.LocalPreviewFeature local)
                localPreviewPhysicalDrain = PreservePhysicalDrain(this, localPreviewPhysicalDrain, local.DrainPhysicalAsync());
            else unknownOwners.Add(owner);
        }
        if (unknownOwners.Count != 0)
        {
            var incomplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startupDrain = PortraitPreview.PortraitCaptureRetirement.CombineOwned(
                new object[] { this, unknownOwners.ToArray() }, incomplete.Task);
            portraitPhysicalDrain = PreservePhysicalDrain(this, portraitPhysicalDrain, startupDrain);
        }
        var localToDrain = localPreview;
        try { localToDrain?.Dispose(); }
        catch (Exception failure) { Log.Error(failure, "[FireGaze] 原生初始化失败后的本地试用清理未完成"); }
        finally
        {
            localPreviewPhysicalDrain = PreservePhysicalDrain(this, localPreviewPhysicalDrain, localToDrain?.DrainPhysicalAsync());
            localPreview = null;
        }
        return true;
    }

    private static Task? PreservePhysicalDrain(object owner, Task? previous, Task? current)
        => previous is null ? current
            : current is null || ReferenceEquals(previous, current) ? previous
            : PortraitPreview.PortraitCaptureRetirement.CombineOwned(owner, previous, current);

    private void AnnouncePreviewFeatures(IFramework framework)
    {
        if (!workLifetime.TryAcquire(out var lease, out _)) return;
        using (lease)
        {
            lease!.TryExecute(() =>
            {
                try
                {
                    Internal.PreviewFeatureNotice.TryAnnounce(ClientState.IsLoggedIn,
                        localPreview is not null && portraitPreview is not null, Config,
                        message => Chat.Print(new XivChatEntry { Type = XivChatType.Echo, Message = message }),
                        () => SaveConfig(force: true));
                    if (Config.PreviewFeaturesAnnounced) Framework.Update -= AnnouncePreviewFeatures;
                }
                catch (Exception error)
                {
                    Framework.Update -= AnnouncePreviewFeatures;
                    Log.Warning(error, "[FireGaze] 新功能提示暂时未能完成");
                }
            });
        }
    }

    /// <summary>
    ///     是否还有插件正在加载（反射读 PluginState == Loading）。
    /// </summary>
    private bool AnyPluginLoading()
    {
        try
        {
            var manager = ResolveService("Dalamud.Plugin.Internal.PluginManager");
            var prop = manager?.GetType().GetProperty("InstalledPlugins", BindingFlags.Public | BindingFlags.Instance);
            if (prop?.GetValue(manager) is not System.Collections.IEnumerable plugins)
            {
                return false;
            }

            foreach (var plugin in plugins)
            {
                var state = plugin?.GetType().GetProperty("State", BindingFlags.Public | BindingFlags.Instance)?.GetValue(plugin);
                if (state?.ToString() == "Loading")
                {
                    return true;
                }
            }
        }
        catch
        {
            // 读不到就当没有，靠超时兜底
        }

        return false;
    }

    /// <summary>
    ///     当前配置。
    /// </summary>
    public Configuration Config { get; private set; } = null!;

    /// <summary>
    ///     本地待提交的翻译贡献（配置目录，不联网）。
    /// </summary>
    internal ContributionsStore Contributions { get; private set; } = null!;

    /// <summary>
    ///     插件配置目录（备份写在这里的 backups/ 下）。
    /// </summary>
    public string ConfigDirectory { get; private set; } = string.Empty;

    /// <summary>
    ///     图标落盘缓存（体检页 + 插件安装器共用）。
    /// </summary>
    internal UI.IconStore Icons { get; private set; } = null!;

    /// <summary>
    ///     诊断用：与 <see cref="ConfigDirectory"/> 相同（dalamudUI.ini 就在它上两级）。
    /// </summary>
    public static string ConfigDirectoryForDiagnostics => instance?.ConfigDirectory ?? "(插件已卸载)";

    /// <summary>
    ///     翻译词表。
    /// </summary>
    public TranslationTable Table { get; private set; } = null!;

    /// <summary>
    ///     清单改写器。
    /// </summary>
    public ManifestPatcher Patcher { get; private set; } = null!;

    /// <summary>
    ///     卫月仓库配置读写。
    /// </summary>
    public DalamudRepos Repos { get; private set; } = null!;

    /// <summary>
    ///     插件内部文本的本地包存放（「插件汉化」页签用）。
    /// </summary>
    internal UIText.UITextStore TextPacks { get; private set; } = null!;

    /// <summary>
    ///     持久数据目录（默认 <c>&lt;启动器根&gt;\FireGazeData\</c>）：补丁原始备份与译文包镜像放这里，
    ///     活过「重置插件配置」（2026-10-02 全量落地）。解析不出来时等于配置目录，退回旧行为。
    /// </summary>
    internal string DurableDataDirectory { get; private set; } = string.Empty;

    /// <summary>
    ///     公共译文库下载端（按需拉取现成译文；数据只作加速，补丁仍走本地）。
    /// </summary>
    internal UIText.UITextLibrary TextLibrary { get; private set; } = null!;

    /// <summary>
    ///     插件内部文本的补丁调度（打补丁 / 还原 / 重载 / 更新后重打）。
    /// </summary>
    internal UIText.UITextPatchManager TextPatches => uiTextPatchManager;

    /// <summary>
    ///     最近一次汉化应用改写的清单数。
    /// </summary>
    public int LastTranslatedCount { get; private set; }

    public string Name => "FireGaze";

    /// <summary>
    ///     The loader's asynchronous contract is the single admission point for ordinary
    ///     plugin work. Initialization touches Framework-bound services, so it is admitted
    ///     there even when the asynchronous loader invokes this method on a worker thread.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Framework.RunOnFrameworkThread(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                workLifetime.ShutdownToken.ThrowIfCancellationRequested();
                InitializeCore();
                if (!workLifetime.TryStartRunning(out var reason))
                    throw new InvalidOperationException(reason);
            }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------ 生命周期

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var previewToDrain = portraitPreview;
        try
        {
            previewToDrain?.Dispose();
        }
        catch (Exception e)
        {
            Log.Error(e, "[FireGaze] 肖像同步预览清理失败");
        }
        finally
        {
            // An exceptional Framework release must not let the host skip still-owned
            // provider work. An unsealed feature deliberately reports an incomplete drain.
            portraitManagedDrain = PreservePhysicalDrain(this, portraitManagedDrain, previewToDrain?.DrainManagedAsync());
            portraitPhysicalDrain = PreservePhysicalDrain(this, portraitPhysicalDrain, previewToDrain?.DrainPhysicalAsync());
            portraitPreview = null;
        }
        var localToDrain = localPreview;
        try
        {
            localToDrain?.Dispose();
        }
        catch (Exception e)
        {
            Log.Error(e, "[FireGaze] 本地试用清理失败");
        }
        finally
        {
            localPreviewPhysicalDrain = PreservePhysicalDrain(this, localPreviewPhysicalDrain, localToDrain?.DrainPhysicalAsync());
            localPreview = null;
        }

        uiTextTab?.StopWorkflows();
        // A clean host shutdown does not prove which plugin bytes were loaded.
        // Keep pending verification for the identity-bound load receipt.

        if (startupTickRegistered)
            TryDetach("Framework.Update / OnStartupTick", () => { Framework.Update -= OnStartupTick; startupTickRegistered = false; });
        TryDetach("Framework.Update / AnnouncePreviewFeatures", () => Framework.Update -= AnnouncePreviewFeatures);

        TryDetach("translation timer stop", () => translateTimer?.Stop());
        TryDetach("translation timer dispose", () => translateTimer?.Dispose());

        // Attempt each host callback removal separately: one broken event accessor must not
        // leave later delegates attached without even attempting their removal.
        if (drawWindowsRegistered)
            TryDetach("UIBuilder.Draw / DrawWindows", () => { subscribedUIBuilder!.Draw -= DrawWindows; drawWindowsRegistered = false; });
        if (installerTickRegistered)
            TryDetach("UIBuilder.Draw / TickInstallerListScroll", () => { subscribedUIBuilder!.Draw -= TickInstallerListScroll; installerTickRegistered = false; });
        if (openConfigUIRegistered)
            TryDetach("UIBuilder.OpenConfigUi", () => { subscribedUIBuilder!.OpenConfigUi -= ToggleWindow; openConfigUIRegistered = false; });
        if (openMainUIRegistered)
            TryDetach("UIBuilder.OpenMainUi", () => { subscribedUIBuilder!.OpenMainUi -= OpenMainWindow; openMainUIRegistered = false; });
        if (activePluginsChangedRegistered)
            TryDetach("ActivePluginsChanged", () => { pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged; activePluginsChangedRegistered = false; });
        TryDetach("WindowSystem detach", () => window?.Detach());
        foreach (var command in registeredCommands)
        {
            TryDetach($"command {command}", () => CommandManager.RemoveHandler(command));
        }

        if (detachErrors.Count != 0)
            throw new AggregateException("FireGaze 无法确认所有宿主回调均已解绑。", detachErrors.ToArray());
    }

    private void TryDetach(string owner, Action detach)
    {
        try { detach(); }
        catch (Exception error)
        {
            var recorded = new InvalidOperationException($"Failed to detach {owner}.", error);
            detachErrors.Add(recorded);
            try { Log.Warning(recorded, "[FireGaze] 卸载时解绑失败；异步关闭将报告此错误"); }
            catch { }
        }
    }

    /// <summary>
    ///     Asynchronous host cleanup. The existing Framework-bound release is run as the
    ///     begin-close phase; log/timer roots are drained only after it returns.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var result = await workLifetime.CloseAsync(
            beginClose: BeginDisposeAsync,
            cleanupAfterDrain: FinishDisposeAsync).ConfigureAwait(false);
        if (!result.Completed)
            throw new AggregateException("FireGaze 异步清理未完成。",
                new[] { result.BeginCloseError, result.CleanupError }.OfType<Exception>());
    }

    private async ValueTask FinishDisposeAsync()
    {
        var errors = new List<Exception>();
        try { uiTextEditorWindow?.FlushForHostClose(); }
        catch (Exception error) { errors.Add(error); }
        // SaveConfig deliberately catches ordinary runtime errors. Final host
        // persistence must report failure while still releasing drained roots.
        try { if (Config is not null) pluginInterface.SavePluginConfig(Config); }
        catch (Exception error) { errors.Add(error); }
        try { Icons?.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        if (ReferenceEquals(instance, this)) instance = null;
        try
        {
            if (!await Diagnostics.ActivityLog.ShutdownAsync().ConfigureAwait(false))
                throw new InvalidOperationException("诊断日志未能完成最终排空。");
        }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("最终保存或资源收尾失败。", errors);
    }

    private async ValueTask BeginDisposeAsync()
    {
        Exception? beginError = null;
        try
        {
            if (Framework is null || Framework.IsInFrameworkUpdateThread) Dispose();
            else await Framework.RunOnFrameworkThread(Dispose).ConfigureAwait(false);
        }
        catch (Exception error) { beginError = error; }

        // The host disposes its texture-provider scope after DisposeAsync returns. Logical
        // cancellation is insufficient: hold that scope through physical completion and late
        // resource release. When GPU work is stuck this remains visibly pending, never a
        // successful unload. A file failure must not bypass another unfinished physical task.
        try
        {
            await Task.WhenAll(portraitManagedDrain ?? Task.CompletedTask,
                portraitPhysicalDrain ?? Task.CompletedTask,
                localPreviewPhysicalDrain ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (beginError is not null) throw new AggregateException(beginError, error);
            throw;
        }
        if (beginError is not null) throw beginError;
    }

    /// <summary>
    ///     注册聊天命令。
    /// </summary>
    /// <remarks>
    ///     口径：本插件只注册 <c>/firegaze</c>（外加 <c>/nar</c>、<c>/pdz</c> 两个旧别名）。
    ///     <c>/fg</c> 属于别的插件、**不注册**，一切面向玩家的文案也只写 <c>/firegaze</c>。
    ///     命令名被占用时只记一条警告，不能让插件加载失败。
    /// </remarks>
    private void AddCommand(string name, CommandInfo info)
    {
        try
        {
            CommandManager.AddHandler(name, info);
            registeredCommands.Add(name);
        }
        catch (Exception e)
        {
            Log.Warning($"[FireGaze] 命令 {name} 注册失败（可能已被其他插件占用）：{e.Message}");
        }
    }

    // ------------------------------------------------------------------ 窗口 / 命令

    /// <summary>
    ///     切换主窗口。
    /// </summary>
    // The style must exist before WindowSystem begins any window, including its popup/tooltip children.
    // This callback owns only FireGaze windows; disposal restores the host before the next plugin draws.
    private void DrawWindows()
    {
        if (!this.workLifetime.TryAcquire(out var lease, out _)) return;
        using (lease)
            lease!.TryExecute(() => FireGazeTheme.Draw(windowSystem.Draw));
    }

    public void ToggleWindow()
    {
        var wasOpen = window.IsOpen;
        window.Toggle();
        if (!wasOpen) UI.MainWindowOpenPolicy.ExpandForExplicitOpen(window);
    }

    /// <summary>
    ///     卫月说插件列表变了（装/卸/启/停）：转给「插件汉化」页重建索引。
    ///     事件可能在任意线程；页签只置一个标记，绘制线程下一帧处理。
    /// </summary>
    private void OnActivePluginsChanged(Dalamud.Plugin.IActivePluginsChangedEventArgs args)
    {
        if (!this.workLifetime.TryAcquire(out var lease, out _)) return;
        using (lease)
            lease!.TryExecute(() =>
            {
                iconWarmUpIndex.NotifyChanged();
                try
                {
                    uiTextTab.NotifyInstalledPluginsChanged();
                }
                catch (Exception e)
                {
                    Log.Warning(e, "[FireGaze] 处理插件状态变化失败");
                }
            });
    }

    /// <summary>
    ///     打开主窗口（供卫月插件安装器的「主界面」入口调用；不切换页签）。
    /// </summary>
    public void OpenMainWindow()
    {
        window.IsOpen = true;
        UI.MainWindowOpenPolicy.ExpandForExplicitOpen(window);
        window.BringToFront();
    }

    /// <summary>
    ///     打开主窗口并跳到指定页。
    /// </summary>
    public void OpenWindow(MainTab tab)
    {
        window.SelectTab(tab);
        window.IsOpen = true;
        UI.MainWindowOpenPolicy.ExpandForExplicitOpen(window);
        window.BringToFront();
    }

    internal void OpenTranslationFor(string internalName)
    {
        uiTextTab.FocusPlugin(internalName);
        OpenWindow(MainTab.UIText);
    }

    internal void DrawLocalPreview()
    {
        if (localPreview is null)
        {
            ImGui.TextWrapped("本地试用功能正在初始化，请稍后再试。");
            return;
        }
        using var trialTheme = UI.FireGazeTheme.PushPortrait();
        localPreview.DrawContent();
    }

    internal void DrawPortraitEditor()
    {
        if (portraitPreview is null)
        {
            ImGui.TextWrapped("肖像同步预览功能正在初始化，请稍后再试。");
            return;
        }
        portraitPreview.DrawContent();
    }

    /// <summary>
    ///     打开「插件发现」页签（原「参与翻译」独立窗口已并入主窗口第五页签）。
    /// </summary>
    public void OpenContributeWindow()
    {
        window.SelectTab(UI.MainTab.Discovery);
        window.IsOpen = true;
        UI.MainWindowOpenPolicy.ExpandForExplicitOpen(window);
        window.BringToFront();
    }

    private void OnCommand(string command, string args)
    {
        if (!workLifetime.TryAcquire(out var lease, out _)) return;
        using (lease)
            lease!.TryExecute(() => OnCommandCore(command, args));
    }

    private void OnCommandCore(string command, string args)
    {
        var trimmed = args.Trim();
        if (trimmed.Equals("portrait", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("portrait ", StringComparison.OrdinalIgnoreCase))
        {
            if (portraitPreview is null) Chat.PrintError("[肖像同步预览] 功能正在初始化，请稍后再试。");
            else portraitPreview.OnCommand(trimmed["portrait".Length..].TrimStart());
            return;
        }
        if (trimmed.Equals("preview", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("preview ", StringComparison.OrdinalIgnoreCase))
        {
            var previewArguments = trimmed["preview".Length..].TrimStart();
            if (previewArguments.Length == 0) OpenWindow(MainTab.LocalPreview);
            else if (localPreview is null) Chat.PrintError("[本地试用] 功能正在初始化，请稍后再试。");
            else localPreview.OnCommand(previewArguments);
            return;
        }

        var arg = trimmed.ToLowerInvariant();

        switch (arg)
        {
            case "":
                ToggleWindow();
                break;
            case "zh":
                var count = Patcher.ApplyAll();
                LastTranslatedCount = count;
                Chat.Print($"[FireGaze] 已重新应用简介汉化（改写 {count} 条）");
                break;
            case "update":
                _ = UpdateTranslationTableAsync();
                Chat.Print("[FireGaze] 正在从 GitHub 更新词表…");
                break;
            case "translate":
                OpenContributeWindow();
                break;
            default:
                Chat.Print("[FireGaze] 用法：/firegaze [zh|update|translate|preview]");
                break;
        }
    }

    // ------------------------------------------------------------------ 安装器列表浏览位置

    /// <summary>
    ///     开关：是否记住列表浏览位置（关掉时把已记住的位置一并清掉，等于「忘掉」）。
    /// </summary>
    public void SetRememberListScroll(bool remember)
    {
        Config.RememberListScroll = remember;
        if (!remember)
        {
            Config.ListScrollY = null;
        }

        SaveConfig();
    }

    /// <summary>
    ///     设置页要用的安装器功能状态（只读）。
    /// </summary>
    internal UI.InstallerListScroll InstallerFeatures => installerListScroll;

    /// <summary>一次性额度公告窗（首次打开主窗口且未确认时弹出）。</summary>
    internal UI.QuotaNoticeWindow QuotaNotice => quotaNoticeWindow;

    /// <summary>
    ///     用户点了额度公告的「知道了」：立即落盘——
    ///     用户可能开着 DailyRoutines 的即刻登出，等不到游戏正常退出时保存。
    /// </summary>
    public void ConfirmTranslateQuotaNotice()
    {
        Config.TranslateQuotaNoticeConfirmed = true;
        SaveConfig(force: true);
    }

    /// <summary>
    ///     卫月插件接口（仓库体检页订阅「插件列表变化」用）。
    /// </summary>
    internal IDalamudPluginInterface PluginInterface => pluginInterface;

    /// <summary>
    ///     开关：是否拦住插件安装器的自动刷新。
    /// </summary>
    public void SetBlockInstallerAutoRefresh(bool block)
    {
        Config.BlockInstallerAutoRefresh = block;
        SaveConfig();
    }

    /// <summary>
    ///     每帧看一眼插件安装器（记住滚动位置 / 拦住自动刷新 / 图标预热；不用钩子）。
    /// </summary>
    private void TickInstallerListScroll()
    {
        if (!this.workLifetime.TryAcquire(out var lease, out _)) return;
        using (lease)
            lease!.TryExecute(this.TickInstallerListScrollCore);
    }

    private void TickInstallerListScrollCore()
    {
        try
        {
            installerListScroll.Tick(Config, () => SaveConfig(force: false));
            uiTextPatchManager.Tick();
            uiTextTab.TickWorkflows();

            // 安装器开着 → 把本地缓存的图标分批挂回卫月的图标缓存（安装器直接用本地图，不重新下载）
            if (installerListScroll.IsOpen)
            {
                EnsureIconWarmUpIndex();
                Icons.WarmUpStep(4);
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "[FireGaze] 安装器列表这一帧出错（已拦下，不影响游戏）");
        }
    }

    /// <summary>
    ///     首次打开安装器和已装插件变化后后台重建索引；读不到时过 10 秒再试。
    /// </summary>
    private void EnsureIconWarmUpIndex()
    {
        iconWarmUpIndex.Tick(DateTime.UtcNow, QueueIconWarmUpIndex, Icons.ScheduleWarmUp);
    }

    private Task<RepoAudit.InstalledPluginsIndex>? QueueIconWarmUpIndex()
    {
        var completion = new TaskCompletionSource<RepoAudit.InstalledPluginsIndex>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!workLifetime.Queue(_ =>
            {
                try { completion.TrySetResult(RepoAudit.InstalledPluginsIndex.Build()); }
                catch (Exception error) { completion.TrySetException(error); }
                return ValueTask.CompletedTask;
            }, out _))
            return null;
        return completion.Task;
    }

    // ------------------------------------------------------------------ 配置

    /// <summary>
    ///     保存插件配置。
    /// </summary>
    public void SaveConfig(bool force = true)
    {
        lock (saveLock)
        {
            if (!force && DateTime.UtcNow - lastSave < TimeSpan.FromSeconds(30))
            {
                return;
            }

            lastSave = DateTime.UtcNow;
        }

        try
        {
            pluginInterface.SavePluginConfig(Config);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[FireGaze] 保存配置失败");
        }
    }

    // ------------------------------------------------------------------ 安装器钩子

    // ------------------------------------------------------------------ 简介汉化

    /// <summary>
    ///     重新加载词表。
    /// </summary>
    public bool ReloadTranslationTable(out string? error) => Table.Load(out error);

    /// <summary>
    ///     把词表拷一份给后台线程用（翻译搜索索引在别的线程上读）。
    ///     宿主线程可能同时在改词表，所以这里做一次带锁拷贝。
    /// </summary>
    public Dictionary<string, TransEntry> SnapshotTable()
    {
        var copy = new Dictionary<string, TransEntry>(StringComparer.Ordinal);
        foreach (var (key, value) in Table.Enumerate())
        {
            copy[key] = value;
        }

        return copy;
    }

    /// <summary>
    ///     玩家刚提交了译文：把「待复核」标记清掉，并立刻重新应用一遍。
    /// </summary>
    public void MarkTranslationReview(string internalName, string field)
    {
        Table.ClearReview(internalName, field);
        ApplyTranslations();
    }

    /// <summary>
    ///     一次加多条库（参与翻译页批量添加用）：自动备份一次、跳过已经在列表里的，
    ///     加完记一条可撤回的「添加」（右侧的「撤回添加」按钮就是撤它）。
    /// </summary>
    public bool AddThirdPartyRepositories(IReadOnlyList<string> urls, out string message)
    {
        message = string.Empty;

        var existing = Repos.ReadAll(out var readError);
        if (readError is not null)
        {
            message = "读不到仓库列表：" + readError;
            return false;
        }

        var present = new HashSet<string>(existing.Select(x => x.URL), StringComparer.Ordinal);
        var wanted = urls.Where(u => !string.IsNullOrWhiteSpace(u) && !present.Contains(u))
                         .Distinct(StringComparer.Ordinal)
                         .ToList();
        if (wanted.Count == 0)
        {
            message = "这些库都已经在你的列表里了";
            return false;
        }

        var backup = Repos.BackupRepos(out _);
        var added = wanted.Where(url => Repos.Add(url, out _) > 0).ToList();
        if (added.Count == 0)
        {
            message = "添加失败：一条也没加上";
            return false;
        }

        Repos.Save(out _);
        Repos.TriggerReload(out _);
        TrackFirstSeen();

        RecordUndo(new UndoRecord
        {
            Action = "add",
            TimeUTC = DateTime.UtcNow,
            Entries = added.Select(url => new UndoEntry { URL = url, IsEnabled = true, Index = 0 }).ToList(),
            BackupPath = string.IsNullOrEmpty(backup) ? null : backup,
        });

        message = $"已添加 {added.Count} 条库（可撤回）；卫月正在抓取它们的插件"
                  + (wanted.Count > added.Count ? $"，另有 {wanted.Count - added.Count} 条没加上" : string.Empty);
        return true;
    }

    /// <summary>
    ///     添加一条第三方仓库（参与翻译页单条添加用）：先自动备份，再加，再让卫月重新拉取。
    /// </summary>
    public bool AddThirdPartyRepository(string url, out string message)
        => AddThirdPartyRepositories([url], out message);

    /// <summary>
    ///     应用一次汉化（计时器用，静默）。
    /// </summary>
    private void ApplyTranslationsQuiet()
    {
        try
        {
            LastTranslatedCount = Patcher.ApplyAll();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    ///     立刻应用一次汉化（UI 按钮用）。
    /// </summary>
    public int ApplyTranslations()
    {
        LastTranslatedCount = Patcher.ApplyAll();
        return LastTranslatedCount;
    }

    /// <summary>
    ///     关闭汉化时把已经改写的文本还原成原文（总开关可逆）。
    /// </summary>
    public int RestoreTranslations()
    {
        var count = Patcher.RestoreAll();
        LastTranslatedCount = 0;
        return count;
    }

    /// <summary>
    ///     从 GitHub 更新词表并立刻应用。
    /// </summary>
    public Task<(bool Ok, string Message)> UpdateTranslationTableAsync()
        => UpdateTranslationTableAsync(automatic: false);

    private async Task<(bool Ok, string Message)> UpdateTranslationTableAsync(bool automatic)
    {
        const string closedMessage = "插件正在关闭，词表更新已取消。";
        try
        {
            return await RunTextTask(async lease =>
            {
                var (update, downloadMessage) = await Table.DownloadFromGitHubAsync(lease.ShutdownToken).ConfigureAwait(false);
                (bool Ok, string Message) result = (false, closedMessage);
                // Re-admit only the commit, using the same generation. Closing
                // rejects a late network receipt before any disk/config/host write.
                lease.TryExecute(() =>
                {
                    result = update is null ? (false, downloadMessage) : Table.CommitUpdate(update);
                    if (result.Ok)
                    {
                        var kept = ReapplyPendingContributions();
                        Config.LastTableUpdateLocal = DateTime.Now;
                        if (kept > 0) result.Message += $"；本地还没提交的 {kept} 条译文已保留并优先生效";
                    }
                    if (automatic || !result.Ok)
                        Config.LastTableUpdateCheckUTC = result.Ok ? DateTime.UtcNow : DateTime.UtcNow.AddHours(-23);
                    SaveConfig();
                    if (result.Ok) Patcher.ApplyAll();
                    if (result.Ok) Diagnostics.ActivityLog.Info("简介汉化", "更新词表：" + result.Message);
                    else Diagnostics.ActivityLog.Warning("简介汉化", "更新词表失败：" + result.Message);
                });
                return result;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (false, closedMessage);
        }
    }

    /// <summary>
    ///     把「待提交」里还没交出去的译文重新盖回词表（从 GitHub 更新词表之后调用）。
    ///     规则：本地译文优先于下载下来的版本；上游原文改过的那几条会进「待复核」。
    ///     返回重新盖上去的字段数。
    /// </summary>
    public int ReapplyPendingContributions()
    {
        var applied = 0;
        foreach (var record in Contributions.Records)
        {
            if (Table.MarkUserTranslation(record.InternalName, record.Field, record.Original, record.Translated))
            {
                applied++;
            }
        }

        if (applied > 0)
        {
            Table.SaveToConfigDirectory(out _);
        }

        return applied;
    }

    /// <summary>
    ///     每天自动检查一次词表更新（不可关闭；词表同时供插件发现用）。
    /// </summary>
    private void MaybeAutoUpdateTable()
    {
        if (Config.LastTableUpdateCheckUTC != default &&
            DateTime.UtcNow - Config.LastTableUpdateCheckUTC < TimeSpan.FromHours(24))
        {
            return;
        }

        if (Interlocked.Exchange(ref tableUpdateBusy, 1) == 1)
        {
            return;
        }

        _ = CompleteAutoUpdateTableAsync();
    }

    private async Task CompleteAutoUpdateTableAsync()
    {
        try
        {
            await UpdateTranslationTableAsync(automatic: true).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The observed task must terminate even if a consumer throws. No
            // host service is used here: this continuation may run after close.
        }
        finally
        {
            Interlocked.Exchange(ref tableUpdateBusy, 0);
        }
    }

    // ------------------------------------------------------------------ 仓库体检

    /// <summary>
    ///     记录首次见到的仓库（安装前就存在的记为「无记录」）。
    /// </summary>
    public void TrackFirstSeen()
    {
        try
        {
            var repos = Repos.ReadAll(out _);
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var changed = false;

            if (!Config.RepoFirstRunDone)
            {
                foreach (var repo in repos)
                {
                    if (!string.IsNullOrEmpty(repo.URL) && !Config.RepoFirstSeen.ContainsKey(repo.URL))
                    {
                        Config.RepoFirstSeen[repo.URL] = string.Empty;
                        changed = true;
                    }
                }

                Config.RepoFirstRunDone = true;
            }

            foreach (var repo in repos)
            {
                if (!string.IsNullOrEmpty(repo.URL) && !Config.RepoFirstSeen.ContainsKey(repo.URL))
                {
                    Config.RepoFirstSeen[repo.URL] = now;
                    changed = true;
                }
            }

            if (changed)
            {
                SaveConfig();
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "[FireGaze] 记录仓库首次出现时间失败");
        }
    }

    /// <summary>
    ///     取某仓库的首次记录时间（null = 无记录）。
    /// </summary>
    public string? GetFirstSeen(string url)
        => Config.RepoFirstSeen.TryGetValue(url, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>
    ///     推入一条撤销记录。
    /// </summary>
    public void RecordUndo(UndoRecord record)
    {
        Config.UndoHistory.Add(record);
        while (Config.UndoHistory.Count > Configuration.MaxUndo)
        {
            Config.UndoHistory.RemoveAt(0);
        }

        SaveConfig();
    }

    /// <summary>
    ///     撤回最近一次「停用 / 删除」。
    /// </summary>
    public bool TryUndoLast(out string message)
    {
        message = string.Empty;
        if (Config.UndoHistory.Count == 0)
        {
            message = "没有可撤回的操作";
            return false;
        }

        var record = Config.UndoHistory[^1];
        string? error;

        if (record.Action == "add")
        {
            var removed = Repos.Remove(record.Entries.Select(x => x.URL), out error);
            message = $"已撤回添加：移除了 {removed} 条库";
        }
        else if (record.Action == "delete")
        {
            var inserted = Repos.Insert(record.Entries, out error);
            message = $"已把 {inserted} 个仓库链接放回列表";
        }
        else
        {
            var urls = record.Entries.Where(x => x.IsEnabled).Select(x => x.URL);
            var changed = Repos.SetEnabled(urls, true, out error);
            message = $"已重新启用 {changed} 个仓库";
        }

        if (error is not null)
        {
            message += $"（出错：{error}）";
            return false;
        }

        Config.UndoHistory.RemoveAt(Config.UndoHistory.Count - 1);
        SaveConfig();
        Repos.Save(out _);
        Repos.TriggerReload(out _);
        TrackFirstSeen();
        return true;
    }

    /// <summary>
    ///     打开备份目录。
    /// </summary>
    public void OpenBackupDirectory()
    {
        try
        {
            var dir = Path.Combine(ConfigDirectory, "backups");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            Log.Warning(e, "[FireGaze] 打开备份目录失败");
        }
    }

    /// <summary>
    ///     卫月内部服务（internal）只能用反射拿。
    /// </summary>
    private static object? ResolveService(string typeName)
    {
        try
        {
            var dalamud = typeof(IDalamudPluginInterface).Assembly;
            var serviceOpen = dalamud.GetType("Dalamud.Service`1", throwOnError: false);
            var target = dalamud.GetType(typeName, throwOnError: false);
            if (serviceOpen is null || target is null)
            {
                return null;
            }

            var get = serviceOpen.MakeGenericType(target).GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
            return get?.Invoke(null, null);
        }
        catch
        {
            return null;
        }
    }
}
