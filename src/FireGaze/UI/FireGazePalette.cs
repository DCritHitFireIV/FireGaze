using System.Numerics;

namespace FireGaze.UI;

/// <summary>FireGaze's own opaque work surfaces and semantic colors. Game content keeps its original colors.</summary>
internal static class FireGazePalette
{
    public static readonly Vector4 Window = Rgb(0x2B2B2B);
    public static readonly Vector4 Panel = Rgb(0x333333);
    public static readonly Vector4 Raised = Rgb(0x3D3D3D);
    public static readonly Vector4 Hover = Rgb(0x474747);
    public static readonly Vector4 Selected = Rgb(0x4A4A4A);
    public static readonly Vector4 Text = Rgb(0xEEEEEE);
    public static readonly Vector4 Muted = Rgb(0xB9B9B9);
    public static readonly Vector4 Border = Rgb(0x989898);
    // Button fills must retain white-label contrast; light foregrounds serve text and focus.
    public static readonly Vector4 Action = Rgb(0x245B75);
    public static readonly Vector4 ActionHover = Rgb(0x1D4D65);
    public static readonly Vector4 ActionActive = Rgb(0x173F54);
    public static readonly Vector4 Focus = Rgb(0x8DC5DF);
    public static readonly Vector4 BrandEmber = Rgb(0xAE4B21);
    public static readonly Vector4 Good = Rgb(0x2E684C);
    public static readonly Vector4 GoodText = Rgb(0x94D5AC);
    public static readonly Vector4 Warn = Rgb(0xE7C58A);
    public static readonly Vector4 Bad = Rgb(0xB73535);
    public static readonly Vector4 BadText = Rgb(0xFFADAD);
    public static readonly Vector4 Skip = Rgb(0xCFB4DF);
    public static readonly Vector4 SelectionShadow = Rgb(0x141414);
    public static readonly Vector4 White = Rgb(0xFFFFFF);

    private static Vector4 Rgb(uint value) => new(((value >> 16) & 255) / 255f,
        ((value >> 8) & 255) / 255f, (value & 255) / 255f, 1f);
}
