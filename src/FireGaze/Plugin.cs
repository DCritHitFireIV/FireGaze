using System.Diagnostics;
using System.Reflection;
using System.Timers;
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
    private const string HarmonyId = "firegaze.auto-refresh-blocker";

    private static Plugin instance = null!;

    [PluginService] public static IPluginLog Log { get; private set; } = null!;

    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService] public static IChatGui Chat { get; private set; } = null!;

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

        this.ConfigDirectory = pluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(this.ConfigDirectory);

        this.Repos = new DalamudRepos(Path.Combine(this.ConfigDirectory, "backups"));

        var pluginDirectory = pluginInterface.AssemblyLocation.Directory?.FullName
                              ?? Path.GetDirectoryName(pluginInterface.AssemblyLocation.FullName)
                              ?? ".";
        this.Table = new TranslationTable(this.ConfigDirectory, pluginDirectory);
        this.Patcher = new ManifestPatcher(() => this.Config, this.Table, m => Log.Warning("[FireGaze] " + m));
        PluginLogFallback.Sink = m => Log.Warning("[FireGaze] " + m);

        this.ReloadTranslationTable(out _);
        this.TrackFirstSeen();

        this.window = new MainWindow(this);
        this.windowSystem.AddWindow(this.window);
        pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += this.ToggleWindow;

        this.InstallPatches();

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
        this.translateTimer.Start();

        Log.Information(
            $"[FireGaze] 已加载：词表 {this.Table.Count} 条；拦截模式 = {this.Config.BlockerMode}；" +
            $"汉化 = {(this.Config.TranslateEnabled ? "开" : "关")}");
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
        switch (arg)
        {
            case "":
                this.ToggleWindow();
                break;
            case "on":
                this.SetBlockerMode(BlockMode.Always);
                Chat.Print("[FireGaze] 已开启拦截（所有后台自动刷新）");
                break;
            case "off":
                this.SetBlockerMode(BlockMode.Off);
                Chat.Print("[FireGaze] 已关闭拦截");
                break;
            case "open":
                this.SetBlockerMode(BlockMode.InstallerOpenOnly);
                Chat.Print("[FireGaze] 只在插件安装器打开时拦截");
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
            Chat.Print("[FireGaze] 暂无拦截记录");
            return;
        }

        Chat.Print($"[FireGaze] 累计拦截 {this.Config.BlockedCount} 次，最近来源：");
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
    public void SetBlockerMode(BlockMode mode)
    {
        this.Config.BlockerMode = mode;
        this.SaveConfig();
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

            var manager = ResolveService("Dalamud.Plugin.Internal.PluginManager");
            if (manager is null)
            {
                this.BlockerStatusText = "找不到 PluginManager（卫月版本不兼容），未挂钩";
                Log.Warning("[FireGaze] " + this.BlockerStatusText);
                return;
            }

            var target = manager.GetType();
            var prefix = typeof(Plugin).GetMethod(nameof(BlockerPrefix), BindingFlags.Static | BindingFlags.NonPublic)!;

            var patched = new List<string>();
            foreach (var name in new[] { "SetPluginReposFromConfigAsync", "ReloadAllReposAsync" })
            {
                var method = target.GetMethod(name, BindingFlags.Instance | BindingFlags.Public);
                if (method is null)
                {
                    Log.Warning($"[FireGaze] 未找到 {target.FullName}.{name}");
                    continue;
                }

                this.harmony.PatchPrefix(method, prefix);
                patched.Add(name);
            }

            this.BlockerStatusText = patched.Count == 0 ? "未找到目标方法" : "已挂钩：" + string.Join(" / ", patched);
            Log.Information($"[FireGaze] {this.BlockerStatusText}");
        }
        catch (Exception e)
        {
            this.BlockerStatusText = "挂钩失败：" + e.Message;
            Log.Error(e, "[FireGaze] 挂钩失败（不影响游戏）");
        }
    }

    /// <summary>
    /// 前缀钩子：返回 false 时用 <paramref name="__result"/> 顶替原方法返回值
    /// （目标是 async Task，不能返回 null，否则调用方 await 会炸）。
    /// </summary>
    private static bool BlockerPrefix(MethodBase __originalMethod, ref Task __result)
    {
        if (instance is null || instance.OnBlockerPrefix(__originalMethod))
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }

    private bool OnBlockerPrefix(MethodBase original)
    {
        try
        {
            var caller = this.FindPluginCaller();
            if (caller is null)
            {
                // 卫月自己发起的（安装器 / 刷新按钮 / 设置里改仓库 / 卫月定时检查 / FireGaze 自己改仓库）
                return true;
            }

            var mode = this.Config.BlockerMode;
            if (mode == BlockMode.Off)
            {
                return true;
            }

            if (mode == BlockMode.InstallerOpenOnly && !this.IsInstallerOpen())
            {
                return true;
            }

            int count;
            lock (this.recordLock)
            {
                this.Config.BlockedCount++;
                count = this.Config.BlockedCount;

                var line = $"{caller} → {original.Name}";
                this.Config.RecentBlockedSources.RemoveAll(x => x == line);
                this.Config.RecentBlockedSources.Insert(0, line);
                if (this.Config.RecentBlockedSources.Count > Configuration.MaxRecentBlocked)
                {
                    this.Config.RecentBlockedSources.RemoveRange(
                        Configuration.MaxRecentBlocked,
                        this.Config.RecentBlockedSources.Count - Configuration.MaxRecentBlocked);
                }
            }

            this.SaveConfig(force: false);

            if (this.Config.BlockerWriteLog)
            {
                bool first;
                lock (this.recordLock)
                {
                    first = this.loggedSources.Add(caller);
                }

                var message = $"[FireGaze] 已拦截后台刷新（第 {count} 次）：{caller} → {original.Name}";
                if (first)
                {
                    Log.Information(message);
                }
                else
                {
                    Log.Debug(message);
                }
            }

            return false;
        }
        catch (Exception e)
        {
            Log.Error(e, "[FireGaze] 拦截判断出错，本次放行");
            return true;
        }
    }

    /// <summary>找出第一个「不是卫月自己」的调用帧；整条调用链都在卫月 / FireGaze 里就返回 null。</summary>
    private string? FindPluginCaller()
    {
        var trace = new StackTrace(1, false);
        var self = typeof(Plugin).Assembly;
        var chain = new List<string>();
        var assemblies = new HashSet<string>(StringComparer.Ordinal);

        foreach (var frame in trace.GetFrames())
        {
            var method = frame.GetMethod();
            if (method is null)
            {
                continue;
            }

            var assembly = method.DeclaringType?.Assembly ?? method.Module.Assembly;
            if (ReferenceEquals(assembly, self))
            {
                continue;
            }

            var assemblyName = assembly.GetName().Name;
            if (assemblyName is null || IsIgnored(assemblyName))
            {
                continue;
            }

            if (!assemblies.Add(assemblyName))
            {
                continue;
            }

            var type = method.DeclaringType?.FullName;
            chain.Add(type is null ? assemblyName : $"{assemblyName} · {type}.{method.Name}");

            if (chain.Count >= 3)
            {
                break;
            }
        }

        return chain.Count == 0 ? null : string.Join(" ← ", chain);
    }

    private static bool IsIgnored(string assemblyName) =>
        assemblyName is "Dalamud" or "0Harmony" or "netstandard" or "mscorlib" or "Newtonsoft.Json" or "Serilog" or "ImGui.NET" ||
        assemblyName.StartsWith("Dalamud.", StringComparison.Ordinal) ||
        assemblyName.StartsWith("System.", StringComparison.Ordinal) ||
        assemblyName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
        assemblyName.StartsWith("FFXIVClientStructs", StringComparison.Ordinal) ||
        assemblyName.StartsWith("Lumina", StringComparison.Ordinal) ||
        assemblyName.StartsWith("InteropGenerator", StringComparison.Ordinal);

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
