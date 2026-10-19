using System.Numerics;

namespace FireGaze.UI;

/// <summary>FireGaze's own opaque work surfaces and semantic colors. Game content keeps its original colors.</summary>
internal static class FireGazePalette
{
    // White work surfaces keep equipment, portraits and the manager icon as the colored content.
    public static readonly Vector4 Window = Rgb(0xFFFFFF);
    public static readonly Vector4 Panel = Rgb(0xFFFFFF);
    public static readonly Vector4 Raised = Rgb(0xFFFFFF);
    public static readonly Vector4 Hover = Rgb(0xF2F2F2);
    public static readonly Vector4 Selected = Rgb(0xECECEC);
    public static readonly Vector4 Text = Rgb(0x202020);
    public static readonly Vector4 Muted = Rgb(0x626262);
    public static readonly Vector4 Border = Rgb(0x858585);
    public static readonly Vector4 Action = Rgb(0x242424);
    public static readonly Vector4 ActionHover = Rgb(0x3A3A3A);
    public static readonly Vector4 ActionActive = Rgb(0x111111);
    public static readonly Vector4 Focus = Rgb(0x242424);
    public static readonly Vector4 BrandEmber = Rgb(0xD45A31);
    public static readonly Vector4 ChartRed = Rgb(0xB73E45);
    public static readonly Vector4 ChartGreen = Rgb(0x257846);
    public static readonly Vector4 ChartBlue = Rgb(0x3264B9);
    public static readonly Vector4 Good = Rgb(0x343434);
    public static readonly Vector4 GoodText = Rgb(0x1B5A38);
    public static readonly Vector4 Warn = Rgb(0x704500);
    public static readonly Vector4 Bad = Rgb(0xA43B46);
    public static readonly Vector4 BadText = Rgb(0x8A2430);
    public static readonly Vector4 Skip = Rgb(0x4C4C4C);
    // This is the opaque backing for a white selection check, not a translucent drop shadow.
    public static readonly Vector4 SelectionShadow = Rgb(0x343434);
    public static readonly Vector4 PortraitWindow = Window;
    public static readonly Vector4 PortraitPanel = Panel;
    public static readonly Vector4 PortraitRaised = Raised;
    public static readonly Vector4 PortraitHover = Hover;
    public static readonly Vector4 PortraitBorder = Border;
    public static readonly Vector4 White = Rgb(0xFFFFFF);

    private static Vector4 Rgb(uint value) => new(((value >> 16) & 255) / 255f,
        ((value >> 8) & 255) / 255f, (value & 255) / 255f, 1f);
}
