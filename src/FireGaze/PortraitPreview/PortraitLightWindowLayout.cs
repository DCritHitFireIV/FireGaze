using System.Numerics;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitLightWindowBounds(Vector2 Minimum,Vector2 Preferred,Vector2 Maximum);

/// <summary>ImGui windows use physical dimensions; every bound fits the current work area.</summary>
internal static class PortraitLightWindowLayout
{
    public static PortraitLightWindowBounds ForViewport(Vector2 work,float fontScale)
    {
        var scale=float.IsFinite(fontScale) && fontScale>0 ? fontScale : 1f;
        var maximum=Vector2.Min(new Vector2(560,650)*scale,PortraitPopupLayout.MaximumSize(work,scale));
        return new(Vector2.Min(new Vector2(290,360)*scale,maximum),
            Vector2.Min(new Vector2(340,450)*scale,maximum),maximum);
    }
}
