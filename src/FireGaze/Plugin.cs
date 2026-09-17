using System.Diagnostics;
using System.Reflection;
using System.Timers;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FireGaze.RepoAudit;
using FireGaze.Translate;
using FireGaze.UI;
using Timer = System.Timers.Timer;

namespace FireGaze;

/// <summary>
/// FireGaze —— 卫月一体化工具箱：
///   ① 插件简介汉化（名字 / 一行简介 / 详情 × 原版 / 中文 / 双语）
///   ② 第三方仓库体检（扫描死链、内容不合规，支持停用 / 删除 + 备份 + 撤回）
///   ③ 插件列表自动刷新拦截（拦掉插件发起的后台仓库重载）
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string HarmonyId = "firegaze.installer-guard";

    /// <summary>
    /// 列表刷新拦截（软拦截）：重载照常执行（保留反射调用链，ECommons/OmenTools 等依赖它），
    /// 只跳过它引发的「安装器可用列表重建」，所以浏览时位置不会被顶回顶部。
    /// 下线开关：改成 false 即完全不挂钩、不显示页签。
    /// </summary>
    public static readonly bool BlockerFeatureEnabled = true;

    /// <summary>
    /// 「用户刚才在安装器里显式操作过」的放行窗口。
    /// 为什么按点击判定：安装器底部「刷新插件列表」与「打开安装器」调用的是同一个
    /// <c>PluginManager.ReloadAllReposAsync()</c>，列表重建又走同一个
    /// <c>OnAvailablePluginsChanged</c> 事件，事件侧分不出是谁发起的；
    /// 只有「底部按钮行里的那次点击」能可靠地代表用户显式刷新。
    /// </summary>
    private static readonly TimeSpan UserActionGrace = TimeSpan.FromSeconds(30);

    private static Plugin instance = null!;

    [PluginService] public static IPluginLog Log { get; private set; } = null!;

    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService] public static IChatGui Chat { get; private set; } = null!;

    [PluginService] public static IFramework Framework { get; private set; } = null!;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly WindowSystem windowSystem = new("FireGaze");
    private readonly MainWindow window;
    private readonly Timer translateTimer;

    private HarmonyHost? harmony;

    private readonly object saveLock = new();
    private readonly object recordLock = new();
    private readonly HashSet<string> loggedSources = new(StringComparer.Ordinal);
    private DateTime lastSave = DateTime.MinValue;

    private object? dalamudInterface;
    private PropertyInfo? windowSystemProp;
    private PropertyInfo? windowsProp;
    private PropertyInfo? isOpenProp;
    private int tableUpdateBusy;
    private long installerVisibleTicks;
    private long userActionTicks;
    private string lastAllowNote = string.Empty;
    private string lastSkipNote = string.Empty;
    private string lastSuppressNote = string.Empty;
    private int listSuppressCount;
    private int openReloadSkippedCount;
    private string lastOpenSkipNote = string.Empty;
    private bool pendingListScrollRestore;
    private int listScrollRestoreAttempts;
    private int listScrollGraceFrames;
    private bool listReady;
    private bool sawListReady;
    private bool listLoadingForced;
    private bool startupInitDone;
    private DateTime loadedAt;
    private readonly HashSet<string> registeredCommands = new(StringComparer.Ordinal);

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        instance = this;
        this.pluginInterface = pluginInterface;
        this.Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // 2.0 之前的默认值是「拦截开启 / 汉化开启」；新默认：两功能都先关着，由用户自己打开
        if (this.Config.Version < 2)
        {
            this.Config.Version = 2;
            this.Config.BlockerMode = BlockMode.Off;
            this.Config.TranslateEnabled = false;
            pluginInterface.SavePluginConfig(this.Config);
        }

        // 3.0：拦截规则简化为「安装器打开时不刷新」，旧的「全部跳过」等价于开启
        if (this.Config.Version < 3)
        {
            this.Config.Version = 3;
            if (this.Config.BlockerMode == BlockMode.Always)
            {
                this.Config.BlockerMode = BlockMode.InstallerOpenOnly;
            }

            pluginInterface.SavePluginConfig(this.Config);
        }

        this.ConfigDirectory = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(this.ConfigDirectory);

        this.Repos = new DalamudRepos(Path.Combine(this.ConfigDirectory, "backups"));

        var pluginDirectory = pluginInterface.AssemblyLocation.Directory?.FullName
                              ?? Path.GetDirectoryName(pluginInterface.AssemblyLocation.FullName)
                              ?? ".";
        this.Table = new TranslationTable(this.ConfigDirectory, pluginDirectory);
        this.Patcher = new ManifestPatcher(() => this.Config, this.Table, m => Log.Warning("[FireGaze] " + m));
        PluginLogFallback.Sink = m => Log.Warning("[FireGaze] " + m);

        this.window = new MainWindow(this);
        this.windowSystem.AddWindow(this.window);
        pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += this.ToggleWindow;

        // 重活（词表、挂钩、定时器）一律延后到「所有插件加载完」之后：
        // 加壳插件的模块初始化器会在加载阶段扫描/改写进程内存，我们需要在那个窗口里保持安静。
        this.loadedAt = DateTime.UtcNow;
        Framework.Update += this.OnStartupTick;

        Log.Information("[FireGaze] 已加载（初始化将等插件加载阶段结束后进行）");

        this.AddCommand(
            "/firegaze",
            new CommandInfo(this.OnCommand)
            {
                HelpMessage = "打开 FireGaze 窗口；子命令：on|off|open|log|zh|update",
            });
        this.AddCommand(
            "/nar",
            new CommandInfo((_, _) => this.ToggleWindow())
            {
                HelpMessage = "（旧命令）打开 FireGaze 窗口",
                ShowInHelp = false,
            });
        this.AddCommand(
            "/pdz",
            new CommandInfo((_, _) =>
            {
                this.ReloadTranslationTable(out _);
                this.Patcher.ApplyAll();
                Chat.Print("[FireGaze] 已重新加载词表并应用");
            })
            {
                HelpMessage = "（旧命令）重新加载简介汉化词表并应用",
                ShowInHelp = false,
            });

        this.translateTimer = new Timer(10_000) { AutoReset = true };
        this.translateTimer.Elapsed += (_, _) =>
        {
            this.ApplyTranslationsQuiet();
            this.MaybeAutoUpdateTable();
        };
    }

    /// <summary>
    /// 延迟初始化：等所有插件都不在加载中（或超时 30 秒）再动手。
    /// 加壳插件（.NET Reactor 系）在模块初始化器里会扫描/改写进程内存，
    /// 我们在这个窗口里不加载 0Harmony、不打补丁、不开定时器，避免把它们搞崩。
    /// </summary>
    private void OnStartupTick(IFramework framework)
    {
        if (this.startupInitDone)
        {
            return;
        }

        if (this.AnyPluginLoading() && DateTime.UtcNow - this.loadedAt < TimeSpan.FromSeconds(30))
        {
            return;
        }

        this.startupInitDone = true;
        Framework.Update -= this.OnStartupTick;

        try
        {
            this.ReloadTranslationTable(out _);
            this.TrackFirstSeen();

            if (this.Config.BlockerMode != BlockMode.Off)
            {
                this.InstallPatches();
            }
            else
            {
                this.BlockerStatusText = "未挂钩（模式为关闭）";
            }

            this.translateTimer.Start();

            Log.Information(
                $"[FireGaze] 初始化完成（插件加载阶段已结束）：词表 {this.Table.Count} 条；" +
                $"拦截模式 = {this.Config.BlockerMode}；汉化 = {(this.Config.TranslateEnabled ? "开" : "关")}");
        }
        catch (Exception e)
        {
            Log.Error(e, "[FireGaze] 延迟初始化失败");
        }
    }

    /// <summary>是否还有插件正在加载（反射读 PluginState == Loading）。</summary>
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

    /// <summary>当前配置。</summary>
    public Configuration Config { get; private set; }

    /// <summary>插件配置目录（备份写在这里的 backups/ 下）。</summary>
    public string ConfigDirectory { get; }

    /// <summary>翻译词表。</summary>
    public TranslationTable Table { get; }

    /// <summary>清单改写器。</summary>
    public ManifestPatcher Patcher { get; }

    /// <summary>卫月仓库配置读写。</summary>
    public DalamudRepos Repos { get; }

    /// <summary>钩子状态文字。</summary>
    public string BlockerStatusText { get; private set; } = "尚未挂钩";

    /// <summary>累计拦截次数。</summary>
    public int BlockedCount => this.Config.BlockedCount;

    /// <summary>最近一次「放行」的原因（供界面显示，便于验证手动刷新有没有生效）。</summary>
    public string LastAllowNote => this.lastAllowNote;

    /// <summary>最近一次跳过列表重建/挡下加载态的时间（界面显示用）。</summary>
    public string LastSkipNote => this.lastSkipNote;

    /// <summary>挡下「正在加载插件…」替换的次数与最近时间（界面显示用）。</summary>
    public string ListSuppressNote => this.listSuppressCount == 0
        ? "—"
        : $"{this.listSuppressCount} 次 · 最近 {this.lastSuppressNote}";

    /// <summary>跳过「打开安装器触发仓库重载」的次数与最近时间（界面显示用）。</summary>
    public string OpenSkipNote => this.openReloadSkippedCount == 0
        ? "—"
        : $"{this.openReloadSkippedCount} 次 · 最近 {this.lastOpenSkipNote}";

    /// <summary>最近拦下的来源。</summary>
    public IReadOnlyList<string> RecentBlockedSources
    {
        get
        {
            lock (this.recordLock)
            {
                return this.Config.RecentBlockedSources.ToArray();
            }
        }
    }

    /// <summary>最近一次汉化应用改写的清单数。</summary>
    public int LastTranslatedCount { get; private set; }

    public string Name => "FireGaze";

    // ------------------------------------------------------------------ 生命周期

    public void Dispose()
    {
        this.SaveConfig(force: true);

        try
        {
            Framework.Update -= this.OnStartupTick;
        }
        catch
        {
            // ignore
        }

        try
        {
            this.harmony?.Unpatch(HarmonyId);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[FireGaze] 卸载钩子失败");
        }

        try
        {
            this.translateTimer.Stop();
            this.translateTimer.Dispose();
        }
        catch
        {
            // ignore
        }

        this.pluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.ToggleWindow;
        foreach (var command in this.registeredCommands)
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
    }

    /// <summary>
    /// 注册聊天命令。命令名可能被其他插件占用（如 /fg 已属于别的插件），
    /// 占用时只记一条警告，不能让插件加载失败。
    /// </summary>
    private void AddCommand(string name, CommandInfo info)
    {
        try
        {
            CommandManager.AddHandler(name, info);
            this.registeredCommands.Add(name);
        }
        catch (Exception e)
        {
            Log.Warning($"[FireGaze] 命令 {name} 注册失败（可能已被其他插件占用）：{e.Message}");
        }
    }

    // ------------------------------------------------------------------ 窗口 / 命令

    /// <summary>切换主窗口。</summary>
    public void ToggleWindow() => this.window.Toggle();

    /// <summary>打开主窗口并跳到指定页。</summary>
    public void OpenWindow(MainTab tab)
    {
        this.window.SelectTab(tab);
        this.window.IsOpen = true;
        this.window.BringToFront();
    }

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim().ToLowerInvariant();

        if (!BlockerFeatureEnabled && arg is "on" or "off" or "open" or "log")
        {
            Chat.Print("[FireGaze] 列表刷新拦截功能暂时关闭。");
            return;
        }

        switch (arg)
        {
            case "":
                this.ToggleWindow();
                break;
            case "on":
                this.SetBlockerMode(BlockMode.Always);
                Chat.Print("[FireGaze] 已开启：安装器打开时不刷新列表");
                break;
            case "off":
                this.SetBlockerMode(BlockMode.Off);
                Chat.Print("[FireGaze] 已关闭：安装器打开时也照常刷新");
                break;
            case "open":
                this.SetBlockerMode(BlockMode.InstallerOpenOnly);
                Chat.Print("[FireGaze] 已开启：安装器打开时不刷新列表");
                break;
            case "log":
                this.PrintBlockedLog();
                break;
            case "zh":
                var count = this.Patcher.ApplyAll();
                this.LastTranslatedCount = count;
                Chat.Print($"[FireGaze] 已重新应用简介汉化（改写 {count} 条）");
                break;
            case "update":
                _ = this.UpdateTranslationTableAsync();
                Chat.Print("[FireGaze] 正在从 GitHub 更新词表…");
                break;
            default:
                Chat.Print("[FireGaze] 用法：/firegaze [on|off|open|log|zh|update]");
                break;
        }
    }

    private void PrintBlockedLog()
    {
        if (this.Config.RecentBlockedSources.Count == 0)
        {
            Chat.Print("[FireGaze] 暂无跳过记录");
            return;
        }

        Chat.Print($"[FireGaze] 累计跳过列表重建 {this.Config.BlockedCount} 次，最近来源：");
        foreach (var line in this.Config.RecentBlockedSources)
        {
            Chat.Print("  " + line);
        }
    }

    // ------------------------------------------------------------------ 配置

    /// <summary>保存插件配置。</summary>
    public void SaveConfig(bool force = true)
    {
        lock (this.saveLock)
        {
            if (!force && DateTime.UtcNow - this.lastSave < TimeSpan.FromSeconds(30))
            {
                return;
            }

            this.lastSave = DateTime.UtcNow;
        }

        try
        {
            this.pluginInterface.SavePluginConfig(this.Config);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[FireGaze] 保存配置失败");
        }
    }

    // ------------------------------------------------------------------ 博客：拦截

    /// <summary>切换拦截模式。</summary>
    /// <summary>界面开关：开启 = 安装器打开时不刷新列表。</summary>
    public void SetBlockerEnabled(bool enabled)
        => this.SetBlockerMode(enabled ? BlockMode.InstallerOpenOnly : BlockMode.Off);

    public void SetBlockerMode(BlockMode mode)
    {
        this.Config.BlockerMode = mode;
        this.SaveConfig();

        if (mode == BlockMode.Off)
        {
            this.UninstallPatches();
        }
        else if (this.harmony is null && this.startupInitDone)
        {
            this.InstallPatches();
        }
    }

    /// <summary>卸下钩子（模式切回「关闭」时调用）。已加载的 0Harmony 无法从默认 ALC 卸下，但不再使用。</summary>
    private void UninstallPatches()
    {
        try
        {
            this.harmony?.Unpatch(HarmonyId);
        }
        catch (Exception e)
        {
            Log.Warning(e, "[FireGaze] 卸载钩子失败");
        }

        this.harmony = null;
        this.BlockerStatusText = "未挂钩（模式为关闭）";
    }

    /// <summary>开关拦截日志。</summary>
    public void SetBlockerWriteLog(bool value)
    {
        this.Config.BlockerWriteLog = value;
        this.SaveConfig();
    }

    /// <summary>清空拦截记录。</summary>
    public void ClearBlockedRecords()
    {
        lock (this.recordLock)
        {
            this.Config.BlockedCount = 0;
            this.Config.RecentBlockedSources.Clear();
        }

        this.SaveConfig();
    }

    private void InstallPatches()
    {
        if (!BlockerFeatureEnabled)
        {
            this.BlockerStatusText = "已暂时关闭（在修复中）";
            Log.Information("[FireGaze] 列表刷新拦截功能暂时关闭（在修复中），本次不挂钩子");
            return;
        }

        if (this.harmony is not null)
        {
            // 已经挂过了（切换模式时重复调用）
            return;
        }

        try
        {
            var directory = this.pluginInterface.AssemblyLocation.Directory?.FullName
                            ?? Path.GetDirectoryName(this.pluginInterface.AssemblyLocation.FullName)
                            ?? ".";

            this.harmony = HarmonyHost.Create(HarmonyId, directory, out var harmonyError);
            if (this.harmony is null)
            {
                this.BlockerStatusText = "Harmony 不可用：" + (harmonyError ?? "未知原因");
                Log.Error("[FireGaze] " + this.BlockerStatusText);
                return;
            }

            Log.Information("[FireGaze] 0Harmony：" + HarmonyHost.Resolution);

            var dalamud = typeof(IDalamudPluginInterface).Assembly;
            var installerType = dalamud.GetType("Dalamud.Interface.Internal.Windows.PluginInstaller.PluginInstallerWindow");
            if (installerType is null)
            {
                this.BlockerStatusText = "找不到插件安装器类型（卫月版本不兼容），未挂钩";
                Log.Warning("[FireGaze] " + this.BlockerStatusText);
                return;
            }

            var drawPrefix = typeof(Plugin).GetMethod(nameof(InstallerDrawPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
            var openPrefix = typeof(Plugin).GetMethod(nameof(InstallerOpenPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
            var footerPrefix = typeof(Plugin).GetMethod(nameof(InstallerFooterPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
            var categoriesPrefix = typeof(Plugin).GetMethod(nameof(InstallerCategoriesPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
            var listContentPrefix = typeof(Plugin).GetMethod(nameof(InstallerListContentPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;
            var listLoadingPostfix = typeof(Plugin).GetMethod(nameof(InstallerListLoadingPostfix), BindingFlags.Static | BindingFlags.NonPublic)!;
            var refreshPrefix = typeof(Plugin).GetMethod(nameof(InstallerRefreshPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;

            var patched = new List<string>();
            var failed = new List<string>();

            // 只在「插件安装器」这一侧挂钩：完全不碰「重载仓库」的接口，
            // 避免影响依赖反射调用链的插件（ECommons/OmenTools/OmniToolbox/XSZ 等）。
            this.PatchInstallerHook(installerType, "Draw", "firegaze.installer.draw", drawPrefix, patched, failed, false);
            this.PatchInstallerHook(installerType, "OnOpen", "firegaze.installer.open", openPrefix, patched, failed, false);

            // 底部按钮行：用来识别「用户显式点了刷新」（事件侧无法区分手动刷新与打开安装器）。
            this.PatchInstallerHook(installerType, "DrawFooter", "firegaze.installer.footer", footerPrefix, patched, failed, false);

            this.PatchInstallerHook(installerType, "OnAvailablePluginsChanged", "firegaze.installer.rebuild", refreshPrefix, patched, failed, false);

            // 列表浏览位置：分类选择器前缀（其后紧接着就是列表子窗口 ScrollingPlugins 的 Begin）
            //   + 列表内容前缀（此刻当前窗口就是那个子窗口）。
            this.PatchInstallerHook(installerType, "DrawPluginCategorySelectors", "firegaze.installer.categories", categoriesPrefix, patched, failed, false);
            this.PatchInstallerHook(installerType, "DrawPluginCategoryContent", "firegaze.installer.list", listContentPrefix, patched, failed, false);

            // 列表加载态：用后缀（原方法照跑，失败信息照显），只把返回值改成「已就绪」，
            // 免得后台重载期间把整份列表替换成「正在加载插件…」。
            this.PatchInstallerHook(installerType, "DrawPluginListLoading", "firegaze.installer.loading", listLoadingPostfix, patched, failed, true);

            this.BlockerStatusText = patched.Count == 0
                ? "未挂钩（未找到目标方法）"
                : "已挂钩：" + string.Join(" / ", patched) +
                  (failed.Count > 0 ? $"（失败：{string.Join(" / ", failed)}）" : string.Empty);
            Log.Information($"[FireGaze] {this.BlockerStatusText}");
        }
        catch (Exception e)
        {
            this.BlockerStatusText = "挂钩失败：" + e.Message;
            Log.Error(e, "[FireGaze] 挂钩失败（不影响游戏）");
        }
    }

    /// <summary>
    /// 给安装器的一个方法挂钩。单个钩子失败只记警告，不影响其他钩子
    /// （卫月改版换名时最多丢一个功能，不会整片失效）。
    /// </summary>
    private void PatchInstallerHook(
        Type installerType,
        string methodName,
        string hookName,
        MethodInfo hook,
        List<string> patched,
        List<string> failed,
        bool postfix)
    {
        try
        {
            var target = installerType.GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

            if (target is null)
            {
                failed.Add(hookName);
                Log.Warning($"[FireGaze] 未找到 PluginInstallerWindow.{methodName}");
                return;
            }

            if (postfix)
            {
                this.harmony!.PatchPostfix(target, hook);
            }
            else
            {
                this.harmony!.PatchPrefix(target, hook);
            }

            patched.Add(hookName);
        }
        catch (Exception e)
        {
            failed.Add(hookName);
            Log.Warning(e, $"[FireGaze] 挂钩 {hookName} 失败");
        }
    }

    /// <summary>安装器绘制时的前缀（firegaze.installer.draw，窗口打开时每帧一次）：记录安装器当前可见。</summary>
    private static bool InstallerDrawPrefix()
    {
        instance?.NoteInstallerVisible();
        return true;
    }

    /// <summary>分类选择器前缀（firegaze.installer.categories）：恢复列表浏览位置。</summary>
    private static bool InstallerCategoriesPrefix()
    {
        instance?.RestoreListScroll();
        return true;
    }

    /// <summary>列表内容前缀（firegaze.installer.list）：此刻当前窗口 = 列表子窗口 ScrollingPlugins，读/确认滚动位置。</summary>
    private static bool InstallerListContentPrefix()
    {
        instance?.NoteListScroll();
        return true;
    }

    /// <summary>
    /// 列表加载态后缀（firegaze.installer.loading）：原方法照跑（失败提示照显），
    /// 只把返回值改为「已就绪」——拦截开启时继续用旧列表显示，不被「正在加载插件…」替掉。
    /// </summary>
    private static void InstallerListLoadingPostfix(ref bool __result)
    {
        instance?.NoteListLoadingResult(ref __result);
    }

    /// <summary>安装器打开时的前缀（firegaze.installer.open）。</summary>
    /// <remarks>
    /// 拦截开启时直接短路掉 <c>PluginInstallerWindow.OnOpen</c>：
    /// 它的开头就是 <c>pluginManager.ReloadAllReposAsync()</c> + <c>ScanDevPluginsAsync()</c>，
    /// 所以“每次打开安装器都联网把所有仓库重拉一遍”（界面卡在「加载插件仓库中…」）就是这里来的。
    /// 短路只跳开窗动作，窗口照常打开；不走重载接口，不影响依赖安装与插件更新。
    /// </remarks>
    private static bool InstallerOpenPrefix()
    {
        // 每次打开都清掉上一次会话残留的「用户操作」放行窗口：
        // 于是「打开安装器触发的那次刷新」依旧会被跳过（用户没有点过任何东西）。
        instance?.ResetUserActionGrace();
        instance?.NoteInstallerVisible();
        instance?.BeginListScrollRestore();

        return instance?.ShouldRunInstallerOnOpen() ?? true;
    }

    /// <summary>
    /// 安装器底部按钮行的前缀（firegaze.installer.footer，窗口打开时每帧一次）。
    /// 这一行里有「更新插件 / 扫描开发版插件 / 刷新插件列表 / 设置 / 关闭」，
    /// 点其中任何一个都算用户显式操作；「刷新插件列表」会发起一次仓库重载，
    /// 而它与「打开安装器」在事件侧无法区分，所以在这里按点击打标记。
    /// </summary>
    private static bool InstallerFooterPrefix()
    {
        instance?.NoteFooterInteraction();
        return true;
    }

    /// <summary>记录「用户刚在安装器底部按钮行里点过鼠标」。</summary>
    private void NoteFooterInteraction()
    {
        try
        {
            if (this.Config.BlockerMode == BlockMode.Off)
            {
                return;
            }

            if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                return;
            }

            // DrawFooter 在安装器窗口上下文里执行，这里的几何就是安装器窗口自己的。
            var mouse = ImGui.GetMousePos();
            var windowPos = ImGui.GetWindowPos();
            var contentMax = ImGui.GetWindowContentRegionMax();

            var rowHeight = ImGui.GetFrameHeight() + 8f;
            var rowTop = windowPos.Y + contentMax.Y - rowHeight;
            var rowBottom = windowPos.Y + contentMax.Y + 8f;

            if (mouse.Y < rowTop || mouse.Y > rowBottom)
            {
                return;
            }

            if (mouse.X < windowPos.X - 4f || mouse.X > windowPos.X + contentMax.X + 4f)
            {
                return;
            }

            Interlocked.Exchange(ref this.userActionTicks, DateTime.UtcNow.Ticks);
            Log.Debug("[FireGaze] 检测到安装器底部按钮行的点击 → 接下来 30 秒内的列表重建放行");
        }
        catch (Exception e)
        {
            Log.Debug(e, "[FireGaze] 记录安装器底部点击失败");
        }
    }

    /// <summary>列表重建前缀（firegaze.installer.rebuild）：安装器开着就跳过这次重建。</summary>
    private static bool InstallerRefreshPrefix()
    {
        return instance is null || instance.ShouldAllowListRebuild();
    }

    /// <summary>记录「安装器当前可见」。</summary>
    private void NoteInstallerVisible()
    {
        Interlocked.Exchange(ref this.installerVisibleTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>清空「用户操作」放行窗口（窗口每次打开时调用）。</summary>
    private void ResetUserActionGrace() => Interlocked.Exchange(ref this.userActionTicks, 0);

    // ---------------------------------------------------------- 安装器列表浏览位置

    /// <summary>开关：是否记住列表浏览位置。</summary>
    public void SetRememberListScroll(bool remember)
    {
        this.Config.RememberListScroll = remember;
        this.SaveConfig();
    }

    /// <summary>
    /// 开窗时是否让 <c>PluginInstallerWindow.OnOpen</c> 原件继续跑。
    /// 拦截开启时返回 false：不联网重拉仓库、不扫描 dev 插件、不清搜索框/不重置排序。
    /// </summary>
    private bool ShouldRunInstallerOnOpen()
    {
        if (this.Config.BlockerMode == BlockMode.Off)
        {
            return true;
        }

        this.openReloadSkippedCount++;
        this.lastOpenSkipNote = DateTime.Now.ToString("HH:mm:ss");
        Log.Debug("[FireGaze] 已跳过「打开安装器」触发的仓库重载（拦截开启）");
        return false;
    }

    /// <summary>安装器打开时：准备恢复列表浏览位置。</summary>
    private void BeginListScrollRestore()
    {
        this.pendingListScrollRestore = this.Config.RememberListScroll && this.Config.ListScrollY is not null;
        this.listScrollRestoreAttempts = 0;

        if (this.pendingListScrollRestore)
        {
            Log.Debug($"[FireGaze] 准备恢复列表浏览位置：{this.Config.ListScrollY:F0}");
        }
    }

    /// <summary>
    /// 恢复列表浏览位置。在「分类选择器」前缀里发：它后面紧跟的就是列表子窗口
    /// <c>ScrollingPlugins</c> 的 <c>Begin</c>（中间没有别的窗口 Begin），所以
    /// <c>SetNextWindowScroll</c> 会被那个子窗口立即消费，不会闪一下。
    /// 一直不生效（内容变短被夹到顶部）就放弃。
    /// </summary>
    private void RestoreListScroll()
    {
        try
        {
            if (!this.pendingListScrollRestore)
            {
                return;
            }

            if (this.Config.ListScrollY is not { } y)
            {
                this.pendingListScrollRestore = false;
                return;
            }

            if (!this.listReady)
            {
                // 列表还在加载（内容为空）时发会立刻被夹到 0，等就绪再发。
                return;
            }

            if (++this.listScrollRestoreAttempts > 20)
            {
                this.pendingListScrollRestore = false;
                return;
            }

            ImGuiP.SetNextWindowScroll(new System.Numerics.Vector2(-1f, y));
        }
        catch (Exception e)
        {
            Log.Debug(e, "[FireGaze] 恢复列表浏览位置失败");
            this.pendingListScrollRestore = false;
        }
    }

    /// <summary>记录列表滚动位置（前缀里执行时当前窗口就是列表子窗口）。</summary>
    private void NoteListScroll()
    {
        try
        {
            if (this.pendingListScrollRestore)
            {
                if (this.Config.ListScrollY is { } target && Math.Abs(ImGui.GetScrollY() - target) < 2f)
                {
                    this.pendingListScrollRestore = false;
                    this.listScrollGraceFrames = 1;
                }

                return;   // 恢复未到位前不要覆盖记录
            }

            if (!this.Config.RememberListScroll)
            {
                return;
            }

            if (this.listScrollGraceFrames > 0)
            {
                this.listScrollGraceFrames--;
                return;
            }

            if (ImGui.GetScrollMaxY() <= 0f && ImGui.GetScrollY() <= 0f)
            {
                return;   // 列表没内容（加载中/空）→ 不拿 0 去覆盖
            }

            var y = ImGui.GetScrollY();
            if (this.Config.ListScrollY == y)
            {
                return;
            }

            this.Config.ListScrollY = y;
            this.SaveConfig(force: false);
        }
        catch (Exception e)
        {
            Log.Debug(e, "[FireGaze] 记录列表浏览位置失败");
        }
    }

    /// <summary>
    /// 列表「是否就绪」的观察点：<paramref name="ready"/> 为 false 表示仓库正在重载
    /// （原方法会画「正在加载插件…」，并且让调用者跳过整个列表）。
    /// 拦截开启且以前成功画过列表时，把返回值改成 true：继续用旧列表显示，不拿加载态替掉它。
    /// </summary>
    private void NoteListLoadingResult(ref bool ready)
    {
        this.listReady = ready;

        if (ready)
        {
            this.sawListReady = true;
            this.listLoadingForced = false;
            return;
        }

        if (!this.sawListReady || this.Config.BlockerMode == BlockMode.Off || !this.IsInstallerVisible())
        {
            return;
        }

        ready = true;

        if (!this.listLoadingForced)
        {
            this.listLoadingForced = true;
            this.listSuppressCount++;
            this.lastSuppressNote = DateTime.Now.ToString("HH:mm:ss");
            Log.Debug("[FireGaze] 仓库重载中 → 保留旧列表显示（不显示「正在加载插件…」）");
        }
    }

    /// <summary>安装器是否开着：反射读窗口 IsOpen 为准，Draw/Open 记录做兜底（刚关掉的那一两帧）。</summary>
    private bool IsInstallerVisible()
    {
        if (this.IsInstallerOpen())
        {
            return true;
        }

        var ticks = Interlocked.Read(ref this.installerVisibleTicks);
        return ticks != 0 &&
               DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) <= TimeSpan.FromSeconds(3);
    }

    /// <summary>
    /// 是否允许安装器重建「可用插件列表」。
    /// 规则：安装器开着 → 跳过这次重建（列表与滚动位置保持不动）；关着 → 照常重建。
    /// </summary>
    private bool ShouldAllowListRebuild()
    {
        try
        {
            if (this.Config.BlockerMode == BlockMode.Off)
            {
                return true;
            }

            if (!this.IsInstallerVisible())
            {
                return true;
            }

            // 用户刚在安装器里点过底部按钮（尤其「刷新插件列表」）→ 放行，保证手动刷新有效。
            var userTicks = Interlocked.Read(ref this.userActionTicks);
            if (userTicks != 0 &&
                DateTime.UtcNow - new DateTime(userTicks, DateTimeKind.Utc) <= UserActionGrace)
            {
                this.lastAllowNote = $"用户操作（点过安装器底部按钮）· {DateTime.Now:HH:mm:ss}";
                if (this.Config.BlockerWriteLog)
                {
                    Log.Debug("[FireGaze] 检测到用户刚在安装器里操作过 → 本次列表重建放行");
                }

                return true;
            }

            int count;
            lock (this.recordLock)
            {
                this.Config.BlockedCount++;
                count = this.Config.BlockedCount;

                var line = $"{DateTime.Now:HH:mm:ss} 安装器打开中";
                this.Config.RecentBlockedSources.Insert(0, line);
                while (this.Config.RecentBlockedSources.Count > Configuration.MaxRecentBlocked)
                {
                    this.Config.RecentBlockedSources.RemoveAt(this.Config.RecentBlockedSources.Count - 1);
                }
            }

            this.SaveConfig(force: false);

            this.lastSkipNote = DateTime.Now.ToString("HH:mm:ss");
            if (this.Config.BlockerWriteLog)
            {
                Log.Information($"[FireGaze] firegaze.installer.rebuild 已跳过列表重建（第 {count} 次）：安装器打开中");
            }

            return false;
        }
        catch (Exception e)
        {
            Log.Error(e, "[FireGaze] 列表重建判断出错，本次放行");
            return true;
        }
    }

    /// <summary>插件安装器窗口是否开着。</summary>
    private bool IsInstallerOpen()
    {
        try
        {
            this.dalamudInterface ??= ResolveService("Dalamud.Interface.Internal.DalamudInterface");
            if (this.dalamudInterface is null)
            {
                return false;
            }

            if (this.windowSystemProp is null)
            {
                this.windowSystemProp = this.dalamudInterface.GetType()
                    .GetProperty("WindowSystem", BindingFlags.Instance | BindingFlags.Public);
            }

            var windowSystem = this.windowSystemProp?.GetValue(this.dalamudInterface);
            if (windowSystem is null)
            {
                return false;
            }

            this.windowsProp ??= windowSystem.GetType().GetProperty("Windows", BindingFlags.Instance | BindingFlags.Public);
            if (this.windowsProp?.GetValue(windowSystem) is not System.Collections.IEnumerable windows)
            {
                return false;
            }

            foreach (var item in windows)
            {
                if (item is null)
                {
                    continue;
                }

                if (item.GetType().Name != "PluginInstallerWindow")
                {
                    continue;
                }

                this.isOpenProp ??= item.GetType().GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public);
                return this.isOpenProp?.GetValue(item) is true;
            }
        }
        catch (Exception e)
        {
            Log.Debug(e, "[FireGaze] 查询安装器窗口状态失败");
        }

        return false;
    }

    // ------------------------------------------------------------------ 简介汉化

    /// <summary>重新加载词表。</summary>
    public bool ReloadTranslationTable(out string? error) => this.Table.Load(out error);

    /// <summary>应用一次汉化（计时器用，静默）。</summary>
    private void ApplyTranslationsQuiet()
    {
        try
        {
            this.LastTranslatedCount = this.Patcher.ApplyAll();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>立刻应用一次汉化（UI 按钮用）。</summary>
    public int ApplyTranslations()
    {
        this.LastTranslatedCount = this.Patcher.ApplyAll();
        return this.LastTranslatedCount;
    }

    /// <summary>关闭汉化时把已经改写的文本还原成原文（总开关可逆）。</summary>
    public int RestoreTranslations()
    {
        var count = this.Patcher.RestoreAll();
        this.LastTranslatedCount = 0;
        return count;
    }

    /// <summary>从 GitHub 更新词表并立刻应用。</summary>
    public async Task<(bool Ok, string Message)> UpdateTranslationTableAsync()
    {
        var (ok, message) = await this.Table.UpdateFromGitHubAsync(CancellationToken.None).ConfigureAwait(false);
        if (ok)
        {
            this.Patcher.ApplyAll();
        }

        return (ok, message);
    }

    /// <summary>每两周自动检查一次词表更新（汉化启用时才生效）。</summary>
    private void MaybeAutoUpdateTable()
    {
        if (!this.Config.TranslateEnabled || !this.Config.AutoUpdateTable)
        {
            return;
        }

        if (this.Config.LastTableUpdateCheckUtc != default &&
            DateTime.UtcNow - this.Config.LastTableUpdateCheckUtc < TimeSpan.FromDays(14))
        {
            return;
        }

        if (Interlocked.Exchange(ref this.tableUpdateBusy, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var success = false;
            try
            {
                var (ok, message) = await this.UpdateTranslationTableAsync().ConfigureAwait(false);
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
                this.Config.LastTableUpdateCheckUtc = success ? DateTime.UtcNow : DateTime.UtcNow.AddDays(-13);
                this.SaveConfig();
                Interlocked.Exchange(ref this.tableUpdateBusy, 0);
            }
        });
    }

    // ------------------------------------------------------------------ 仓库体检

    /// <summary>记录首次见到的仓库（安装前就存在的记为「无记录」）。</summary>
    public void TrackFirstSeen()
    {
        try
        {
            var repos = this.Repos.ReadAll(out _);
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var changed = false;

            if (!this.Config.RepoFirstRunDone)
            {
                foreach (var repo in repos)
                {
                    if (!string.IsNullOrEmpty(repo.Url) && !this.Config.RepoFirstSeen.ContainsKey(repo.Url))
                    {
                        this.Config.RepoFirstSeen[repo.Url] = string.Empty;
                        changed = true;
                    }
                }

                this.Config.RepoFirstRunDone = true;
            }

            foreach (var repo in repos)
            {
                if (!string.IsNullOrEmpty(repo.Url) && !this.Config.RepoFirstSeen.ContainsKey(repo.Url))
                {
                    this.Config.RepoFirstSeen[repo.Url] = now;
                    changed = true;
                }
            }

            if (changed)
            {
                this.SaveConfig();
            }
        }
        catch (Exception e)
        {
            Log.Warning(e, "[FireGaze] 记录仓库首次出现时间失败");
        }
    }

    /// <summary>取某仓库的首次记录时间（null = 无记录）。</summary>
    public string? GetFirstSeen(string url)
        => this.Config.RepoFirstSeen.TryGetValue(url, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>推入一条撤销记录。</summary>
    public void RecordUndo(UndoRecord record)
    {
        this.Config.UndoHistory.Add(record);
        while (this.Config.UndoHistory.Count > Configuration.MaxUndo)
        {
            this.Config.UndoHistory.RemoveAt(0);
        }

        this.SaveConfig();
    }

    /// <summary>撤回最近一次「停用 / 删除」。</summary>
    public bool TryUndoLast(out string message)
    {
        message = string.Empty;
        if (this.Config.UndoHistory.Count == 0)
        {
            message = "没有可撤回的操作";
            return false;
        }

        var record = this.Config.UndoHistory[^1];
        string? error;

        if (record.Action == "delete")
        {
            var inserted = this.Repos.Insert(record.Entries, out error);
            message = $"已把 {inserted} 个仓库链接放回列表";
        }
        else
        {
            var urls = record.Entries.Where(x => x.IsEnabled).Select(x => x.Url);
            var changed = this.Repos.SetEnabled(urls, true, out error);
            message = $"已重新启用 {changed} 个仓库";
        }

        if (error is not null)
        {
            message += $"（出错：{error}）";
            return false;
        }

        this.Config.UndoHistory.RemoveAt(this.Config.UndoHistory.Count - 1);
        this.SaveConfig();
        this.Repos.Save(out _);
        this.Repos.TriggerReload(out _);
        this.TrackFirstSeen();
        return true;
    }

    /// <summary>打开备份目录。</summary>
    public void OpenBackupDirectory()
    {
        try
        {
            var dir = Path.Combine(this.ConfigDirectory, "backups");
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

    /// <summary>卫月内部服务（internal）只能用反射拿。</summary>
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
