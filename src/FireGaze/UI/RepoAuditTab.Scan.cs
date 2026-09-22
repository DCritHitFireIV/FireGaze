using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

internal sealed partial class RepoAuditTab
{
    private void StartScan()
    {
        if (scanning)
        {
            return;
        }

        plugin.TrackFirstSeen();

        var repos = plugin.Repos.ReadAll(out var error);
        if (error is not null)
        {
            SetStatus("读取仓库列表失败：" + error, true);
            return;
        }

        var includeDisabled = plugin.Config.ScanIncludeDisabled;

        // 把上一轮的结论先搬过来：重新体检时行上不会闪成「未检查」，真正有变化的那几条会被新结果覆盖
        Dictionary<string, RepoAuditItem> previous;
        lock (gate)
        {
            previous = items.ToDictionary(x => x.URL, x => x, StringComparer.Ordinal);
        }

        var list = repos
            .Where(x => includeDisabled || x.IsEnabled)
            .Where(x => !string.IsNullOrWhiteSpace(x.URL))
            .Select(x =>
            {
                var item = new RepoAuditItem
                {
                    URL = x.URL,
                    NormalizedURL = InstalledPluginsIndex.NormalizeRepositoryURL(x.URL),
                    IsEnabled = x.IsEnabled,
                    Index = x.Index,
                    FirstSeen = plugin.GetFirstSeen(x.URL),
                };

                if (previous.TryGetValue(x.URL, out var old) && old.Status != RepoStatus.Unknown)
                {
                    item.Status = old.Status;
                    item.HTTPStatus = old.HTTPStatus;
                    item.Note = old.Note;
                    item.PluginCount = old.PluginCount;
                    item.DroppedCount = old.DroppedCount;
                    item.Channel = old.Channel;
                    item.CheckedUTC = old.CheckedUTC;
                }

                return item;
            })
            .ToList();

        if (list.Count == 0)
        {
            SetStatus("仓库列表是空的（或者都被排除了）。", true);
            return;
        }

        lock (gate)
        {
            items = list;
            selected.Clear();
            done = 0;
            total = list.Count;
            okCount = deadCount = invalidCount = blockedCount = 0;
            unreachableCount = disabledCount = unknownCount = 0;
            scanning = true;
            filter = "problems";
            sortKey = "status";
            sortDescending = false;
            resetSortRequested = true;
            listBuilt = true;
        }

        snapshotDirty = true;
        installedCountsDirty = true;

        SetStatus($"开始体检 {list.Count} 个仓库…", false);
        Plugin.Log.Information($"[FireGaze] 开始体检：{list.Count} 个仓库（含已停用 = {(includeDisabled ? "是" : "否")}）");
        var startedAt = DateTime.UtcNow;

        var cts = new CancellationTokenSource();
        cancellation = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await RepoScanner.ScanAsync(
                    list,
                    item =>
                    {
                        // 只默认勾选「死链 + 内容不合规」；连接失败可能是网络问题，不默认纳入
                        if (item.IsAutoSelected)
                        {
                            lock (gate)
                            {
                                selected.Add(item.URL);
                            }
                        }
                    },
                    progress =>
                    {
                        lock (gate)
                        {
                            done = progress.Done;
                            total = progress.Total;
                            okCount = progress.Ok;
                            deadCount = progress.Dead;
                            invalidCount = progress.Invalid;
                            blockedCount = progress.Blocked;
                            unreachableCount = progress.Unreachable;
                        }
                    },
                    cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SetStatus("扫描已取消。", false);
            }
            catch (Exception e)
            {
                SetStatus("扫描出错：" + e.Message, true);
            }
            finally
            {
                lastScanDuration = DateTime.UtcNow - startedAt;

                int dead, invalid, unreachable;
                lock (gate)
                {
                    scanning = false;
                    snapshotDirty = true;
                    dead = deadCount;
                    invalid = invalidCount;
                    unreachable = unreachableCount;
                }

                if (statusMessage?.StartsWith("开始体检") == true)
                {
                    statusMessage =
                        $"体检完成（用时 {lastScanDuration.TotalSeconds:0}s）：已自动勾选 {dead + invalid} 个死链/不合规项"
                        + (unreachable > 0 ? $"；另有 {unreachable} 个「连接失败」需人工确认（可能是网络问题，未勾选）。" : "。");
                    statusIsError = false;
                }

                plugin.Config.LastScanUTC = DateTime.UtcNow;
                foreach (var item in list)
                {
                    item.FirstSeen = plugin.GetFirstSeen(item.URL) ?? item.FirstSeen;
                }

                plugin.TrackFirstSeen();
                plugin.SaveConfig();
                RecomputeCounters();

                Plugin.Log.Information(
                    $"[FireGaze] 体检完成：用时 {lastScanDuration.TotalSeconds:0}s ｜ 共 {total} 个仓库 ｜ "
                    + $"可用 {okCount} · 死链 {deadCount} · 不合规 {invalidCount} · 拒绝 {blockedCount} · "
                    + $"连接失败 {unreachableCount} · 未检查 {unknownCount}");
            }
        });
    }
}
