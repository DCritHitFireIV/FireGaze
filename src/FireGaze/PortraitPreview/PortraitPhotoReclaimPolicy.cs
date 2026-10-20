namespace FireGaze.PortraitPreview;

/// <summary>Bounded, progress-aware retry for a capacity failure.</summary>
internal static class PortraitPhotoReclaimPolicy
{
    internal static bool TryWithReclaim(
        Func<bool> attempt,
        Func<bool> capacity,
        Func<bool> reclaim,
        Func<(int Count, long Bytes)> state,
        int maxReclaims)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(capacity);
        ArgumentNullException.ThrowIfNull(reclaim);
        ArgumentNullException.ThrowIfNull(state);
        if (maxReclaims < 0) throw new ArgumentOutOfRangeException(nameof(maxReclaims));

        if (attempt()) return true;
        for (var index = 0; index < maxReclaims && capacity(); index++)
        {
            var before = state();
            if (!reclaim()) return false;
            var after = state();
            // A pinned-only cache can truthfully report that no work was required. It is not
            // progress toward this candidate, so stop rather than spinning on the same bin.
            if (after == before) return false;
            if (attempt()) return true;
        }
        return false;
    }
}
