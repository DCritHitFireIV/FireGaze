namespace FireGaze.LocalPreview.Core;

public readonly record struct CompanionSelectionRectangle(float X1, float Y1, float X2, float Y2);
public readonly record struct CompanionSelectionCard(long Id, CompanionSelectionRectangle Bounds);

/// <summary>Rectangle selection uses the rendered card bounds and a fixed gesture baseline.</summary>
public static class CompanionRectangleSelection
{
    public static CompanionRangeSelectionResult Apply(IReadOnlyList<CompanionSelectionCard> cards,
        IReadOnlyCollection<long> selected, long? anchor, CompanionSelectionRectangle rectangle,
        CompanionSelectionRectangle clip, bool extendSelection, bool toggle)
    {
        var visible = cards.Where(card => card.Id > 0).Select(card => card.Id).Distinct().ToArray();
        var hits = cards.Where(card => card.Id > 0 && Overlaps(card.Bounds, rectangle, clip))
            .Select(card => card.Id).ToHashSet();
        var result = new HashSet<long>();
        if (extendSelection || toggle) result.UnionWith(selected.Where(visible.Contains));
        if (extendSelection || !toggle) result.UnionWith(hits);
        else result.SymmetricExceptWith(hits);
        var nextAnchor = (extendSelection || toggle) && anchor is { } previous && visible.Contains(previous)
            ? anchor : visible.Where(hits.Contains).Select(id => (long?)id).FirstOrDefault();
        return new(visible.Where(result.Contains).ToArray(), nextAnchor);
    }

    private static bool Overlaps(CompanionSelectionRectangle card, CompanionSelectionRectangle drag,
        CompanionSelectionRectangle clip)
    {
        if (!Finite(card) || !Finite(drag) || !Finite(clip)) return false;
        var left = MathF.Max(MathF.Min(card.X1, card.X2), MathF.Max(MathF.Min(drag.X1, drag.X2), MathF.Min(clip.X1, clip.X2)));
        var right = MathF.Min(MathF.Max(card.X1, card.X2), MathF.Min(MathF.Max(drag.X1, drag.X2), MathF.Max(clip.X1, clip.X2)));
        var top = MathF.Max(MathF.Min(card.Y1, card.Y2), MathF.Max(MathF.Min(drag.Y1, drag.Y2), MathF.Min(clip.Y1, clip.Y2)));
        var bottom = MathF.Min(MathF.Max(card.Y1, card.Y2), MathF.Min(MathF.Max(drag.Y1, drag.Y2), MathF.Max(clip.Y1, clip.Y2)));
        return left < right && top < bottom;
    }

    private static bool Finite(CompanionSelectionRectangle value)
        => float.IsFinite(value.X1) && float.IsFinite(value.Y1)
            && float.IsFinite(value.X2) && float.IsFinite(value.Y2);
}
