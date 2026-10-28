using System.Text.Json;

namespace FireGaze.UIText;

// A downloaded library merge is a proposal against its pre-download snapshot.
// It may replace only identities still equal to that snapshot at commit time.
// Provenance and ranking are already resolved by the real library merger.
internal static class UITextLibraryReceipt
{
    private static readonly JsonSerializerOptions SnapshotOptions = new() { IgnoreReadOnlyProperties = true };
    internal sealed record Result(UITextPack Pack, int Applied);

    internal static Result Accept(UITextPack baseline, UITextPack proposal, UITextPack current,
        Func<UITextTarget, bool>? isUnchanged = null)
    {
        var proposed = UITextPackEditMerge.Clone(proposal);
        var result = UITextPackEditMerge.Clone(current);
        var applied = MergeRows(baseline.Entries, proposed.Entries, result.Entries, row => row.Original,
            baseline.IsSkipped, proposed.IsSkipped, result.IsSkipped,
            (key, skipped) => { if (skipped) result.MarkSkipped(key); else result.UnmarkSkipped(key); },
            row => row.Translated, static (_, _) => true,
            row => isUnchanged?.Invoke(new(row.Original, row.Context, row, null)) ?? true);
        applied += MergeRows(baseline.Resources, proposed.Resources, result.Resources, row => (row.Container, row.Key),
            key => baseline.IsResourceSkipped(key.Container, key.Key),
            key => proposed.IsResourceSkipped(key.Container, key.Key),
            key => result.IsResourceSkipped(key.Container, key.Key),
            (key, skipped) =>
            {
                if (skipped) result.MarkResourceSkipped(key.Container, key.Key);
                else result.UnmarkResourceSkipped(key.Container, key.Key);
            }, row => row.Translated, static (before, after) => before.Original == after.Original,
            row => isUnchanged?.Invoke(new(row.Original, null, null, row)) ?? true);
        applied += MergeRows(baseline.Attributes, proposed.Attributes, result.Attributes, row => row.Original,
            baseline.IsAttributeSkipped, proposed.IsAttributeSkipped, result.IsAttributeSkipped,
            (key, skipped) => { if (skipped) result.MarkAttributeSkipped(key); else result.UnmarkAttributeSkipped(key); },
            row => row.Translated, static (_, _) => true,
            row => isUnchanged?.Invoke(new(row.Original, row.Context, null, null, row)) ?? true);
        return new(result, applied);
    }

    private static int MergeRows<T, TKey>(IReadOnlyList<T> baseline, IReadOnlyList<T> proposal, List<T> result,
        Func<T, TKey> keyOf, Func<TKey, bool> beforeSkipped, Func<TKey, bool> proposedSkipped,
        Func<TKey, bool> currentSkipped, Action<TKey, bool> setSkipped, Func<T, string> translated,
        Func<T, T, bool> sameIdentity, Func<T, bool> isUnchanged) where TKey : notnull
    {
        var before = baseline.ToDictionary(keyOf);
        var current = result.ToDictionary(keyOf);
        var applied = 0;
        foreach (var row in proposal)
        {
            if (!isUnchanged(row)) continue;
            var key = keyOf(row);
            var exists = current.TryGetValue(key, out var latest);
            if (before.TryGetValue(key, out var prior))
            {
                if (!exists || !sameIdentity(prior, row) || !Same(prior, latest!)
                    || beforeSkipped(key) != currentSkipped(key)) continue;
                if (Same(prior, row) && beforeSkipped(key) == proposedSkipped(key)) continue;
            }
            else if (exists || currentSkipped(key)) continue;

            if (!string.IsNullOrWhiteSpace(translated(row))
                && (!exists || translated(latest!) != translated(row))) applied++;
            if (exists) result.Remove(latest!);
            result.Add(row);
            current[key] = row;
            setSkipped(key, proposedSkipped(key));
        }
        return applied;
    }

    private static bool Same<T>(T left, T right) =>
        JsonSerializer.Serialize(left, SnapshotOptions) == JsonSerializer.Serialize(right, SnapshotOptions);
}
