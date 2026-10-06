using FireGaze.Diagnostics;
using FireGaze.Discovery;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>插件发现：状态行、统计拉取/重试、库链辅助操作。</summary>
internal sealed partial class DiscoveryTab
{
    private void SetStatus(string message, bool isError)
    {
        statusMessage = message;
        statusIsError = isError;
    }

    /// <summary>
    ///     刷新云库：从 GitHub 重拉词表（带防倒退），成功后由 DrawCore 全量重建列表。
    ///     用户 2026-10-07：投稿的库链有没有上云，点一下就能确认。
    /// </summary>
    private void StartCloudRefresh()
    {
        if (refreshInFlight)
        {
            return;
        }

        refreshInFlight = true;
        SetStatus("正在从 GitHub 拉取最新云库…", isError: false);
        _ = Task.Run(async () =>
        {
            try
            {
                var (ok, message) = await plugin.UpdateTranslationTableAsync().ConfigureAwait(false);
                refreshResult = (ok, message);
            }
            catch (Exception e)
            {
                refreshResult = (false, e.GetType().Name);
            }
        });
    }

    /// <summary>排序档位的中文名（下拉框用）。</summary>
    internal static string SortLabel(DiscoverySortMode mode) => mode switch
    {
        DiscoverySortMode.WeeklyLikes => "每周点赞排行",
        DiscoverySortMode.Recommends => "推荐排行",
        DiscoverySortMode.Updated => "最新更新",
        DiscoverySortMode.Random => "随机",
        DiscoverySortMode.Author => "作者名称",
        _ => "插件名称",
    };

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    /// <summary>
    ///     拉一次中继统计（打开页签时 / 每 10 分钟）：成功就更新排序与赞数；失败退到上次缓存。
    /// </summary>
    private void EnsureDiscoveryStats()
    {
        if (statsTask is not null)
        {
            if (!statsTask.IsCompleted)
            {
                return;
            }

            var finished = statsTask;
            statsTask = null;
            var result = finished.Status == TaskStatus.RanToCompletion ? finished.Result : null;
            if (result is not null)
            {
                discoveryStats = result;
                discoveryState.SaveStats(result);
                statsFetchFailed = false;
            }
            else
            {
                statsFetchFailed = true;
                discoveryStats ??= discoveryState.CachedStats;
                ActivityLog.Debug("插件发现", "统计拉取失败（中继不可达或未部署 /plugin-stats）；用缓存兜底");
            }

            statsFetchedAt = DateTime.UtcNow;
            rebuildPending = true;
            return;
        }

        discoveryStats ??= discoveryState.CachedStats;
        if (DateTime.UtcNow - statsFetchedAt < TimeSpan.FromMinutes(10))
        {
            return;
        }

        statsFetchedAt = DateTime.UtcNow;
        statsTask = Task.Run(() => DiscoveryRelay.FetchStatsAsync(CancellationToken.None));
    }

    /// <summary>把后台点赞回来的云端真值写进统计（只在 UI 线程改字典）。</summary>
    private void DrainLikeResults()
    {
        while (likeResults.TryDequeue(out var like))
        {
            if (discoveryStats is null)
            {
                continue;
            }

            discoveryStats.TotalLikes[like.Plugin] = like.Total;
            discoveryStats.WeeklyLikes[like.Plugin] = like.Weekly;
            rebuildPending = true;
        }
    }

    /// <summary>把失败的上报（点赞 / 加库）每隔一会儿重试一条——不谎报成功。</summary>
    private void RetryPendingReport()
    {
        if (DateTime.UtcNow - lastPendingRetry < TimeSpan.FromSeconds(15))
        {
            return;
        }

        lastPendingRetry = DateTime.UtcNow;
        var action = discoveryState.PeekPending();
        if (action is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var ok = action.Type == "add"
                ? await DiscoveryRelay.ReportAddsAsync([action.Plugin], CancellationToken.None).ConfigureAwait(false)
                : (await DiscoveryRelay.LikeAsync(action.Plugin, CancellationToken.None).ConfigureAwait(false)).Ok;
            if (ok)
            {
                discoveryState.CompletePending(action);
                ActivityLog.Info("插件发现", $"补报成功（{action.Type}：{action.Plugin}）");
            }
        });
    }

    /// <summary>云端语料里有没有这条库链（onlyKnown = 只看「已经在你本机库里」的那些）。</summary>
    private bool RepoExistsInIndex(string? normalizedURL, bool onlyKnown)
    {
        if (string.IsNullOrEmpty(normalizedURL) || index is not { Available: true })
        {
            return false;
        }

        foreach (var entry in index.All)
        {
            if (entry.RepositoryURL is not { Length: > 0 } url)
            {
                continue;
            }

            if (onlyKnown && !entry.RepositoryKnown)
            {
                continue;
            }

            if (string.Equals(InstalledPluginsIndex.NormalizeRepositoryURL(url), normalizedURL, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>本机投过、但云端词表还没带上这条库链的地址（界面上显示「等收录」）。</summary>
    private List<string> SubmittedButNotInCloud()
    {
        var result = new List<string>();
        foreach (var url in discoveryState.SubmittedRepos.Keys)
        {
            if (!RepoExistsInIndex(url, onlyKnown: false))
            {
                result.Add(url);
            }
        }

        return result;
    }

    /// <summary>把一条库直接加进本机列表（先自动备份），成功后上报推荐数、并就地翻新行的库状态。</summary>
    private void AddRepoFromRow(string url)
    {
        var added = plugin.AddThirdPartyRepository(url, out var message);
        SetStatus(added ? "已把这条库链加进你的插件列表" : message, !added);
        if (added)
        {
            MarkRepoState(url, enabled: true);
            ReportRepoAdds([url]);
        }

        rebuildPending = true;
    }

    /// <summary>
    ///     加库/启用成功后就地翻新该库链所有行的「已知/启用」状态：
    ///     不等一轮卫月仓库重载，按钮立即变成 `[已在库]`（二轮测评：短窗口内不反馈）。
    /// </summary>
    private void MarkRepoState(string url, bool enabled)
    {
        if (index is not { Available: true })
        {
            return;
        }

        var normalized = InstalledPluginsIndex.NormalizeRepositoryURL(url);
        foreach (var entry in index.All)
        {
            if (entry.RepositoryURL is { Length: > 0 } repo
                && string.Equals(InstalledPluginsIndex.NormalizeRepositoryURL(repo), normalized, StringComparison.Ordinal))
            {
                entry.RepositoryKnown = true;
                entry.RepositoryEnabled = enabled;
            }
        }

        rebuildPending = true;
    }

    /// <summary>最近一次「加库」的撤回记录（不是加库就不给撤）——行内加库也要能反悔。</summary>
    private UndoRecord? LastAddRecord()
    {
        var history = plugin.Config.UndoHistory;
        if (history.Count == 0)
        {
            return null;
        }

        var last = history[^1];
        return last.Action == "add" ? last : null;
    }

    private void UndoLastAdd()
    {
        var ok = plugin.TryUndoLast(out var message);
        SetStatus(message, !ok);
        rebuildPending = true;
    }

    /// <summary>重新启用一条已停用的库（保留链接，让卫月重新抓它的插件）。</summary>
    private void EnableRepoFromRow(string url)
    {
        plugin.Repos.SetEnabled([url], true, out _);
        plugin.Repos.Save(out _);
        plugin.Repos.TriggerReload(out _);
        plugin.TrackFirstSeen();
        MarkRepoState(url, enabled: true);
        SetStatus("已启用这条库；卫月会重新抓取它的插件", isError: false);
        rebuildPending = true;
    }

    /// <summary>
    ///     把「刚从云端加进自己库」的库链按插件上报推荐数（用户 2026-10-06 定：按插件不按库）。
    ///     先排队后发送：发送失败会留在待重试里，成功后清掉。
    /// </summary>
    private void ReportRepoAdds(IReadOnlyCollection<string> urls)
    {
        if (urls.Count == 0 || index is not { Available: true })
        {
            return;
        }

        var wanted = new HashSet<string>(urls.Select(InstalledPluginsIndex.NormalizeRepositoryURL), StringComparer.Ordinal);
        var names = index.All
            .Where(x => x.RepositoryURL is { Length: > 0 } url
                        && wanted.Contains(InstalledPluginsIndex.NormalizeRepositoryURL(url)))
            .Select(x => x.InternalName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            return;
        }

        discoveryState.MarkAdds(names);
        _ = Task.Run(async () =>
        {
            if (await DiscoveryRelay.ReportAddsAsync(names, CancellationToken.None).ConfigureAwait(false))
            {
                discoveryState.CompleteAdds(names);
            }
            else
            {
                ActivityLog.Warning("插件发现", $"推荐上报失败（{names.Count} 个插件）；已排队自动重试");
            }
        });
    }
}
