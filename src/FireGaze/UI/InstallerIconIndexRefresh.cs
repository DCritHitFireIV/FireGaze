using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>
/// Coalesces plugin changes into a single background index build. Tick runs on
/// the UI thread; notifications may arrive on any thread.
/// </summary>
internal sealed class InstallerIconIndexRefresh
{
    private long requestedRevision = 1;
    private long appliedRevision;
    private long pendingRevision;
    private Task<InstalledPluginsIndex>? pending;
    private DateTime retryAfter = DateTime.MinValue;

    public void NotifyChanged() => Interlocked.Increment(ref requestedRevision);

    public void Tick(DateTime now, Func<Task<InstalledPluginsIndex>?> beginBuild,
        Action<IReadOnlyList<InstalledPluginEntry>> apply)
    {
        if (pending is null)
        {
            var revision = Interlocked.Read(ref requestedRevision);
            if (appliedRevision == revision || now < retryAfter) return;
            pending = beginBuild();
            if (pending is not null) pendingRevision = revision;
            return;
        }

        if (!pending.IsCompleted) return;
        var completed = pending;
        pending = null;
        // Observe failures even when an intervening event invalidates the result.
        _ = completed.Exception;
        if (pendingRevision != Interlocked.Read(ref requestedRevision)) return;

        if (completed.Status == TaskStatus.RanToCompletion && completed.Result.Available)
        {
            apply(completed.Result.All);
            appliedRevision = pendingRevision;
        }
        else
        {
            retryAfter = now.AddSeconds(10);
        }
    }
}
