using System.Reflection;
using FireGaze.RepoAudit;
using FireGaze.UI;

internal static class InstallerIconIndexRefreshCases
{
    private static InstalledPluginsIndex Index(bool available, params InstalledPluginEntry[] entries)
    {
        var index = (InstalledPluginsIndex)Activator.CreateInstance(typeof(InstalledPluginsIndex), true)!;
        typeof(InstalledPluginsIndex).GetProperty(nameof(InstalledPluginsIndex.Available))!.SetValue(index, available);
        typeof(InstalledPluginsIndex).GetField("all", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(index, entries.ToList());
        return index;
    }

    public static void Run(Action<bool, string> check)
    {
        var now = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc);
        var refresh = new InstallerIconIndexRefresh();
        var builds = 0; var applies = 0;
        var first = new TaskCompletionSource<InstalledPluginsIndex>();
        var second = new TaskCompletionSource<InstalledPluginsIndex>();
        Task<InstalledPluginsIndex>? Begin() { builds++; return builds == 1 ? first.Task : second.Task; }
        IReadOnlyList<InstalledPluginEntry>? received = null;
        void Apply(IReadOnlyList<InstalledPluginEntry> entries) { applies++; received = entries; }
        refresh.Tick(now, Begin, Apply);
        for (var i = 0; i < 100; i++) refresh.Tick(now, Begin, Apply);
        check(builds == 1 && applies == 0, "UI frames retain one pending background index without duplicate scans");

        Task.Run(() => { for (var i = 0; i < 100; i++) refresh.NotifyChanged(); }).GetAwaiter().GetResult();
        first.SetResult(Index(true));
        refresh.Tick(now, Begin, Apply);
        check(applies == 0 && builds == 1, "plugin changes arriving during a build discard its stale snapshot");
        refresh.Tick(now, Begin, Apply);
        check(builds == 2 && applies == 0, "many plugin-change events coalesce into one replacement index build");
        var entry = new InstalledPluginEntry { InternalName = "Late", DisplayName = "Late", RawPlugin = new object() };
        second.SetResult(Index(true, entry));
        refresh.Tick(now, Begin, Apply);
        check(applies == 1 && received?.Count == 1 && ReferenceEquals(received[0], entry),
            "only the current successful installed-plugin snapshot reaches the icon targets");
        for (var i = 0; i < 100; i++) refresh.Tick(now.AddMinutes(1), Begin, Apply);
        check(builds == 2 && applies == 1, "an unchanged installer index is not rebuilt on later UI frames");

        refresh = new InstallerIconIndexRefresh(); builds = 0; applies = 0;
        Task<InstalledPluginsIndex>? Unavailable() { builds++; return Task.FromResult(Index(false)); }
        refresh.Tick(now, Unavailable, Apply); refresh.Tick(now, Unavailable, Apply);
        refresh.Tick(now.AddSeconds(9), Unavailable, Apply);
        check(builds == 1 && applies == 0, "unavailable installed-plugin state leaves current icons intact and backs off");
        refresh.Tick(now.AddSeconds(10), Unavailable, Apply);
        check(builds == 2, "an unavailable installed-plugin index can retry after ten seconds");

        refresh = new InstallerIconIndexRefresh(); builds = 0;
        Task<InstalledPluginsIndex>? Faulted() { builds++; return Task.FromException<InstalledPluginsIndex>(new IOException("fixture")); }
        refresh.Tick(now, Faulted, Apply); refresh.Tick(now, Faulted, Apply);
        refresh.Tick(now.AddSeconds(9), Faulted, Apply);
        check(builds == 1, "a failed background index also respects the retry backoff");

        refresh = new InstallerIconIndexRefresh(); builds = 0;
        Task<InstalledPluginsIndex>? Rejected() { builds++; return null; }
        refresh.Tick(now, Rejected, Apply); refresh.Tick(now, Rejected, Apply);
        check(builds == 2, "a rejected lifetime queue does not permanently consume the requested refresh");

        refresh = new InstallerIconIndexRefresh(); builds = 0;
        Task<InstalledPluginsIndex>? Ready() { builds++; return Task.FromResult(Index(true)); }
        refresh.Tick(now, Ready, _ => { });
        refresh.Tick(now, Ready, _ => refresh.NotifyChanged());
        refresh.Tick(now, Ready, _ => { });
        check(builds == 2, "a plugin change during UI application remains pending for the next bounded build");
    }
}
