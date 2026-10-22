using System.Numerics;

namespace FireGaze.UI;

internal readonly record struct NearbyWindowBounds(Vector2 Position, Vector2 Size);

/// <summary>All coordinates and extents are screen pixels; only spacing uses UI scale.</summary>
internal static class NearbyWindowLayout
{
    internal static NearbyWindowBounds Place(Vector2 workPosition, Vector2 workSize,
        Vector2 ownerPosition, Vector2 ownerSize, Vector2 desiredSize, float scale)
    {
        workPosition = new(Finite(workPosition.X, 0), Finite(workPosition.Y, 0));
        workSize = new(Extent(workSize.X, 1), Extent(workSize.Y, 1));
        var gap = 12 * (float.IsFinite(scale) && scale > 0 ? scale : 1);
        var margin = Vector2.Min(new Vector2(gap), workSize * .1f);
        var minimum = workPosition + margin;
        var available = Vector2.Max(Vector2.One, workSize - margin * 2);
        var size = Vector2.Min(new(Extent(desiredSize.X, 480), Extent(desiredSize.Y, 360)), available);
        var maximum = Vector2.Max(minimum, workPosition + workSize - margin - size);
        var owner = new Vector2(Finite(ownerPosition.X, workPosition.X + workSize.X * .5f),
            Finite(ownerPosition.Y, workPosition.Y + workSize.Y * .5f));
        var extent = new Vector2(Extent(ownerSize.X, 1), Extent(ownerSize.Y, 1));
        var candidates = new[]
        {
            owner + new Vector2(extent.X + gap, 0),
            owner - new Vector2(size.X + gap, 0),
            owner + new Vector2(0, extent.Y + gap),
            owner - new Vector2(0, size.Y + gap),
        };
        foreach (var candidate in candidates)
            if (candidate.X >= minimum.X && candidate.Y >= minimum.Y
                && candidate.X <= maximum.X && candidate.Y <= maximum.Y)
                return new(candidate, size);

        // Crowded screens may require overlap. Prefer covering less of the owner,
        // then the nearest candidate; never anchor an unrelated window at (0, 0).
        var best = Vector2.Clamp(candidates[0], minimum, maximum);
        var bestOverlap = float.PositiveInfinity;
        var bestDistance = float.PositiveInfinity;
        foreach (var candidate in candidates)
        {
            var bounded = Vector2.Clamp(candidate, minimum, maximum);
            var intersection = Vector2.Max(Vector2.Zero,
                Vector2.Min(bounded + size, owner + extent) - Vector2.Max(bounded, owner));
            var overlap = intersection.X * intersection.Y;
            var distance = Vector2.DistanceSquared(bounded + size * .5f, owner + extent * .5f);
            if (overlap < bestOverlap || overlap == bestOverlap && distance < bestDistance)
            { best = bounded; bestOverlap = overlap; bestDistance = distance; }
        }
        return new(best, size);
    }

    private static float Finite(float value, float fallback) => float.IsFinite(value) ? value : fallback;
    private static float Extent(float value, float fallback) => float.IsFinite(value) && value > 0 ? value : fallback;
}
