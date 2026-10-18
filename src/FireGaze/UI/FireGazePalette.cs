using System.Numerics;

namespace FireGaze.UI;

/// <summary>FireGaze's own opaque work surfaces and semantic colors. Game content keeps its original colors.</summary>
internal static class FireGazePalette
{
    public static readonly Vector4 Window = Rgb(0xE6E6E6);
    public static readonly Vector4 Panel = Rgb(0xF0F0F0);
    public static readonly Vector4 Raised = Rgb(0xFAFAFA);
    public static readonly Vector4 Hover = Rgb(0xE0E0E0);
    public static readonly Vector4 Selected = Rgb(0xDCE5E8);
    public static readonly Vector4 Text = Rgb(0x24282D);
    public static readonly Vector4 Muted = Rgb(0x59616B);
    public static readonly Vector4 Border = Rgb(0x7A828B);
    public static readonly Vector4 Action = Rgb(0x245B75);
    public static readonly Vector4 ActionHover = Rgb(0x1D4D65);
    public static readonly Vector4 ActionActive = Rgb(0x173F54);
    public static readonly Vector4 BrandEmber = Rgb(0xAE4B21);
    public static readonly Vector4 Good = Rgb(0x2E684C);
    public static readonly Vector4 Warn = Rgb(0x80550E);
    public static readonly Vector4 Bad = Rgb(0xB73535);
    public static readonly Vector4 Skip = Rgb(0x615173);
    public static readonly Vector4 White = Rgb(0xFFFFFF);

    private static Vector4 Rgb(uint value) => new(((value >> 16) & 255) / 255f,
        ((value >> 8) & 255) / 255f, (value & 255) / 255f, 1f);
}
