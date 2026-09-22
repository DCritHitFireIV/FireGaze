using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

internal sealed partial class RepoAuditTab
{
    /// <summary>
    ///     第一步：只查不下载——列出「声明了图标、但图标还没缓存下来」的已装插件。
    /// </summary>
    private void RunIconCheck()
    {
        var index = installedIndex;
        if (index is not { Available: true })
        {
            return;
        }

        Plugin.Log.Information($"[FireGaze] 用户点击：检查缺图标（已装 {index.All.Count} 个插件）");

        var cached = 0;
        var fromDisk = 0;
        var noAddress = 0;

        iconMissing.Clear();
        iconDead.Clear();

        foreach (var entry in index.All)
        {
            if (!entry.DeclaresIcon)
            {
                noAddress++;
                continue;
            }

            // 先看我们自己的落盘缓存（重开游戏后也能命中），再看卫月内存里的
            if (plugin.Icons.TryGetHandle(entry, out var cachedHandle) && !cachedHandle.IsNull)
            {
                iconHandles[entry.InternalName] = cachedHandle;
                fromDisk++;
                cached++;
                continue;
            }

            // 只读检查：直接看卫月的图标缓存，不触发任何下载
            if (PluginIconLookup.TryPeekHandle(entry, out var handle) && !handle.IsNull)
            {
                iconHandles[entry.InternalName] = handle;
                cached++;
                continue;
            }

            iconMissing.Add(entry);
        }

        iconCheckDone = true;

        var names = string.Join("、", iconMissing.Take(6).Select(x => x.DisplayName));
        var summary = $"图标检查：已安装 {index.All.Count} 个插件 ｜ 图标已有 {cached} 个"
                      + (fromDisk > 0 ? $"，其中本地缓存 {fromDisk} 个" : string.Empty)
                      + $" ｜ 缺图标 {iconMissing.Count} 个"
                      + (noAddress > 0 ? $" ｜ 另有 {noAddress} 个没提供图标地址" : string.Empty)
                      + (iconMissing.Count > 0
                          ? $" ｜ {names}" + (iconMissing.Count > 6 ? $" 等 {iconMissing.Count} 个 · 悬停看全部" : string.Empty)
                          : string.Empty)
                      + "。下好的图标会存在本地，重开游戏不用重下。";

        SetStatus(summary, false);
        iconMissingNames = iconMissing.Select(x => x.DisplayName).ToList();
    }

    /// <summary>
    ///     第二步：用户点了下载——走我们自己的下载通道（限并发，下完写进本地缓存）。
    /// </summary>
    private void StartIconDownload()
    {
        if (iconMissing.Count == 0)
        {
            SetStatus("图标检查：没有需要下载的图标。", false);
            return;
        }

        Plugin.Log.Information(
            $"[FireGaze] 用户点击：下载图标（待下 {iconMissing.Count} 个，并发 {IconConcurrency}）");

        iconWaiting.Clear();
        iconWaiting.AddRange(iconMissing);
        iconInFlight.Clear();
        while (iconReady.TryDequeue(out _))
        {
        }

        while (iconFailed.TryDequeue(out _))
        {
        }

        iconDownloadTotal = iconWaiting.Count;
        iconDownloadRequested = 0;
        iconDownloadGot = 0;
        iconDownloadFailed = 0;
        iconDownloadStartedAt = DateTime.Now;
        iconDownloadDeadline = DateTime.Now.AddSeconds(120);
        iconTailDeadline = default;
        nextIconKick = DateTime.MinValue;
        iconDownloadRunning = true;
        iconDownloadLine = $"图标下载：已请求 0/{iconDownloadTotal} · 拿到 0";
        SetStatus(null, false);
    }

    /// <summary>
    ///     下载推进：后台线程只负责下和写盘（结果丢进队列），界面线程在这里取货、建纹理；
    ///     同时在飞不超过 <see cref="IconConcurrency"/> 个，每 <see cref="IconKickMs"/> 毫秒发动一个。
    /// </summary>
    private void TickIconDownload()
    {
        if (iconDeadReport is { } report)
        {
            iconDeadReport = null;
            foreach (var name in report)
            {
                iconDead.Add(name);
            }

            // 地址确认失效的别继续占着「下载图标（N）」的计数：再点也只会再失败一次
            iconMissing.RemoveAll(x => report.Contains(x.InternalName));
        }

        if (!iconDownloadRunning)
        {
            return;
        }

        // 0) 我们自己的下载：后台已写好盘 → 这里建纹理（界面线程）、计数
        var loadedAny = false;
        while (iconReady.TryDequeue(out var done))
        {
            iconInFlight.Remove(done);
            iconDownloadGot++;
            if (plugin.Icons.EnsureTexture(done))
            {
                Plugin.Log.Debug($"[FireGaze] 图标建纹理：{done.InternalName}");
            }

            loadedAny = true;
        }

        while (iconFailed.TryDequeue(out var bad))
        {
            iconInFlight.Remove(bad);
            iconDownloadFailed++;
        }

        if (loadedAny)
        {
            plugin.Icons.FlushIndex();
        }

        // 1) 轮询交给卫月下的（官方库插件）：拿到就移出
        for (var i = iconInFlight.Count - 1; i >= 0; i--)
        {
            var entry = iconInFlight[i];
            if (PluginIconLookup.TryPeekHandle(entry, out var handle) && !handle.IsNull)
            {
                iconHandles[entry.InternalName] = handle;
                iconInFlight.RemoveAt(i);
                iconDownloadGot++;
            }
        }

        // 2) 发动新的（一次一个，节奏交给 IconKickMs 控制）
        if (iconWaiting.Count > 0 && iconInFlight.Count < IconConcurrency && DateTime.Now >= nextIconKick)
        {
            var entry = iconWaiting[0];
            iconWaiting.RemoveAt(0);

            if (plugin.Icons.TryGetHandle(entry, out _))
            {
                iconDownloadGot++;   // 已在本地（刚下过 / 盘上有）
            }
            else if (entry.IsThirdParty && !string.IsNullOrWhiteSpace(entry.IconURL))
            {
                KickOurDownload(entry);
            }
            else
            {
                PluginIconLookup.TryGetHandle(entry, out _);   // 官方库插件：地址由卫月拼，交给它下
                iconInFlight.Add(entry);
            }

            iconDownloadRequested++;
            nextIconKick = DateTime.Now.AddMilliseconds(IconKickMs);
        }

        // 3) 收尾期：待发队列空了、只剩少数几个还在飞（且已经发过几个）时，只给 4 秒
        var remaining = iconWaiting.Count + iconInFlight.Count;
        var tail = iconWaiting.Count == 0
                   && iconInFlight.Count is > 0 and <= IconTailMax
                   && iconDownloadRequested >= IconTailMinRequested;

        if (tail)
        {
            if (iconTailDeadline == default)
            {
                iconTailDeadline = DateTime.Now + IconTailGrace;
                Plugin.Log.Debug(
                    $"[FireGaze] 图标下载收尾：只剩 {remaining} 个，最多再等 {IconTailGrace.TotalSeconds:0} 秒");
            }
        }
        else
        {
            iconTailDeadline = default;
        }

        var tailLeft = tail ? Math.Max(0, (iconTailDeadline - DateTime.Now).TotalSeconds) : 0;

        iconDownloadLine = $"图标下载：拿到 {iconDownloadGot}/{iconDownloadTotal}"
                                + (iconDownloadFailed > 0 ? $" · 失败 {iconDownloadFailed}" : string.Empty)
                                + (remaining > 0 ? $" · 还剩 {remaining}" : string.Empty)
                                + (tail ? $"，最多再等 {tailLeft:0} 秒" : string.Empty);

        if ((iconWaiting.Count == 0 && iconInFlight.Count == 0)
            || DateTime.Now >= iconDownloadDeadline
            || (tail && DateTime.Now >= iconTailDeadline))
        {
            FinishIconDownload();
        }
    }

    /// <summary>
    ///     发起一个图标下载（后台线程：下完写盘，结果丢队列）。
    /// </summary>
    private void KickOurDownload(InstalledPluginEntry entry)
    {
        iconInFlight.Add(entry);

        var url = entry.IconURL!;
        Plugin.Log.Debug($"[FireGaze] 图标下载开始：{entry.InternalName} ← {url}");

        _ = Task.Run(async () =>
        {
            var result = await IconDownloader.FetchAsync(url, CancellationToken.None).ConfigureAwait(false);

            if (result.Bytes is { Length: > 0 } bytes && IconCache.LooksLikeImage(bytes, result.ContentType))
            {
                if (plugin.Icons.SaveDownloaded(entry, bytes, result.ContentType))
                {
                    Plugin.Log.Debug($"[FireGaze] 图标下载完成：{entry.InternalName}（{bytes.Length} 字节，{result.ContentType}）");
                    iconReady.Enqueue(entry);
                    return;
                }

                Plugin.Log.Debug($"[FireGaze] 图标写盘失败：{entry.InternalName}");
            }
            else
            {
                Plugin.Log.Debug(
                    $"[FireGaze] 图标下载失败：{entry.InternalName}（HTTP {result.Status}，{result.Error ?? "不是图片"}）");
            }

            iconFailed.Enqueue(entry);
        });
    }

    /// <summary>
    ///     收尾：报结果，并对仍未拿到的图标地址探一次可达性。
    ///     三类分别是：作者没给地址（补不了）、没下完（可再点一次）、地址已失效（要作者改）。
    /// </summary>
    private void FinishIconDownload()
    {
        if (!iconDownloadRunning)
        {
            return;
        }

        iconDownloadRunning = false;
        plugin.Icons.FlushIndex();

        Plugin.Log.Information(
            $"[FireGaze] 图标下载结束：请求 {iconDownloadRequested} / 拿到 {iconDownloadGot} / 失败 {iconDownloadFailed}"
            + $" / 还在等 {iconWaiting.Count + iconInFlight.Count}；本地缓存共 {plugin.Icons.CachedCount} 个");

        // 已进本地缓存的从「缺图标」清单里拿掉：按钮上的数字立刻回到真实值
        iconMissing.RemoveAll(x => plugin.Icons.Has(x));

        var leftover = new List<InstalledPluginEntry>(iconWaiting.Count + iconInFlight.Count);
        leftover.AddRange(iconWaiting);
        leftover.AddRange(iconInFlight);
        iconWaiting.Clear();
        iconInFlight.Clear();

        var total = iconDownloadTotal;
        var got = iconDownloadGot;
        var failed = iconDownloadFailed;
        var seconds = (DateTime.Now - iconDownloadStartedAt).TotalSeconds;

        var noAddress = leftover.Count(x => x.IsThirdParty && string.IsNullOrWhiteSpace(x.IconURL));
        var checkable = leftover
            .Where(x => !(x.IsThirdParty && string.IsNullOrWhiteSpace(x.IconURL)))
            .ToList();

        var head = $"图标下载完成：拿到 {got} / {total} 个"
                   + (failed > 0 ? $" · 失败 {failed}" : string.Empty)
                   + (leftover.Count > 0 ? $" · 未完成 {leftover.Count}" : string.Empty)
                   + $"；用时 {seconds:0}s。已存的图标重开游戏不用重下。";

        if (checkable.Count == 0)
        {
            SetStatus(head, false);
            return;
        }

        SetStatus(head + " 正在确认那几个的图标地址…", false);

        var urls = checkable
            .Where(x => x.IsThirdParty && !string.IsNullOrWhiteSpace(x.IconURL))
            .Select(x => (Entry: x, URL: x.IconURL!))
            .DistinctBy(x => x.URL, StringComparer.Ordinal)
            .ToList();
        var official = checkable.Count - urls.Count;

        var version = statusVersion;

        _ = Task.Run(async () =>
        {
            var deadNames = new List<string>();
            var failed = 0;

            // 整体限时 8 秒：这是收尾确认，不值得让用户等一连串 18 秒超时
            using var probeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            foreach (var (entry, url) in urls)
            {
                if (probeBudget.IsCancellationRequested)
                {
                    failed++;
                    continue;
                }

                try
                {
                    var probe = await RepoScanner.ProbeURLAsync(url, probeBudget.Token).ConfigureAwait(false);
                    if (probe.Status is 404 or 410)
                    {
                        deadNames.Add(entry.InternalName);
                    }
                    else if (!probe.Ok)
                    {
                        failed++;
                    }
                }
                catch (OperationCanceledException)
                {
                    failed++;
                }
            }

            if (version != statusVersion)
            {
                return;   // 期间用户做了别的动作（停用 / 删除 / 撤回 / 扫描），别覆盖人家的确认消息
            }

            var parts = new List<string>(3);
            if (deadNames.Count > 0)
            {
                parts.Add($"{deadNames.Count} 个图标地址已失效，需要插件作者更新清单");
            }

            if (failed > 0)
            {
                parts.Add($"{failed} 个未完成，可以再点一次");
            }

            if (official > 0)
            {
                parts.Add($"{official} 个是官方库插件，稍后自己会下好");
            }

            iconDeadReport = deadNames;
            SetStatus(head + (parts.Count > 0 ? " " + string.Join("；", parts) + "。" : string.Empty), false);
        });
    }

    private static void DrawIconPlaceholder(InstalledPluginEntry entry, float size)
    {
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRect(
            start,
            start + new Vector2(size, size),
            ImGui.GetColorU32(new Vector4(0.45f, 0.45f, 0.45f, 1f)),
            4f);

        var initial = string.IsNullOrEmpty(entry.DisplayName) ? "?" : entry.DisplayName[..1].ToUpperInvariant();
        var textSize = ImGui.CalcTextSize(initial);
        drawList.AddText(
            start + ((new Vector2(size, size) - textSize) * 0.5f),
            ImGui.GetColorU32(UiHelpers.Muted),
            initial);

        ImGui.Dummy(new Vector2(size, size));
    }
}
