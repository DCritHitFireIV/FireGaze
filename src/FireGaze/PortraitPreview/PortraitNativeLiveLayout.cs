using System.Numerics;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitNativeLiveImageRect(Vector2 Offset, Vector2 Size);

internal static class PortraitNativeLiveLayout
{
    public static PortraitNativeLiveImageRect Fit(Vector2 available, int width, int height, float fraction = 1f, Vector2 pan = default)
    {
        if (!float.IsFinite(available.X) || !float.IsFinite(available.Y)
            || available.X <= 0 || available.Y <= 0 || width <= 0 || height <= 0) return default;
        fraction = ClampFraction(fraction);
        var ratio = MathF.Min(available.X / width, available.Y / height) * fraction;
        var size = new Vector2(width, height) * ratio;
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y)) return default;
        var overflow = Vector2.Max(Vector2.Zero, size - available);
        return new((available - size) * .5f + ClampPan(pan) * overflow * .5f, size);
    }

    public static float ResizeFraction(float current, Vector2 delta, Vector2 maximum)
    {
        current = ClampFraction(current);
        if (!float.IsFinite(maximum.X) || !float.IsFinite(maximum.Y) || maximum.X <= 0 || maximum.Y <= 0)
            return current;
        if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y)) return current;
        // The image stays centered: its bottom-right corner moves half as far as its size.
        var lengthSquared = (double)maximum.X * maximum.X + (double)maximum.Y * maximum.Y;
        var change = 2 * ((double)delta.X * maximum.X + (double)delta.Y * maximum.Y) / lengthSquared;
        return (float)Math.Clamp(current + change, .25d, 3d);
    }

    public static (float Fraction, Vector2 Pan) ZoomAt(Vector2 available, int width, int height,
        float current, Vector2 pan, Vector2 pointer, float wheel)
    {
        current = ClampFraction(current);
        var before = Fit(available, width, height, current, pan);
        if (before.Size.X <= 0 || before.Size.Y <= 0) return (current, Vector2.Zero);
        if (!float.IsFinite(wheel) || !float.IsFinite(pointer.X) || !float.IsFinite(pointer.Y))
            return (current, ClampPan(pan));
        var next = ClampFraction(current * MathF.Pow(1.15f, Math.Clamp(wheel, -100, 100)));
        var after = Fit(available, width, height, next);
        var imagePoint = (pointer - before.Offset) / before.Size;
        return (next, PanFromOffset(available, after.Size, pointer - imagePoint * after.Size));
    }

    public static Vector2 PanBy(Vector2 available, Vector2 size, Vector2 pan, Vector2 delta)
    {
        if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y)) return ClampPan(pan);
        var overflow = Vector2.Max(Vector2.Zero, size - available);
        var offset = (available - size) * .5f + ClampPan(pan) * overflow * .5f;
        return PanFromOffset(available, size, offset + delta);
    }

    private static Vector2 PanFromOffset(Vector2 available, Vector2 size, Vector2 offset)
    {
        var overflow = Vector2.Max(Vector2.Zero, size - available);
        return ClampPan(new(overflow.X > 0 ? 1 + 2 * offset.X / overflow.X : 0,
            overflow.Y > 0 ? 1 + 2 * offset.Y / overflow.Y : 0));
    }

    private static float ClampFraction(float value) => float.IsFinite(value) ? Math.Clamp(value, .25f, 3f) : 1f;
    private static Vector2 ClampPan(Vector2 value) => new(
        float.IsFinite(value.X) ? Math.Clamp(value.X, -1, 1) : 0,
        float.IsFinite(value.Y) ? Math.Clamp(value.Y, -1, 1) : 0);
}
