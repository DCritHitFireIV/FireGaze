namespace FireGaze.LocalPreview.Interaction;

/// <summary>
/// Compact, responsive placement for icon cards in the local-preview catalogs.
/// The old table used a fixed column count, so a wide window stretched the
/// columns and left large empty lanes between cards.  This layout keeps a
/// small, stable gap and derives the number of lanes from the content width.
/// </summary>
public readonly record struct PreviewIconMasonryLayout(
    int Columns,
    float CardWidth,
    float Gap)
{
    private const float MinimumGap = 8f;

    public static PreviewIconMasonryLayout For(float availableWidth, float requestedCardWidth, float scale = 1f)
    {
        var width = Math.Max(1f, availableWidth);
        var effectiveScale = Math.Max(0.75f, scale);
        var gap = MinimumGap * effectiveScale;
        var requested = Math.Max(1f, requestedCardWidth);
        var columns = Math.Max(1, (int)MathF.Floor((width + gap) / (requested + gap)));
        var cardWidth = MathF.Floor((width - gap * (columns - 1)) / columns);
        return new(columns, Math.Max(1f, Math.Min(requested, cardWidth)), gap);
    }

    /// <summary>Places cards into the currently shortest lane.</summary>
    public IReadOnlyList<PreviewIconCardPlacement> Place(IReadOnlyList<float> heights)
    {
        var bottoms = new float[Columns];
        var placements = new PreviewIconCardPlacement[heights.Count];
        for (var index = 0; index < heights.Count; index++)
        {
            var column = 0;
            for (var candidate = 1; candidate < bottoms.Length; candidate++)
                if (bottoms[candidate] < bottoms[column]) column = candidate;

            var height = Math.Max(1f, heights[index]);
            placements[index] = new(column, column * (CardWidth + Gap), bottoms[column], height);
            bottoms[column] += height + Gap;
        }
        return placements;
    }
}

public readonly record struct PreviewIconCardPlacement(
    int Column,
    float X,
    float Y,
    float Height);
