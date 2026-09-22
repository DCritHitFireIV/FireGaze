using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

internal sealed partial class ContributeWindow : Window
{
    private void EnsureIndex()
    {
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

            return;
        }

        if (index is { Available: true })
        {
            return;
        }

        if (DateTime.UtcNow < retryAfter)
        {
            return;
        }

        var table = plugin.SnapshotTable();
        buildTask = Task.Run(() => TranslationIndex.Build(table));
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

        // 三个范围全不勾 = 按插件名搜
        var anyScope = scopeName || scopePunchline || scopeDescription;

        foreach (var entry in index.All)
        {
            if (!plugin.Config.ContributeShowDisabled)
            {
                // 只看已启用的库（官方主库没有 RepositoryURL，永远算启用）
                if (entry.RepositoryURL is not null && !entry.RepositoryEnabled)
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

            if (query.Length > 0 && !Match(entry, query, anyScope))
            {
                continue;
            }

            filtered.Add(entry);
        }
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
            var key = plugin.RepositoryURL ?? string.Empty;
            var group = repoGroups.FirstOrDefault(x => string.Equals(x.URL, key, StringComparison.Ordinal));
            if (group is null)
            {
                group = new RepoGroup
                {
                    URL = key,
                    Short = key.Length == 0 ? "官方主库" : RepoShort(key),
                    Enabled = plugin.RepositoryEnabled,
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
    ///     按勾选的搜索范围匹配关键词（三个范围全不勾时只看插件名）。
    /// </summary>
    private bool Match(TranslationIndexEntry entry, string query, bool anyScope)
    {
        if (anyScope)
        {
            if (scopeName && entry.DisplayName.ToLowerInvariant().Contains(query, StringComparison.Ordinal))
            {
                return true;
            }

            if (scopePunchline && entry.OriginalPunchline.ToLowerInvariant().Contains(query, StringComparison.Ordinal))
            {
                return true;
            }

            if (scopeDescription && entry.OriginalDescription.ToLowerInvariant().Contains(query, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        return entry.DisplayName.ToLowerInvariant().Contains(query, StringComparison.Ordinal);
    }

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
