namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitDesignRowLayout(float IconSize, float TextX, float TextWidth,
    float HideX, float HideSize, float Gap);

internal static class PortraitDesignShowcaseLayout
{
    internal static float NormalizeDisplayScale(float scale)
        => float.IsFinite(scale) ? Math.Clamp(scale, .75f, 2f) : 1;

    internal static PortraitDesignRowLayout Row(float width, float scale, float displayScale = 1)
    {
        width = float.IsFinite(width) ? Math.Max(1, width) : 1;
        scale = float.IsFinite(scale) && scale > 0 ? scale : 1;
        scale *= NormalizeDisplayScale(displayScale);
        var icon = Math.Min(34 * scale, width * .18f);
        var hide = Math.Min(26 * scale, width * .15f);
        var gap = Math.Min(8 * scale, width * .025f);
        var textX = icon + gap;
        var hideX = width - hide;
        return new(icon, textX, Math.Max(1, hideX - gap - textX), hideX, hide, gap);
    }
}
