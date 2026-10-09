using System.Collections;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using FireGaze.Discovery;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

/// <summary>
///     插件发现：索引生命周期（云端词表为准，本机卫月清单补原文/图标）与筛选、排序。
/// </summary>
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
        // null = 反射读不到，按「已完成」处理，别让页面永远停在等待上
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
            Plugin.Log.Debug("[FireGaze] 卫月插件库刷新完成：重建插件发现索引");
            indexMayBePartial = false;
            index = null;
            rebuildPending = true;
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

    /// <summary>
    ///     筛选出当前要显示的行：隐藏主库 / 隐藏已在库 / 关键词，然后按排序档位重排。
    /// </summary>
    private void RebuildFiltered()
    {
        if (!rebuildPending)
        {
            return;
        }

        rebuildPending = false;
        filtered.Clear();
        incompatibleHidden = 0;

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

            // 兼容性预筛（用户 2026-10-07）：卫月自己就不会加载的旧 API 插件不显示
            // （与 PluginManager.IsManifestEligible 同口径；等级未知的不拦）
            if (!PluginCompatibility.IsAPICompatible(entry.APILevel))
            {
                incompatibleHidden++;
                continue;
            }

            // 「隐藏已在库」：仓库已经在自己列表里且启用（含官方主库）→ 不用再看到了
            if (hideInLibrary && entry.RepositoryKnown && entry.RepositoryEnabled)
            {
                continue;
            }

            // 已停用库不再过滤（用户 2026-10-07：去掉「连着已停用的库」开关）——
            // 这类插件要能看见、并从行尾的「启用」一键回库
            // 全字段搜索（插件名/作者/简介/详情，原文+译文；SearchBlob 里都拼好了）
            if (query.Length > 0 && !entry.SearchBlob.Contains(query, StringComparison.Ordinal))
            {
                continue;
            }

            filtered.Add(entry);
        }

        ApplyDiscoverySort();
        this.iconQueue.RetainWaiting(filtered.Select(e => e.InternalName).ToArray());
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
                    // 键值必须是真随机——按当前列表顺序发号的话，「换一批」/从别的档切过来时
                    // 顺序原样不变（2026-10-06 用户实测：随机跟没按一样）。
                    shuffleOrder[entry.InternalName] = Random.Shared.Next();
                }
            }
        }

        sortContext.Mode = sortMode;
        sortContext.WeeklyLikes = discoveryStats?.WeeklyLikes ?? [];
        sortContext.Recommends = discoveryStats?.Recommends ?? [];
        sortContext.Shuffle = shuffleOrder;
        DiscoverySort.Apply(filtered, sortContext);
    }

    private void InvalidateIndex()
    {
        // 译文/词表变了：重建搜索结果（不重建反射索引，够快）
        rebuildPending = true;
    }

    /// <summary>词表整份被换过（云端刷新）：丢掉索引，下一帧走 EnsureIndex 全量重建。</summary>
    private void InvalidateIndexFully()
    {
        index = null;
        rebuildPending = true;
        ResetIconQueue();
    }

    /// <summary>
    ///     改过词表后刷新受影响的条目状态。
    ///
    ///     早先的做法是在后台重新跑一遍 <see cref="TranslationIndex.Build" />：那会从**后台线程**反射遍历
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
            TranslationIndex.RefreshSearchBlob(entry);
        }
    }
}
