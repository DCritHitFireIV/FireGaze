using System.Numerics;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitWindowBounds(Vector2 Preferred, Vector2 Minimum, Vector2 Maximum);

internal static class PortraitWindowLayout
{
    internal static PortraitWindowBounds ForViewport(Vector2 work, float globalScale)
    {
        // WindowHost multiplies these logical sizes by GlobalScale after PreDraw.
        var scale = float.IsFinite(globalScale) && globalScale >= .01f ? globalScale : 1f;
        var maximum = PortraitPopupLayout.MaximumSize(work, scale) / scale;
        return new(Vector2.Min(new(1120, 760), maximum), Vector2.Min(new(700, 540), maximum), maximum);
    }
}
