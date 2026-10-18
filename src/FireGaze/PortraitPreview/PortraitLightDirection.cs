using System.Numerics;

namespace FireGaze.PortraitPreview;

internal enum PortraitLightView { Top, Side }
internal readonly record struct PortraitLightAngles(float VerticalDegrees, float HorizontalDegrees);

/// <summary>
/// Pure direction math for the portrait's directional light only. Ambient light has no position or direction.
/// FFXIVClientStructs CharaViewPortrait documents both native short angle fields as -180..180 degrees:
/// Source commit d6397a76b3a63c2b247025dc296653182b718ca6, CharaViewPortrait.cs (2026-10-19).
/// The canonical UI representation folds vertical to -90..90 and horizontal to [-180,180), preserving the ray.
/// Convention: +X right, +Y up, +Z forward; native positive vertical points toward -Y.
/// No native calls are made and screen projections are illustrative, not calibrated camera projections.
/// </summary>
internal static class PortraitLightDirection
{
    public static bool TryNative(short vertical, short horizontal, out PortraitLightAngles angles)
    {
        angles = default;
        return vertical is >= -180 and <= 180 && horizontal is >= -180 and <= 180
            && TryNormalize(vertical, horizontal, out angles);
    }

    public static bool TryNormalize(float vertical, float horizontal, out PortraitLightAngles angles)
    {
        angles = default;
        if (!float.IsFinite(vertical) || !float.IsFinite(horizontal)) return false;
        var latitude = Wrap(vertical); var longitude = Wrap(horizontal);
        if (latitude > 90) { latitude = 180 - latitude; longitude = Wrap(longitude + 180); }
        else if (latitude < -90) { latitude = -180 - latitude; longitude = Wrap(longitude + 180); }
        angles = new(latitude, longitude);
        return true;
    }

    public static bool TryDirection(float vertical, float horizontal, out Vector3 direction)
    {
        direction = default;
        if (!TryNormalize(vertical, horizontal, out var angles)) return false;
        var latitude = angles.VerticalDegrees * Math.PI / 180d;
        var longitude = angles.HorizontalDegrees * Math.PI / 180d;
        direction = new((float)(Math.Cos(latitude) * Math.Sin(longitude)),
            (float)-Math.Sin(latitude), (float)(Math.Cos(latitude) * Math.Cos(longitude)));
        return true;
    }

    public static bool TryAngles(Vector3 direction, out PortraitLightAngles angles)
    {
        angles = default;
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || !float.IsFinite(direction.Z)) return false;
        var x = (double)direction.X; var y = (double)direction.Y; var z = (double)direction.Z;
        var length = Math.Sqrt(x * x + y * y + z * z);
        if (length == 0) return false;
        var horizontal = Math.Sqrt(x * x + z * z);
        var latitude = (float)(-Math.Atan2(y, horizontal) * 180d / Math.PI);
        var longitude = horizontal <= length * 1e-12 ? 0 : (float)(Math.Atan2(x, z) * 180d / Math.PI);
        return TryNormalize(latitude, longitude, out angles);
    }

    public static bool TryQuantize(float vertical, float horizontal, out short nativeVertical, out short nativeHorizontal)
    {
        nativeVertical = nativeHorizontal = 0;
        if (!TryNormalize(vertical, horizontal, out var angles)) return false;
        nativeVertical = (short)Math.Round(angles.VerticalDegrees, MidpointRounding.AwayFromZero);
        nativeHorizontal = (short)Wrap((float)Math.Round(angles.HorizontalDegrees, MidpointRounding.AwayFromZero));
        return true;
    }

    /// <summary>Top: screen (X,-Z), hidden Y. Side: screen (Z,-Y), hidden X. Coordinates lie in the unit disk.</summary>
    public static bool TryProject(float vertical, float horizontal, PortraitLightView view, out Vector2 point)
    {
        point = default;
        if (view is not (PortraitLightView.Top or PortraitLightView.Side)
            || !TryDirection(vertical, horizontal, out var direction)) return false;
        point = view == PortraitLightView.Top ? new(direction.X, -direction.Z) : new(direction.Z, -direction.Y);
        return true;
    }

    /// <summary>
    /// Orthographic direction projections lose one axis. The caller must explicitly preserve/select that
    /// hemisphere; the core never guesses it. Dragging outside the disk clamps to its rim.
    /// </summary>
    public static bool TryFromProjection(Vector2 point, PortraitLightView view, bool negativeHiddenComponent,
        out PortraitLightAngles angles)
    {
        angles = default;
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)
            || view is not (PortraitLightView.Top or PortraitLightView.Side)) return false;
        var x = (double)point.X; var y = (double)point.Y;
        var squared = x * x + y * y;
        if (squared > 1) { var length = Math.Sqrt(squared); x /= length; y /= length; squared = 1; }
        var hidden = Math.Sqrt(Math.Max(0, 1 - squared)) * (negativeHiddenComponent ? -1 : 1);
        var direction = view == PortraitLightView.Top
            ? new Vector3((float)x, (float)hidden, (float)-y)
            : new Vector3((float)hidden, (float)-y, (float)x);
        return TryAngles(direction, out angles);
    }

    private static float Wrap(float degrees)
    {
        var wrapped = degrees % 360f;
        if (wrapped >= 180) wrapped -= 360;
        if (wrapped < -180) wrapped += 360;
        return wrapped == 0 ? 0 : wrapped;
    }
}
