using System.Collections;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dalamud.Plugin;

namespace FireGaze.RepoAudit;

/// <summary>仓库列表里的一条记录。</summary>
public sealed class RepoEntry
{
    public string Url { get; init; } = string.Empty;

    public bool IsEnabled { get; init; }

    /// <summary>在配置列表里的位置（用于撤回时放回原处）。</summary>
    public int Index { get; init; }
}

/// <summary>
/// 直接操作卫月运行时的仓库配置：
///   · 读取 <c>Dalamud.Configuration.Internal.DalamudConfiguration.ThirdRepoList</c>（含已停用项）；
///   · 停用 / 删除 / 放回条目；
///   · <c>QueueSave()</c> 让卫月自己把配置写回磁盘（比我们直接改文件安全）；
///   · 触发 <c>PluginManager.SetPluginReposFromConfigAsync(true)</c> 刷新列表。
///
/// 全程反射访问 internal 类型；任何一步失败都会返回 false 并把原因写进 error。
/// </summary>
public sealed class DalamudRepos
{
    private readonly string backupDirectory;

    private Type? serviceOpenType;
    private Type? configType;
    private object? config;
    private PropertyInfo? thirdRepoListProp;
    private MethodInfo? queueSave;
    private Type? managerType;
    private object? manager;
    private MethodInfo? setReposMethod;
    private PropertyInfo? repoItemUrlProp;
    private PropertyInfo? repoItemEnabledProp;

    public DalamudRepos(string backupDirectory)
    {
        this.backupDirectory = backupDirectory;
    }

    /// <summary>最近一次「刷新仓库」任务的完成情况。</summary>
    public Task? LastRefreshTask { get; private set; }

    /// <summary>读取全部仓库（含已停用），按配置里的顺序。</summary>
    public List<RepoEntry> ReadAll(out string? error)
    {
        error = null;
        var result = new List<RepoEntry>();

        if (!this.EnsureConfig(out error))
        {
            return result;
        }

        try
        {
            if (this.thirdRepoListProp?.GetValue(this.config) is not IList list)
            {
                error = "读取 ThirdRepoList 失败";
                return result;
            }

            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item is null)
                {
                    continue;
                }

                var url = this.repoItemUrlProp?.GetValue(item) as string ?? string.Empty;
                var enabled = this.repoItemEnabledProp?.GetValue(item) as bool? ?? false;
                result.Add(new RepoEntry { Url = url, IsEnabled = enabled, Index = i });
            }
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        return result;
    }

    /// <summary>把若干 URL 设为指定启用状态；返回实际改动的条数。</summary>
    public int SetEnabled(IEnumerable<string> urls, bool enabled, out string? error)
    {
        error = null;
        var set = new HashSet<string>(urls, StringComparer.Ordinal);
        var changed = 0;

        if (!this.EnsureConfig(out error))
        {
            return 0;
        }

        try
        {
            if (this.thirdRepoListProp?.GetValue(this.config) is not IList list)
            {
                error = "读取 ThirdRepoList 失败";
                return 0;
            }

            foreach (var item in list)
            {
                if (item is null)
                {
                    continue;
                }

                var url = this.repoItemUrlProp?.GetValue(item) as string;
                if (url is null || !set.Contains(url))
                {
                    continue;
                }

                if ((this.repoItemEnabledProp?.GetValue(item) as bool? ?? false) != enabled)
                {
                    this.repoItemEnabledProp?.SetValue(item, enabled);
                    changed++;
                }
            }
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        return changed;
    }

    /// <summary>从列表里删除若干 URL；返回实际删除的条数。</summary>
    public int Remove(IEnumerable<string> urls, out string? error)
    {
        error = null;
        var set = new HashSet<string>(urls, StringComparer.Ordinal);
        var removed = 0;

        if (!this.EnsureConfig(out error))
        {
            return 0;
        }

        try
        {
            if (this.thirdRepoListProp?.GetValue(this.config) is not IList list)
            {
                error = "读取 ThirdRepoList 失败";
                return 0;
            }

            for (var i = list.Count - 1; i >= 0; i--)
            {
                var item = list[i];
                var url = item is null ? null : this.repoItemUrlProp?.GetValue(item) as string;
                if (url is null || !set.Contains(url))
                {
                    continue;
                }

                list.RemoveAt(i);
                removed++;
            }
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        return removed;
    }

    /// <summary>把若干条目放回列表（撤回删除用），按原索引从前往后插入。</summary>
    public int Insert(IEnumerable<UndoEntry> entries, out string? error)
    {
        error = null;
        var inserted = 0;

        if (!this.EnsureConfig(out error))
        {
            return 0;
        }

        try
        {
            if (this.thirdRepoListProp?.GetValue(this.config) is not IList list)
            {
                error = "读取 ThirdRepoList 失败";
                return 0;
            }

            var itemType = this.configType!.Assembly.GetType("Dalamud.Configuration.ThirdPartyRepoSettings")
                           ?? list.GetType().GetGenericArguments().FirstOrDefault();
            if (itemType is null)
            {
                error = "找不到 ThirdPartyRepoSettings 类型";
                return 0;
            }

            var existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in list)
            {
                if (item is not null && this.repoItemUrlProp?.GetValue(item) is string u)
                {
                    existing.Add(u);
                }
            }

            foreach (var entry in entries.OrderBy(x => x.Index))
            {
                if (existing.Contains(entry.Url))
                {
                    continue;
                }

                var item = Activator.CreateInstance(itemType);
                if (item is null)
                {
                    continue;
                }

                this.repoItemUrlProp?.SetValue(item, entry.Url);
                this.repoItemEnabledProp?.SetValue(item, entry.IsEnabled);

                var index = Math.Clamp(entry.Index, 0, list.Count);
                list.Insert(index, item);
                existing.Add(entry.Url);
                inserted++;
            }
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        return inserted;
    }

    /// <summary>让卫月把当前配置写回磁盘（下一帧生效）。</summary>
    public bool Save(out string? error)
    {
        error = null;
        if (!this.EnsureConfig(out error))
        {
            return false;
        }

        try
        {
            this.queueSave?.Invoke(this.config, null);
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>触发卫月重新抓取全部仓库（异步，不阻塞界面）。</summary>
    public bool TriggerReload(out string? error)
    {
        error = null;
        if (!this.EnsureManager(out error))
        {
            return false;
        }

        try
        {
            var result = this.setReposMethod?.Invoke(this.manager, [true]);
            this.LastRefreshTask = result as Task;
            return result is not null;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// 备份：写出完整仓库列表 + 复制一份 dalamudConfig.json。
    /// 返回备份文件路径。
    /// </summary>
    public string BackupRepos(out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(this.backupDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var file = Path.Combine(this.backupDirectory, $"repo-backup-{stamp}.json");

            var items = this.ReadAll(out var readError);
            if (readError != null)
            {
                error = readError;
            }

            var payload = new
            {
                time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                note = "FireGaze 删除仓库前的自动备份：包含当时的完整第三方仓库列表（Url / IsEnabled）。",
                count = items.Count,
                list = items.Select(x => new { x.Url, x.IsEnabled }),
            };

            var json = JsonSerializer.Serialize(
                payload,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            File.WriteAllText(file, json, System.Text.Encoding.UTF8);

            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN",
                "dalamudConfig.json");
            if (File.Exists(configPath))
            {
                File.Copy(configPath, Path.Combine(this.backupDirectory, $"dalamudConfig.json.bak-{stamp}"), overwrite: true);
            }

            return file;
        }
        catch (Exception e)
        {
            error = e.Message;
            return string.Empty;
        }
    }

    private bool EnsureConfig(out string? error)
    {
        error = null;

        if (this.config is not null && this.thirdRepoListProp is not null)
        {
            return true;
        }

        try
        {
            var dalamud = typeof(IDalamudPluginInterface).Assembly;
            this.serviceOpenType ??= dalamud.GetType("Dalamud.Service`1", throwOnError: true);
            this.configType ??= dalamud.GetType("Dalamud.Configuration.Internal.DalamudConfiguration", throwOnError: true);

            var serviceType = this.serviceOpenType!.MakeGenericType(this.configType!);
            var get = serviceType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            this.config = get?.Invoke(null, null);

            if (this.config is null)
            {
                error = "获取 DalamudConfiguration 失败";
                return false;
            }

            this.thirdRepoListProp = this.configType!.GetProperty("ThirdRepoList", BindingFlags.Public | BindingFlags.Instance);
            this.queueSave = this.configType.GetMethod("QueueSave", BindingFlags.Public | BindingFlags.Instance);

            var itemType = dalamud.GetType("Dalamud.Configuration.ThirdPartyRepoSettings", throwOnError: true);
            this.repoItemUrlProp = itemType!.GetProperty("Url", BindingFlags.Public | BindingFlags.Instance);
            this.repoItemEnabledProp = itemType.GetProperty("IsEnabled", BindingFlags.Public | BindingFlags.Instance);

            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private bool EnsureManager(out string? error)
    {
        error = null;

        if (this.manager is not null && this.setReposMethod is not null)
        {
            return true;
        }

        try
        {
            var dalamud = typeof(IDalamudPluginInterface).Assembly;
            this.serviceOpenType ??= dalamud.GetType("Dalamud.Service`1", throwOnError: true);
            this.managerType ??= dalamud.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: true);

            var serviceType = this.serviceOpenType!.MakeGenericType(this.managerType!);
            var get = serviceType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            this.manager = get?.Invoke(null, null);

            if (this.manager is null)
            {
                error = "获取 PluginManager 失败";
                return false;
            }

            this.setReposMethod = this.managerType!.GetMethod(
                "SetPluginReposFromConfigAsync",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                [typeof(bool)],
                null);

            if (this.setReposMethod is null)
            {
                error = "找不到 SetPluginReposFromConfigAsync";
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }
}
