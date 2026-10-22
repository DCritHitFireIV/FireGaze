using System.Numerics;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitNativeLiveImageRect(Vector2 Offset, Vector2 Size);

internal static class PortraitNativeLiveLayout
{
    public static PortraitNativeLiveImageRect Fit(Vector2 available, int width, int height, float fraction = 1f)
    {
        if (!float.IsFinite(available.X) || !float.IsFinite(available.Y)
            || available.X <= 0 || available.Y <= 0 || width <= 0 || height <= 0) return default;
        fraction = float.IsFinite(fraction) ? Math.Clamp(fraction, .05f, 1f) : 1f;
        var ratio = MathF.Min(available.X / width, available.Y / height) * fraction;
        var size = new Vector2(width, height) * ratio;
        return new((available - size) * .5f, size);
    }

    public static float ResizeFraction(float current, Vector2 delta, Vector2 maximum, float minimumEdge)
    {
        current = float.IsFinite(current) ? Math.Clamp(current, .05f, 1f) : 1f;
        if (!float.IsFinite(maximum.X) || !float.IsFinite(maximum.Y) || maximum.X <= 0 || maximum.Y <= 0)
            return current;
        minimumEdge = float.IsFinite(minimumEdge) && minimumEdge > 0 ? minimumEdge : 1f;
        var minimum = Math.Clamp(minimumEdge / MathF.Min(maximum.X, maximum.Y), .05f, 1f);
        if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y)) return Math.Clamp(current, minimum, 1f);
        // The image stays centered: its bottom-right corner moves half as far as its size.
        var lengthSquared = (double)maximum.X * maximum.X + (double)maximum.Y * maximum.Y;
        var change = 2 * ((double)delta.X * maximum.X + (double)delta.Y * maximum.Y) / lengthSquared;
        return (float)Math.Clamp(current + change, minimum, 1d);
    }
}
