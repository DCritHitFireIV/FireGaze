namespace FireGaze.LocalPreview.Core;

/// <summary>
/// Selects the companion requests that may be persisted in the single saved group.
/// Pending requests are intentional user choices even before their native actor is
/// ready; failed and retiring leases are transient cleanup states and must not be
/// carried into a later session.
/// </summary>
public static class CompanionSavedGroupPolicy
{
    public static uint[] SelectPersistable(IEnumerable<CompanionDisplayLease> leases)
        => leases
            .Where(x => x.State is CompanionDisplayState.Pending or CompanionDisplayState.Active or CompanionDisplayState.NativeHidden)
            .Select(x => x.CompanionId)
            .ToArray();

    /// <summary>Restores missing instances while repeated clicks keep existing/queued ones.</summary>
    public static uint[] SelectMissing(IEnumerable<uint> saved, IEnumerable<CompanionDisplayLease> leases)
    {
        var remaining = SelectPersistable(leases).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
        var missing = new List<uint>();
        foreach (var id in saved)
        {
            if (remaining.TryGetValue(id, out var count) && count > 0) remaining[id] = count - 1;
            else missing.Add(id);
        }
        return missing.ToArray();
    }

    public static bool CanSave(IEnumerable<CompanionDisplayLease> leases)
        => leases.Any(x => x.State is CompanionDisplayState.Pending or CompanionDisplayState.Active or CompanionDisplayState.NativeHidden);
}
