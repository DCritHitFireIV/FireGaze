using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FireGaze.Discovery;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

internal sealed partial class DiscoveryTab
{
    private void SetStatus(string message, bool isError)
    {
        statusMessage = message;
        statusIsError = isError;
    }

    private static string FieldLabel(string field) => field switch
    {
        "Name" => "插件名",
        "Punchline" => "一行简介",
        _ => "插件详情",
    };

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
            }

            statsFetchedAt = DateTime.UtcNow;
            rebuildPending = true;
            rebuildRepoPending = true;
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
            }
        });
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    /// <summary>
    ///     库链地址的短名（GitHub raw 地址显示成 owner/repo）。
    /// </summary>
    private static string RepoShort(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.Trim('/');
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && uri.Host.Contains("githubusercontent", StringComparison.OrdinalIgnoreCase))
            {
                var take = Math.Min(2, segments.Length);
                var start = Math.Max(0, segments.Length - take - 1);
                return string.Join("/", segments.Skip(start).Take(take));
            }

            return uri.Host;
        }
        catch
        {
            return UiHelpers.Shorten(url, 24);
        }
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }
}
