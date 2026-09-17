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
///   ③ 拦住插件安装器的自动刷新（并可选地记住列表浏览位置）
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private static Plugin instance = null!;

    [PluginService] public static IPluginLog Log { get; private set; } = null!;

    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService] public static IChatGui Chat { get; private set; } = null!;

    [PluginService] public static IFramework Framework { get; private set; } = null!;

    /// <summary>纹理服务（图标落盘缓存 → 纹理用；卫月注入）。</summary>
    [PluginService] public static ITextureProvider Textures { get; private set; } = null!;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly WindowSystem windowSystem = new("FireGaze");
    private readonly MainWindow window;
    private readonly Timer translateTimer;

    private readonly object saveLock = new();
    private DateTime lastSave = DateTime.MinValue;

    private int tableUpdateBusy;
    private readonly UI.InstallerListScroll installerListScroll = new();
    private Task<RepoAudit.InstalledPluginsIndex>? iconWarmUpIndexTask;
    private DateTime iconWarmUpRetryAfter = DateTime.MinValue;
    private bool iconWarmUpRequested;
    private bool installerDefaultsNotice;
    private bool startupInitDone;
    private DateTime loadedAt;
    private readonly HashSet<string> registeredCommands = new(StringComparer.Ordinal);

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        instance = this;
        this.pluginInterface = pluginInterface;
        this.Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // 2.0 之前的默认值是「汉化开启」；新默认先关着，由用户自己打开
        if (this.Config.Version < 2)
        {
            this.Config.Version = 2;
            this.Config.TranslateEnabled = false;
            pluginInterface.SavePluginConfig(this.Config);
        }

        // 3.0：安装器那两项功能改为**默认关**（由用户自己打开）
        if (this.Config.Version < 3)
        {
            this.Config.Version = 3;
            this.Config.RememberListScroll = false;
            this.Config.BlockInstallerAutoRefresh = false;
            this.Config.ListScrollY = null;
            this.installerDefaultsNotice = true;
            pluginInterface.SavePluginConfig(this.Config);
        }

        this.ConfigDirectory = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(this.ConfigDirectory);

        this.Icons = new UI.IconStore(this.ConfigDirectory);

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
        pluginInterface.UiBuilder.Draw += this.TickInstallerListScroll;
        pluginInterface.UiBuilder.OpenConfigUi += this.ToggleWindow;

        // 重活（词表、挂钩、定时器）一律延后到「所有插件加载完」之后：
        // 加壳插件的模块初始化器会在加载阶段扫描/改写进程内存，我们需要在那个窗口里保持安静。
        this.loadedAt = DateTime.UtcNow;
        Framework.Update += this.OnStartupTick;

        Log.Information($"[FireGaze] 已加载 v{typeof(Plugin).Assembly.GetName().Version}（初始化将等插件加载阶段结束后进行）");

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
    /// 我们在这个窗口里不做任何重活、不开定时器，避免把它们搞崩。
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

            this.translateTimer.Start();

            Log.Information(
                $"[FireGaze] 初始化完成（插件加载阶段已结束）：词表 {this.Table.Count} 条；" +
                $"汉化 = {(this.Config.TranslateEnabled ? "开" : "关")}");

            if (this.installerDefaultsNotice)
            {
                this.installerDefaultsNotice = false;
                Chat.Print("[FireGaze] 安装器增强的两项功能已改为默认关闭，可在 /firegaze → 插件安装器 里打开。");
            }
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

    /// <summary>图标落盘缓存（体检页 + 插件安装器共用）。</summary>
    internal UI.IconStore Icons { get; }

    /// <summary>诊断用：与 <see cref="ConfigDirectory"/> 相同（dalamudUI.ini 就在它上两级）。</summary>
    public static string ConfigDirectoryForDiagnostics => instance.ConfigDirectory;

    /// <summary>翻译词表。</summary>
    public TranslationTable Table { get; }

    /// <summary>清单改写器。</summary>
    public ManifestPatcher Patcher { get; }

    /// <summary>卫月仓库配置读写。</summary>
    public DalamudRepos Repos { get; }

    /// <summary>最近一次汉化应用改写的清单数。</summary>
    public int LastTranslatedCount { get; private set; }

    public string Name => "FireGaze";

    // ------------------------------------------------------------------ 生命周期

    public void Dispose()
    {
        this.SaveConfig(force: true);

        try
        {
            this.Icons.Dispose();
        }
        catch
        {
            // ignore
        }

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
            this.translateTimer.Stop();
            this.translateTimer.Dispose();
        }
        catch
        {
            // ignore
        }

        this.pluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        this.pluginInterface.UiBuilder.Draw -= this.TickInstallerListScroll;
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

        switch (arg)
        {
            case "":
                this.ToggleWindow();
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
                Chat.Print("[FireGaze] 用法：/firegaze [zh|update]");
                break;
        }
    }

    // ------------------------------------------------------------------ 安装器列表浏览位置

    /// <summary>开关：是否记住列表浏览位置（关掉时把已记住的位置一并清掉，等于「忘掉」）。</summary>
    public void SetRememberListScroll(bool remember)
    {
        this.Config.RememberListScroll = remember;
        if (!remember)
        {
            this.Config.ListScrollY = null;
        }

        this.SaveConfig();
    }

    /// <summary>设置页要用的安装器功能状态（只读）。</summary>
    internal UI.InstallerListScroll InstallerFeatures => this.installerListScroll;

    /// <summary>卫月插件接口（仓库体检页订阅「插件列表变化」用）。</summary>
    internal IDalamudPluginInterface PluginInterface => this.pluginInterface;

    /// <summary>开关：是否拦住插件安装器的自动刷新。</summary>
    public void SetBlockInstallerAutoRefresh(bool block)
    {
        this.Config.BlockInstallerAutoRefresh = block;
        this.SaveConfig();
    }

    /// <summary>每帧看一眼插件安装器（记住滚动位置 / 拦住自动刷新 / 图标预热；不用钩子）。</summary>
    private void TickInstallerListScroll()
    {
        this.installerListScroll.Tick(this.Config, () => this.SaveConfig(force: false));

        // 安装器开着 → 把本地缓存的图标分批挂回卫月的图标缓存（安装器直接用本地图，不重新下载）
        if (this.installerListScroll.IsOpen)
        {
            this.EnsureIconWarmUpIndex();
            this.Icons.WarmUpStep(4);
        }
    }

    /// <summary>给图标预热准备「已装插件」索引（后台建一次就够；读不到就过 10 秒再试）。</summary>
    private void EnsureIconWarmUpIndex()
    {
        if (this.iconWarmUpRequested)
        {
            return;   // 已经排过一次（含空清单），不要每帧重建索引
        }

        if (this.iconWarmUpIndexTask is null)
        {
            if (DateTime.UtcNow < this.iconWarmUpRetryAfter)
            {
                return;
            }

            this.iconWarmUpIndexTask = Task.Run(RepoAudit.InstalledPluginsIndex.Build);
            return;
        }

        if (!this.iconWarmUpIndexTask.IsCompleted)
        {
            return;
        }

        var index = this.iconWarmUpIndexTask.Status == TaskStatus.RanToCompletion
            ? this.iconWarmUpIndexTask.Result
            : null;

        this.iconWarmUpIndexTask = null;

        if (index is { Available: true })
        {
            this.iconWarmUpRequested = true;
            this.Icons.ScheduleWarmUp(index.All);
        }
        else
        {
            this.iconWarmUpRetryAfter = DateTime.UtcNow.AddSeconds(10);
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

    // ------------------------------------------------------------------ 安装器钩子

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
