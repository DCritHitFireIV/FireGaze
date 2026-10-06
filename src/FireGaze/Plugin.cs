using System.Diagnostics;
using System.Reflection;
using System.Timers;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FireGaze.Internal.Configuration;
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
public sealed class Plugin : IDalamudPlugin
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

    /// <summary>
    ///     游戏数据（「插件汉化」的 FF14 术语表读 Lumina 表用；不联网）。
    /// </summary>
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly WindowSystem windowSystem = new("FireGaze");
    private readonly MainWindow window;
    private readonly UI.UITextEditorWindow uiTextEditorWindow;
    private readonly UI.UITextSettingsWindow uiTextSettingsWindow;
    private readonly UI.UITextTab uiTextTab;
    private readonly UIText.UITextRunLock uiTextRunLock;
    private readonly UIText.UITextPatchManager uiTextPatchManager;
    private readonly Timer translateTimer;

    private readonly object saveLock = new();
    private DateTime lastSave = DateTime.MinValue;

    private int tableUpdateBusy;
    private readonly UI.InstallerListScroll installerListScroll = new();
    private Task<RepoAudit.InstalledPluginsIndex>? iconWarmUpIndexTask;
    private DateTime iconWarmUpRetryAfter = DateTime.MinValue;
    private bool iconWarmUpRequested;
    private bool startupInitDone;
    private DateTime loadedAt;
    private readonly HashSet<string> registeredCommands = new(StringComparer.Ordinal);

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        instance = this;
        this.pluginInterface = pluginInterface;
        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // 配置迁移走单步迁移器（Internal/Configuration），缺台阶直接抛错；只在真的迁移过时保存一次
        var previousConfigVersion = ConfigurationMigrator.Migrate(Config);
        if (previousConfigVersion < ConfigurationMigrator.LatestVersion)
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
        window = new MainWindow(this, uiTextTab = new UI.UITextTab(this, uiTextEditorWindow, uiTextSettingsWindow, TextPacks, uiTextPatchManager, uiTextRunLock));
        windowSystem.AddWindow(window);
        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.Draw += TickInstallerListScroll;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainWindow;

        // 插件装/卸/启/停（公开事件，无钩子）：让「插件汉化」页的行状态立即跟上——
        // 用户在卫月安装器里手动启用后，不用等定期的 30 秒兜底
        pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;

        // 重活（词表、挂钩、定时器）一律延后到「所有插件加载完」之后：
        // 加壳插件的模块初始化器会在加载阶段扫描/改写进程内存，我们需要在那个窗口里保持安静。
        loadedAt = DateTime.UtcNow;
        Framework.Update += OnStartupTick;

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
            // 定时器线程上的未处理异常会直接终止游戏进程（Dalamud.Boot 的 0x12345679），
            // 所以这里**全程包住**：宁可少应用一次汉化，也不能把游戏带下去。
            try
            {
                ApplyTranslationsQuiet();
                MaybeAutoUpdateTable();
            }
            catch (Exception e)
            {
                Log.Warning(e, "[FireGaze] 定时任务出错（已拦下，不影响游戏）");
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

            // 补丁复核：上次打补丁后游戏如果没跑起来（崩了/被强杀），这里会自动还原
            uiTextPatchManager.VerifyOnStartup();

            translateTimer.Start();

            Log.Information(
                $"[FireGaze] 初始化完成（插件加载阶段已结束）：词表 {Table.Count} 条；" +
                $"汉化 = {(Config.TranslateEnabled ? "开" : "关")}");
            Log.Information("[FireGaze] 页签顺序：插件汉化 / 插件发现 / 仓库体检 / 简介汉化 / 插件安装器");

            // 一次性更新公告：更新后第一次加载时播报一次（插件汉化 / 插件发现各对应一个标记，
            // 老玩家已经看过旧的汉化公告，但插件发现的消息还会补一次）
            if (!Config.UITextFeatureAnnounced || !Config.DiscoveryFeatureAnnounced)
            {
                Config.UITextFeatureAnnounced = true;
                Config.DiscoveryFeatureAnnounced = true;
                Chat.Print("FireGaze: 上线了插件汉化和插件发现的功能，现在可以汉化插件界面、浏览云端插件库了");
                SaveConfig();
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "[FireGaze] 延迟初始化失败");
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
    public Configuration Config { get; private set; }

    /// <summary>
    ///     本地待提交的翻译贡献（配置目录，不联网）。
    /// </summary>
    internal ContributionsStore Contributions { get; }

    /// <summary>
    ///     插件配置目录（备份写在这里的 backups/ 下）。
    /// </summary>
    public string ConfigDirectory { get; }

    /// <summary>
    ///     图标落盘缓存（体检页 + 插件安装器共用）。
    /// </summary>
    internal UI.IconStore Icons { get; }

    /// <summary>
    ///     诊断用：与 <see cref="ConfigDirectory"/> 相同（dalamudUI.ini 就在它上两级）。
    /// </summary>
    public static string ConfigDirectoryForDiagnostics => instance?.ConfigDirectory ?? "(插件已卸载)";

    /// <summary>
    ///     翻译词表。
    /// </summary>
    public TranslationTable Table { get; }

    /// <summary>
    ///     清单改写器。
    /// </summary>
    public ManifestPatcher Patcher { get; }

    /// <summary>
    ///     卫月仓库配置读写。
    /// </summary>
    public DalamudRepos Repos { get; }

    /// <summary>
    ///     插件内部文本的本地包存放（「插件汉化」页签用）。
    /// </summary>
    internal UIText.UITextStore TextPacks { get; }

    /// <summary>
    ///     持久数据目录（默认 <c>&lt;启动器根&gt;\FireGazeData\</c>）：补丁原始备份与译文包镜像放这里，
    ///     活过「重置插件配置」（2026-10-02 全量落地）。解析不出来时等于配置目录，退回旧行为。
    /// </summary>
    internal string DurableDataDirectory { get; }

    /// <summary>
    ///     公共译文库下载端（按需拉取现成译文；数据只作加速，补丁仍走本地）。
    /// </summary>
    internal UIText.UITextLibrary TextLibrary { get; }

    /// <summary>
    ///     插件内部文本的补丁调度（打补丁 / 还原 / 重载 / 更新后重打）。
    /// </summary>
    internal UIText.UITextPatchManager TextPatches => uiTextPatchManager;

    /// <summary>
    ///     最近一次汉化应用改写的清单数。
    /// </summary>
    public int LastTranslatedCount { get; private set; }

    public string Name => "FireGaze";

    // ------------------------------------------------------------------ 生命周期

    public void Dispose()
    {
        try
        {
            // 干净退出 = 补丁没把游戏搞死：把「待确认」全部转正，下次启动就不会误还原
            uiTextPatchManager.MarkAllVerified();
        }
        catch (Exception)
        {
            // 退出路径上不抛
        }

        SaveConfig(force: true);

        try
        {
            Icons.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            Framework.Update -= OnStartupTick;
        }
        catch
        {
            // ignore
        }

        try
        {
            translateTimer.Stop();
            translateTimer.Dispose();
        }
        catch
        {
            // ignore
        }

        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.Draw -= TickInstallerListScroll;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainWindow;
        pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        try
        {
            window.Detach();
        }
        catch
        {
            // ignore
        }
        foreach (var command in registeredCommands)
        {
            try
            {
                CommandManager.RemoveHandler(command);
            }
            catch
            {
                // ignore
            }
        }

        // 卸载后别把旧实例留在静态字段上（热重载时旧实例与它的程序集因此可以被回收）
        if (ReferenceEquals(instance, this))
        {
            instance = null;
        }
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
    public void ToggleWindow() => window.Toggle();

    /// <summary>
    ///     卫月说插件列表变了（装/卸/启/停）：转给「插件汉化」页重建索引。
    ///     事件可能在任意线程；页签只置一个标记，绘制线程下一帧处理。
    /// </summary>
    private void OnActivePluginsChanged(Dalamud.Plugin.IActivePluginsChangedEventArgs args)
    {
        try
        {
            uiTextTab.NotifyInstalledPluginsChanged();
        }
        catch (Exception e)
        {
            Log.Warning(e, "[FireGaze] 处理插件状态变化失败");
        }
    }

    /// <summary>
    ///     打开主窗口（供卫月插件安装器的「主界面」入口调用；不切换页签）。
    /// </summary>
    public void OpenMainWindow()
    {
        window.IsOpen = true;
        window.BringToFront();
    }

    /// <summary>
    ///     打开主窗口并跳到指定页。
    /// </summary>
    public void OpenWindow(MainTab tab)
    {
        window.SelectTab(tab);
        window.IsOpen = true;
        window.BringToFront();
    }

    /// <summary>
    ///     打开「插件发现」页签（原「参与翻译」独立窗口已并入主窗口第五页签）。
    /// </summary>
    public void OpenContributeWindow()
    {
        window.SelectTab(UI.MainTab.Discovery);
        window.IsOpen = true;
        window.BringToFront();
    }

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim().ToLowerInvariant();

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
                Chat.Print("[FireGaze] 用法：/firegaze [zh|update|translate]");
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
        try
        {
            installerListScroll.Tick(Config, () => SaveConfig(force: false));
            uiTextPatchManager.Tick();

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
    ///     给图标预热准备「已装插件」索引（后台建一次就够；读不到就过 10 秒再试）。
    /// </summary>
    private void EnsureIconWarmUpIndex()
    {
        if (iconWarmUpRequested)
        {
            return;   // 已经排过一次（含空清单），不要每帧重建索引
        }

        if (iconWarmUpIndexTask is null)
        {
            if (DateTime.UtcNow < iconWarmUpRetryAfter)
            {
                return;
            }

            iconWarmUpIndexTask = Task.Run(RepoAudit.InstalledPluginsIndex.Build);
            return;
        }

        if (!iconWarmUpIndexTask.IsCompleted)
        {
            return;
        }

        var index = iconWarmUpIndexTask.Status == TaskStatus.RanToCompletion
            ? iconWarmUpIndexTask.Result
            : null;

        iconWarmUpIndexTask = null;

        if (index is { Available: true })
        {
            iconWarmUpRequested = true;
            Icons.ScheduleWarmUp(index.All);
        }
        else
        {
            iconWarmUpRetryAfter = DateTime.UtcNow.AddSeconds(10);
        }
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
    public async Task<(bool Ok, string Message)> UpdateTranslationTableAsync()
    {
        var (ok, message) = await Table.UpdateFromGitHubAsync(CancellationToken.None).ConfigureAwait(false);
        if (ok)
        {
            // 下载下来的那份不能盖掉本地还没交出去的译文（用户 2026-09-22 定：要保留，而且要优先）
            var kept = ReapplyPendingContributions();
            Config.LastTableUpdateLocal = DateTime.Now;
            SaveConfig();
            Patcher.ApplyAll();
            if (kept > 0)
            {
                message += $"；本地还没提交的 {kept} 条译文已保留并优先生效";
            }
        }

        return (ok, message);
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
    ///     每两周自动检查一次词表更新（汉化启用时才生效）。
    /// </summary>
    private void MaybeAutoUpdateTable()
    {
        if (!Config.TranslateEnabled || !Config.AutoUpdateTable)
        {
            return;
        }

        if (Config.LastTableUpdateCheckUTC != default &&
            DateTime.UtcNow - Config.LastTableUpdateCheckUTC < TimeSpan.FromDays(14))
        {
            return;
        }

        if (Interlocked.Exchange(ref tableUpdateBusy, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var success = false;
            try
            {
                var (ok, message) = await UpdateTranslationTableAsync().ConfigureAwait(false);
                success = ok;
                Log.Information($"[FireGaze] 自动更新词表：{message}");
            }
            catch (Exception e)
            {
                Log.Warning(e, "[FireGaze] 自动更新词表失败");
            }
            finally
            {
                // 失败时 1 天后重试，成功则 14 天后再查
                Config.LastTableUpdateCheckUTC = success ? DateTime.UtcNow : DateTime.UtcNow.AddDays(-13);
                SaveConfig();
                Interlocked.Exchange(ref tableUpdateBusy, 0);
            }
        });
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
