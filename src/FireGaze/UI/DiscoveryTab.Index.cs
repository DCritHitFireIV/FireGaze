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
    /// <summary>
    ///     索引的生命周期：建一次 / 收结果 / 卫月刷新插件库后自动重建。
    ///
    ///     为什么要盯着「卫月刷新完了没」：卫月重载插件库时会把每个仓库的清单先清空、再逐个填回，
    ///     刷新中途抓到的快照会大量漏插件（2026-10-06 实例：一次刷新刚开始 0.7 秒时抓到快照，
    ///     1743 条词表只对上 98 个插件；当时这份快照还不会自动更新，整个会话都停在 98 个）。
    /// </summary>
    private void EnsureIndex()
    {
        // null = 反射读不到，按「已完成」处理，别让窗口永远停在等待上
        var reposReady = TranslationIndex.IsReposReady() ?? true;

        // ---- 上一次建索引跑完了：收结果 ----
        if (buildTask is not null)
        {
            if (!buildTask.IsCompleted)
            {
                return;
            }

            var finished = buildTask;
            buildTask = null;
            index = finished.Status == TaskStatus.RanToCompletion ? finished.Result : null;
            rebuildPending = true;
            rebuildRepoPending = true;
            if (index is { Available: false })
            {
                retryAfter = DateTime.UtcNow.AddSeconds(10);
            }
            else if (index is { Available: true })
            {
                // 建的时候卫月正在刷新（或到现在还没刷完）→ 这份快照可能不全，刷新完重建
                indexMayBePartial = buildStartedWhileReposBusy || !reposReady;
            }

            return;
        }

        if (index is { Available: true })
        {
            if (!indexMayBePartial || !reposReady)
            {
                return;
            }

            // 刷新完了：丢掉可能不完整的快照，重建一份
            Plugin.Log.Debug("[FireGaze] 卫月插件库刷新完成：重建参与翻译索引");
            indexMayBePartial = false;
            index = null;
            rebuildPending = true;
            rebuildRepoPending = true;
        }

        if (DateTime.UtcNow < retryAfter)
        {
            return;
        }

        // ---- 卫月正在刷新插件库：先等它读完，别把「清空再填回」的中间态拍下来；
        //      等太久（网络慢、刷新一直不结束）就照建，反正刷新完会再重建 ----
        if (!reposReady)
        {
            waitingForReposSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - waitingForReposSince.Value < TimeSpan.FromSeconds(5))
            {
                return;
            }
        }
        else
        {
            waitingForReposSince = null;
        }

        buildStartedWhileReposBusy = !reposReady;
        var table = plugin.SnapshotTable();

        // 本机已配置的库链（含停用）：用来给云端词表里的插件标「已启用 / 已停用 / 未加入」，
        // 「加库」也靠它知道哪些库还没加过。读不到就传 null（界面不提供加库判断）。
        var localRepositories = plugin.Repos.ReadAll(out var repoError);
        buildTask = Task.Run(() => TranslationIndex.Build(table, repoError is null ? localRepositories : null));
    }

    private void RebuildFiltered()
    {
        if (!rebuildPending)
        {
            return;
        }

        rebuildPending = false;
        filtered.Clear();

        if (index is not { Available: true })
        {
            return;
        }

        var query = search.Trim().ToLowerInvariant();

        foreach (var entry in index.All)
        {
            if (hideOfficial && entry.IsOfficial)
            {
                continue;
            }

            if (!plugin.Config.ContributeShowDisabled)
            {
                // 只看已启用的库；「还没加进来」的库属于云端语料，一直都在
                // （不然发现不了、也没法加）
                if (entry.RepositoryKnown && !entry.RepositoryEnabled)
                {
                    continue;
                }
            }

            var pass = stateFilter switch
            {
                StateFilter.Missing => entry.State == "missing",
                StateFilter.Machine => entry.State == "machine",
                StateFilter.User => entry.HasUserTranslation,
                StateFilter.Review => entry.HasReview,
                _ => true,
            };

            if (!pass)
            {
                continue;
            }

            // 全字段搜索（插件名/作者/简介/详情，原文+译文；SearchBlob 里都拼好了）
            if (query.Length > 0 && !entry.SearchBlob.Contains(query, StringComparison.Ordinal))
            {
                continue;
            }

            filtered.Add(entry);
        }

        ApplyDiscoverySort();
    }

    /// <summary>
    ///     按当前排序档位重排筛选结果；随机档用本机生成的乱序快照（一次打开/换一批内稳定）。
    /// </summary>
    private void ApplyDiscoverySort()
    {
        if (sortMode == DiscoverySortMode.Random)
        {
            foreach (var entry in filtered)
            {
                if (!shuffleOrder.ContainsKey(entry.InternalName))
                {
                    shuffleOrder[entry.InternalName] = shuffleSeed++;
                }
            }
        }

        sortContext ??= new DiscoverySortContext();
        sortContext.Mode = sortMode;
        sortContext.WeeklyLikes = discoveryStats?.WeeklyLikes ?? [];
        sortContext.Recommends = discoveryStats?.Recommends ?? [];
        sortContext.Shuffle = shuffleOrder;
        DiscoverySort.Apply(filtered, sortContext);
    }

    /// <summary>
    ///     仓库视图的分组：按库链把筛选后的插件归类（官方主库单独一组）。
    /// </summary>
    private void RebuildRepoGroups()
    {
        if (!rebuildRepoPending)
        {
            return;
        }

        rebuildRepoPending = false;
        repoGroups.Clear();

        RebuildFiltered();
        foreach (var plugin in filtered)
        {
            var key = plugin.IsOfficial ? "\u0000official" : plugin.RepositoryURL ?? string.Empty;
            var group = repoGroups.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal));
            if (group is null)
            {
                group = new RepoGroup
                {
                    Key = key,
                    URL = plugin.IsOfficial ? string.Empty : key,
                    Short = plugin.IsOfficial ? "官方主库" : key.Length == 0 ? "来源未知" : RepoShort(key),
                    Known = plugin.IsOfficial || plugin.RepositoryKnown,
                    Enabled = plugin.IsOfficial || plugin.RepositoryEnabled,
                    IsOfficial = plugin.IsOfficial,
                };
                repoGroups.Add(group);
            }

            group.Plugins.Add(plugin);
        }

        // 缺译多的排前面
        repoGroups.Sort((a, b) =>
        {
            var byMissing = b.MissingCount.CompareTo(a.MissingCount);
            return byMissing != 0 ? byMissing : string.Compare(a.Short, b.Short, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    ///     关键词匹配已改为全字段（SearchBlob），旧的范围匹配已删。
    /// </summary>
    private void InvalidateIndex()
    {
        // 译文变了：重建搜索结果（不重建反射索引，够快）
        rebuildPending = true;
        rebuildRepoPending = true;
    }

    /// <summary>
    ///     改过译文后刷新受影响的条目状态（缺译 / 机器译 / 你译、完成度）。
    ///
    ///     早先的做法是在后台重新跑一遍 <see cref="TranslationIndex.Build"/>：那会从**后台线程**反射遍历
    ///     卫月的仓库/清单列表，而卫月自己的仓库重载（<c>ReloadAllReposAsync</c>）也会在别的线程动同一批集合，
    ///     撞上就会读到正在被改动的集合。改成就地重算：只动我们自己的缓存，不再碰卫月的内部结构。
    /// </summary>
    private void RefreshIndexSoon()
    {
        InvalidateIndex();
        refreshStatesRequested = true;
    }

    /// <summary>
    ///     把每一行看得见的状态按当前词表重算（不反射卫月；一帧最多跑一次）。
    /// </summary>
    private void RefreshEntryStates()
    {
        if (!refreshStatesRequested)
        {
            return;
        }

        refreshStatesRequested = false;
        if (index is not { Available: true })
        {
            return;
        }

        foreach (var entry in index.All)
        {
            plugin.Table.TryGet(entry.InternalName, out var current);
            entry.RefreshFrom(current);
        }
    }
}
