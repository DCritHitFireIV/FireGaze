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
    ///     勾选的插件里，有哪些库链是本机还没有的（可以一次加进来）。
    /// </summary>
    private List<string> SelectedReposToAdd()
    {
        var result = new List<string>();
        if (selected.Count == 0 || index is not { Available: true })
        {
            return result;
        }

        // 本机库里没有的库链（云端词表里标着、但还没加进来的）；按归一化地址去重
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in index.All)
        {
            if (!selected.Contains(entry.InternalName) ||
                string.IsNullOrWhiteSpace(entry.RepositoryURL) ||
                entry.RepositoryKnown)
            {
                continue;
            }

            if (seen.Add(InstalledPluginsIndex.NormalizeRepositoryURL(entry.RepositoryURL)))
            {
                result.Add(entry.RepositoryURL);
            }
        }

        return result;
    }

    /// <summary>
    ///     最近一次「添加库」的撤回记录（不是最近的就不给撤）。
    /// </summary>
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

    private void AddSelectedRepositories(List<string> urls)
    {
        if (urls.Count == 0)
        {
            return;
        }

        var ok = plugin.AddThirdPartyRepositories(urls, out var message);
        SetStatus(message, !ok);
        if (ok)
        {
            ReportRepoAdds(urls);
        }

        rebuildPending = true;
        rebuildRepoPending = true;
    }

    private void UndoLastAdd()
    {
        var ok = plugin.TryUndoLast(out var message);
        SetStatus(message, !ok);
        rebuildPending = true;
        rebuildRepoPending = true;
    }

    private void AddRepository()
    {
        var url = repoInput.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            repoMessage = "这看起来不是一条 http(s) 地址";
            statusIsError = true;
            return;
        }

        var added = plugin.AddThirdPartyRepository(url, out var message);
        repoMessage = message;
        statusIsError = !added;
        if (added)
        {
            repoInput = string.Empty;
            ReportRepoAdds([url]);
        }
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
        });
    }

    private void PeekIcon(TranslationIndexEntry entry)
    {
        if (!entry.DeclaresIcon || iconHandles.ContainsKey(entry.InternalName) ||
            iconMisses.Contains(entry.InternalName))
        {
            return;
        }

        // 只用本地已缓存的（卫月内存缓存或我们的落盘缓存），绝不触发下载
        var installed = new InstalledPluginEntry
        {
            InternalName = entry.InternalName,
            DisplayName = entry.DisplayName,
            IconURL = entry.IconURL,
            RawPlugin = entry.RawPlugin!,
            Manifest = entry.Manifest,
            IsThirdParty = entry.IsThirdParty,
        };

        if (plugin.Icons.TryGetHandle(installed, out var cached) && !cached.IsNull)
        {
            iconHandles[entry.InternalName] = cached;
        }
        else if (PluginIconLookup.TryPeekHandle(installed, out var handle) && !handle.IsNull)
        {
            iconHandles[entry.InternalName] = handle;
        }
        else
        {
            iconMisses.Add(entry.InternalName);
        }
    }
}
