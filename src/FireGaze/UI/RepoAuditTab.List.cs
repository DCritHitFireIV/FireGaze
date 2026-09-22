using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

internal sealed partial class RepoAuditTab
{
    private string StatusTooltip(RepoAuditItem item) => item.Status switch
    {
        RepoStatus.Invalid =>
            "链接不合规：这个链接返回的不是仓库 JSON（常见原因：填了 GitHub 网页地址而不是 raw 地址）。\n"
            + "后果：该库的插件会全部加载不出来，还可能导致插件列表残缺或排版错乱。\n"
            + (item.Note ?? string.Empty),
        RepoStatus.Dead => "链接已失效（404 / 410）。\n" + (item.Note ?? string.Empty),
        RepoStatus.Unreachable =>
            "连接失败：超时 / 证书 / 服务器错误，不一定是死链（可能是网络问题）。\n"
            + "建议先「重新体检」确认，再决定是否停用。\n" + (item.Note ?? string.Empty),
        RepoStatus.Blocked => "服务器拒绝访问：可能是私有仓库、限流或需要登录。\n" + (item.Note ?? string.Empty),
        _ => item.Note ?? string.Empty,
    };

    private int ProblemCount() => problemCountCache;

    private void FilterRadio(string key, string label)
    {
        ImGui.SameLine();
        if (ImGui.RadioButton($"{label}###Filter-{key}", filter == key))
        {
            filter = key;
            snapshotDirty = true;
        }
    }

    private List<RepoAuditItem> Filtered()
    {
        // 缓存：筛选 / 排序 / 数据没变就不重算（体检进行中每 250ms 最多重算一次）
        var now = DateTime.Now;
        if (!snapshotDirty && now < snapshotNextAllowed)
        {
            return snapshotCache;
        }

        IEnumerable<RepoAuditItem> query = filter switch
        {
            "all" => items,
            "unreachable" => items.Where(x => x.Status is RepoStatus.Unreachable),
            "disabled" => items.Where(x => !x.IsEnabled),
            "ok" => items.Where(x => x.Status is RepoStatus.Ok or RepoStatus.Empty),
            _ => items.Where(x => x.IsProblem),
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(x => x.URL.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // 与健康度正交的第二个轴：只看装了插件的库 / 只看没装过插件的库（数据不可用时开关是禁用的）
        if (installedIndex is { Available: true })
        {
            if (onlyWithPlugins)
            {
                query = query.Where(x => x.InstalledCount > 0);
            }
            else if (onlyUnused)
            {
                query = query.Where(x => x.InstalledCount == 0);
            }
        }

        var result = SortItems(query);
        snapshotCache = result;
        snapshotDirty = false;
        snapshotNextAllowed = now.AddMilliseconds(scanning ? 250 : 0);
        return result;
    }

    /// <summary>
    ///     排序：默认按严重度（死链在最上，<c>UiHelpers.SeverityRank</c>）；列头可切换升/降；平序一律按 URL。
    ///     不可用（`—`）与「未记录」在升序里排最后。
    /// </summary>
    private List<RepoAuditItem> SortItems(IEnumerable<RepoAuditItem> query)
    {
        var list = query.ToList();

        Comparison<RepoAuditItem> primary = sortKey switch
        {
            "installed" => (a, b) => InstalledRank(a).CompareTo(InstalledRank(b)),
            "firstSeen" => (a, b) => string.Compare(FirstSeenRank(a), FirstSeenRank(b), StringComparison.Ordinal),
            "url" => (a, b) => string.Compare(a.URL, b.URL, StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => UiHelpers.SeverityRank(a.Status).CompareTo(UiHelpers.SeverityRank(b.Status)),
        };

        list.Sort((a, b) =>
        {
            var result = primary(a, b);
            if (result == 0)
            {
                result = string.Compare(a.URL, b.URL, StringComparison.OrdinalIgnoreCase);
            }

            return sortDescending ? -result : result;
        });

        return list;

        static int InstalledRank(RepoAuditItem item) => item.InstalledCount < 0 ? int.MaxValue : item.InstalledCount;

        static string FirstSeenRank(RepoAuditItem item) => string.IsNullOrEmpty(item.FirstSeen) ? "9999" : item.FirstSeen;
    }

    private void RecomputeCounters()
    {
        lock (gate)
        {
            okCount = items.Count(x => x.Status is RepoStatus.Ok or RepoStatus.Empty);
            deadCount = items.Count(x => x.Status == RepoStatus.Dead);
            invalidCount = items.Count(x => x.Status == RepoStatus.Invalid);
            blockedCount = items.Count(x => x.Status == RepoStatus.Blocked);
            unreachableCount = items.Count(x => x.Status == RepoStatus.Unreachable);
            unknownCount = items.Count(x => x.Status == RepoStatus.Unknown);
            disabledCount = items.Count(x => !x.IsEnabled);
            total = items.Count;
            problemCountCache = items.Count(x => x.IsProblem);
        }
    }

    /// <summary>
    ///     打开页面就把库链清单建出来（状态 = 未检查，不可勾选），不必先跑一次网络扫描：
    ///     「装了没」是离线数据，体检只负责回填健康度。
    /// </summary>
    private void EnsureList()
    {
        if (scanning || listBuilt || DateTime.Now < listRetryAfter)
        {
            return;
        }

        var repos = plugin.Repos.ReadAll(out var error);
        if (error is not null)
        {
            // 读配置失败时**绝不能每帧重试**（1241 个库的配置每帧解析一遍会把游戏拖死）
            listRetryAfter = DateTime.Now.AddSeconds(10);
            SetStatus("读取仓库列表失败：" + error, true);
            Plugin.Log.Warning("[FireGaze] 读取仓库列表失败（10 秒后重试）：" + error);
            return;
        }

        var list = repos
            .Where(x => !string.IsNullOrWhiteSpace(x.URL))
            .Select(entry => new RepoAuditItem
            {
                URL = entry.URL,
                NormalizedURL = InstalledPluginsIndex.NormalizeRepositoryURL(entry.URL),
                IsEnabled = entry.IsEnabled,
                Index = entry.Index,
                FirstSeen = plugin.GetFirstSeen(entry.URL),
            })
            .ToList();

        lock (gate)
        {
            items = list;

            // 还没体检过：默认看「全部」，否则「有问题的」会是空列表（旧行为是扫描后才建列表）
            if (list.Count > 0 && list.All(x => x.Status == RepoStatus.Unknown))
            {
                filter = "all";
            }

            total = list.Count;
        }

        RecomputeCounters();
        listBuilt = true;
        snapshotDirty = true;
        installedCountsDirty = true;
    }

    private void RefreshFromLive()
    {
        var live = plugin.Repos.ReadAll(out _);
        var liveUrls = new HashSet<string>(live.Select(x => x.URL), StringComparer.Ordinal);

        lock (gate)
        {
            var old = items.ToDictionary(x => x.URL, x => x, StringComparer.Ordinal);
            var list = new List<RepoAuditItem>(live.Count);

            foreach (var entry in live)
            {
                if (old.TryGetValue(entry.URL, out var item))
                {
                    item.IsEnabled = entry.IsEnabled;
                    item.Index = entry.Index;
                    list.Add(item);
                }
                else
                {
                    list.Add(new RepoAuditItem
                    {
                        URL = entry.URL,
                        NormalizedURL = InstalledPluginsIndex.NormalizeRepositoryURL(entry.URL),
                        IsEnabled = entry.IsEnabled,
                        Index = entry.Index,
                        FirstSeen = plugin.GetFirstSeen(entry.URL),
                    });
                }
            }

            items = list;
            selected.RemoveWhere(url => !liveUrls.Contains(url));
        }

        RecomputeCounters();
    }
}
