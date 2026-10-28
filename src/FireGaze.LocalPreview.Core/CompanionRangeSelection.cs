namespace FireGaze.LocalPreview.Core;

public readonly record struct CompanionRangeSelectionResult(long[] Selected, long? Anchor);

/// <summary>Detached card selection in displayed order; it never changes the native target.</summary>
public static class CompanionRangeSelection
{
    public static CompanionRangeSelectionResult Apply(IReadOnlyList<long> visible,
        IReadOnlyCollection<long> selected, long? anchor, long clicked, bool extendRange, bool toggle)
    {
        var clickedIndex = IndexOf(visible, clicked);
        if (clickedIndex < 0) return new(selected.ToArray(), anchor);
        var anchorIndex = anchor is { } previous ? IndexOf(visible, previous) : -1;
        var result = new HashSet<long>();
        if (extendRange && anchorIndex >= 0)
        {
            result.UnionWith(selected);
            for (var i = Math.Min(anchorIndex, clickedIndex); i <= Math.Max(anchorIndex, clickedIndex); i++)
                result.Add(visible[i]);
        }
        else
        {
            if (toggle) result.UnionWith(selected);
            if (!result.Add(clicked) && toggle) result.Remove(clicked);
            anchor = clicked;
        }
        return new(visible.Where(result.Contains).Distinct().ToArray(), anchor);
    }

    private static int IndexOf(IReadOnlyList<long> visible, long id)
    {
        for (var i = 0; i < visible.Count; i++) if (visible[i] == id) return i;
        return -1;
    }
}
