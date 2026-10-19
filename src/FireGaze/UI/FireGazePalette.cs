using System.Numerics;

namespace FireGaze.UI;

/// <summary>FireGaze's own opaque work surfaces and semantic colors. Game content keeps its original colors.</summary>
internal static class FireGazePalette
{
    // FireGaze owns a light information surface; purple is the identity accent, not a blanket fill.
    public static readonly Vector4 Window = Rgb(0xF1EBF5);
    public static readonly Vector4 Panel = Rgb(0xFAF7FB);
    public static readonly Vector4 Raised = Rgb(0xF0E5F3);
    public static readonly Vector4 Hover = Rgb(0xE5D5E9);
    public static readonly Vector4 Selected = Rgb(0xE1CBE7);
    public static readonly Vector4 Text = Rgb(0x2B2430);
    public static readonly Vector4 Muted = Rgb(0x5A4B60);
    public static readonly Vector4 Border = Rgb(0x806A89);
    public static readonly Vector4 Action = Rgb(0x6B3B78);
    public static readonly Vector4 ActionHover = Rgb(0x582E65);
    public static readonly Vector4 ActionActive = Rgb(0x45234F);
    public static readonly Vector4 Focus = Rgb(0x7B2F64);
    public static readonly Vector4 BrandEmber = Rgb(0xD45A31);
    public static readonly Vector4 Good = Rgb(0x2E7356);
    public static readonly Vector4 GoodText = Rgb(0x1B5A38);
    public static readonly Vector4 Warn = Rgb(0x704500);
    public static readonly Vector4 Bad = Rgb(0xA43B46);
    public static readonly Vector4 BadText = Rgb(0x8A2430);
    public static readonly Vector4 Skip = Rgb(0x5C3C83);
    public static readonly Vector4 SelectionShadow = Rgb(0x47334D);
    // The portrait workspace is a warm, neutral paper surface inside the lavender information shell.
    public static readonly Vector4 PortraitWindow = Rgb(0xF8F3EE);
    public static readonly Vector4 PortraitPanel = Rgb(0xFFFDF9);
    public static readonly Vector4 PortraitRaised = Rgb(0xEFE7DF);
    public static readonly Vector4 PortraitHover = Rgb(0xE6D9CF);
    public static readonly Vector4 PortraitBorder = Rgb(0xB9A79A);
    public static readonly Vector4 White = Rgb(0xFFFFFF);

    private static Vector4 Rgb(uint value) => new(((value >> 16) & 255) / 255f,
        ((value >> 8) & 255) / 255f, (value & 255) / 255f, 1f);
}
