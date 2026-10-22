using System.Numerics;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitNativeLiveImageRect(Vector2 Offset, Vector2 Size);

internal static class PortraitNativeLiveLayout
{
    public static PortraitNativeLiveImageRect Fit(Vector2 available, int width, int height)
    {
        if (!float.IsFinite(available.X) || !float.IsFinite(available.Y)
            || available.X <= 0 || available.Y <= 0 || width <= 0 || height <= 0) return default;
        var ratio = MathF.Min(available.X / width, available.Y / height);
        var size = new Vector2(width, height) * ratio;
        return new((available - size) * .5f, size);
    }
}
