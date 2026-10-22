using System.Numerics;

namespace FireGaze.PortraitPreview;

internal static class PortraitPopupLayout
{
    internal static Vector2 MaximumSize(Vector2 work, float scale)
    {
        var margin = 12f * (float.IsFinite(scale) && scale > 0 ? scale : 1f);
        return new(Limit(work.X, margin), Limit(work.Y, margin));
    }

    internal static Vector2 PreferredSize(Vector2 desired, Vector2 work, float scale)
    {
        var maximum = MaximumSize(work, scale);
        return new(Fit(desired.X, maximum.X), Fit(desired.Y, maximum.Y));
    }

    private static float Limit(float extent, float margin)
    {
        if (!float.IsFinite(extent) || extent <= 0) return 1f;
        return Math.Max(1f, extent - 2f * Math.Min(margin, extent * .1f));
    }

    // Zero leaves the corresponding dimension to ImGui's content sizing.
    private static float Fit(float desired, float maximum) => desired <= 0 ? 0
        : float.IsFinite(desired) ? Math.Min(desired, maximum) : maximum;
}
