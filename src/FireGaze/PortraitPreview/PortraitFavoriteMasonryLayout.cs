namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitFavoriteMasonryLayout(int Columns, float CardWidth, float Gap)
{
    private const float MinimumCardWidth = 144;

    public static PortraitFavoriteMasonryLayout For(float availableWidth, float scale)
    {
        var gap = 12 * Math.Max(0.75f, scale);
        var minimum = MinimumCardWidth * Math.Max(0.75f, scale);
        var columns = Math.Clamp((int)MathF.Floor((availableWidth + gap) / (minimum + gap)), 1, 6);
        var cardWidth = MathF.Floor((availableWidth - gap * (columns - 1)) / columns);
        return new(columns, Math.Max(1, cardWidth), gap);
    }

    public IReadOnlyList<PortraitFavoriteCardPlacement> Place(IReadOnlyList<float> heights)
    {
        var bottoms = new float[Columns];
        var result = new PortraitFavoriteCardPlacement[heights.Count];
        for (var index = 0; index < heights.Count; index++)
        {
            var column = 0;
            for (var candidate = 1; candidate < bottoms.Length; candidate++)
                if (bottoms[candidate] < bottoms[column]) column = candidate;
            var height = Math.Max(1, heights[index]);
            result[index] = new(column, column * (CardWidth + Gap), bottoms[column], height);
            bottoms[column] += height + Gap;
        }
        return result;
    }
}

internal readonly record struct PortraitFavoriteCardPlacement(int Column, float X, float Y, float Height);
