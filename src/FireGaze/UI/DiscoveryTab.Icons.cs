using System.Collections.Concurrent;
using Dalamud.Bindings.ImGui;
using FireGaze.Diagnostics;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

/// <summary>
///     插件发现：云库插件的图标——按视口排队下载（可见行 + 一小段预载余量），并行但限流，
///     下好的图落盘缓存（重开游戏不重下）、建好纹理后本页与插件安装器都能用。
/// </summary>
/// <remarks>
///     2026-09-18 的卡死教训：绝不能在绘制路径里把几百个下载/纹理任务一次撒出去。
///     这里的每个动作都经过三道闸：视口入队（按内部名去重）→ 每 100ms 最多发动一个下载
///     （在飞 ≤ <see cref="DiscoverIconConcurrency" />）→ 完成回调只入队，纹理在 UI 线程建。
///     用户从列表头一路滚到尾时，队列会自然积累到「看过的都下」，但发动速率恒定。
/// </remarks>
internal sealed partial class DiscoveryTab
{
    private const int DiscoverIconConcurrency = 6;
    private const int DiscoverIconKickMs = 100;
    private const int DiscoverIconMarginAbove = 3;
    private const int DiscoverIconMarginBelow = 8;

    /// <summary>等着下的图标（去重靠 <see cref="iconQueued" />）。</summary>
    private readonly Queue<TranslationIndexEntry> iconWaitQueue = new();

    /// <summary>已经进过队列 / 在飞 / 已失败不再试的条目。</summary>
    private readonly HashSet<string> iconQueued = new(StringComparer.Ordinal);

    /// <summary>下载失败后过几分钟才重排（网络瞬断不把图标钉死在字母块上）。</summary>
    private readonly Dictionary<string, DateTime> iconRetryAt = new(StringComparer.Ordinal);

    /// <summary>后台下载完成的回调（后台 → UI 线程）。</summary>
    private readonly ConcurrentQueue<(TranslationIndexEntry Entry, bool Ok)> iconDownloadResults = new();

    private int iconInFlight;
    private DateTime nextIconKick = DateTime.MinValue;

    /// <summary>本帧可见（含预载余量）的插件进下载队列；只排队，不动手。</summary>
    private void NoteVisibleForIcons(int displayStart, int displayEnd)
    {
        var from = Math.Max(0, displayStart - DiscoverIconMarginAbove);
        var to = Math.Min(filtered.Count, displayEnd + DiscoverIconMarginBelow);
        for (var i = from; i < to; i++)
        {
            var entry = filtered[i];
            if (iconHandles.ContainsKey(entry.InternalName)
                || iconMisses.Contains(entry.InternalName)
                || iconQueued.Contains(entry.InternalName))
            {
                continue;
            }

            if (iconRetryAt.TryGetValue(entry.InternalName, out var retryAt))
            {
                if (DateTime.UtcNow < retryAt)
                {
                    continue;
                }

                iconRetryAt.Remove(entry.InternalName);
            }

            if (!entry.DeclaresIcon || string.IsNullOrWhiteSpace(entry.IconURL))
            {
                iconMisses.Add(entry.InternalName);
                continue;
            }

            iconQueued.Add(entry.InternalName);
            iconWaitQueue.Enqueue(entry);
        }
    }

    /// <summary>每帧推进：收获完成的下载 → 建纹理；然后按节奏发动新的下载。</summary>
    private void TickDiscoveryIcons()
    {
        // 1) 收获：完成 → 建纹理（界面线程；解码在卫月内部异步做，句柄下一帧起可画）
        while (iconDownloadResults.TryDequeue(out var result))
        {
            iconInFlight = Math.Max(0, iconInFlight - 1);
            if (result.Ok)
            {
                plugin.Icons.EnsureTexture(ToIconEntry(result.Entry));
            }
            else
            {
                // 失败不钉死：两分钟后可视时再排一次（不重试次数无限堆，只有看得见的才会重排）
                iconQueued.Remove(result.Entry.InternalName);
                iconRetryAt[result.Entry.InternalName] = DateTime.UtcNow.AddMinutes(2);
                ActivityLog.Debug("插件发现", $"图标下载失败，稍后重试：{result.Entry.InternalName}");
            }
        }

        // 2) 发动：每 100ms 最多一个，在飞受限
        if (iconWaitQueue.Count == 0
            || iconInFlight >= DiscoverIconConcurrency
            || DateTime.UtcNow < nextIconKick)
        {
            return;
        }

        var entry = iconWaitQueue.Dequeue();
        nextIconKick = DateTime.UtcNow.AddMilliseconds(DiscoverIconKickMs);

        var target = ToIconEntry(entry);
        if (plugin.Icons.Has(target))
        {
            // 盘上 / 内存里已经有：建纹理就行，不重新下载
            plugin.Icons.EnsureTexture(target);
            return;
        }

        iconInFlight++;
        var url = entry.IconURL!;
        _ = Task.Run(async () =>
        {
            var result = await IconDownloader.FetchAsync(url, CancellationToken.None).ConfigureAwait(false);
            var ok = result.Bytes is { Length: > 0 } bytes
                     && IconCache.LooksLikeImage(bytes, result.ContentType)
                     && plugin.Icons.SaveDownloaded(target, bytes, result.ContentType);
            iconDownloadResults.Enqueue((entry, ok));
        });
    }

    /// <summary>刷新云库/词表后：图标状态一并重置（失败过的也再给一次机会）。</summary>
    private void ResetIconQueue()
    {
        iconWaitQueue.Clear();
        iconQueued.Clear();
        iconRetryAt.Clear();
        iconMisses.Clear();
    }

    private static InstalledPluginEntry ToIconEntry(TranslationIndexEntry entry) => new()
    {
        InternalName = entry.InternalName,
        DisplayName = entry.DisplayName,
        IconURL = entry.IconURL,
        RawPlugin = entry.RawPlugin!,
        Manifest = entry.Manifest,
        IsThirdParty = true,
    };
}
