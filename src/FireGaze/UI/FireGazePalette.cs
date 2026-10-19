using System.Numerics;

namespace FireGaze.UI;

internal enum FireGazeColorDomain { Information, Portrait }

/// <summary>Semantic colors resolve at use time so independent window domains cannot cache each other.</summary>
internal static class FireGazePalette
{
    [ThreadStatic] private static FireGazeColorDomain domain;

    private static readonly Colors Information = new()
    {
        Window = Rgb(0x202C37), Panel = Rgb(0x202C37), Raised = Rgb(0x273643),
        Hover = Rgb(0x304352), Selected = Rgb(0x364C5E), Text = Rgb(0xF3F5F7),
        Muted = Rgb(0xB9C5CF), Border = Rgb(0x8A9CA9), Action = Rgb(0x365B78),
        ActionHover = Rgb(0x416C8D), ActionActive = Rgb(0x2E4D66), Focus = Rgb(0xC4D9E8),
        BrandEmber = Rgb(0xDB7650), ChartRed = Rgb(0xF0AFB8), ChartGreen = Rgb(0xA8D7BC),
        ChartBlue = Rgb(0xA7C9EB), Good = Rgb(0x315B47), GoodText = Rgb(0xA8D7BC),
        Warn = Rgb(0xE9C590), Bad = Rgb(0x773F47), BadText = Rgb(0xF0AFB8),
        Skip = Rgb(0xB9C5CF), SelectionShadow = Rgb(0x343434),
    };
    private static readonly Colors Portrait = new()
    {
        Window = Rgb(0xFFFFFF), Panel = Rgb(0xFFFFFF), Raised = Rgb(0xFFFFFF),
        Hover = Rgb(0xF2F2F2), Selected = Rgb(0xECECEC), Text = Rgb(0x202020),
        Muted = Rgb(0x626262), Border = Rgb(0x858585), Action = Rgb(0x242424),
        ActionHover = Rgb(0x3A3A3A), ActionActive = Rgb(0x111111), Focus = Rgb(0x242424),
        BrandEmber = Rgb(0xD45A31), ChartRed = Rgb(0xB73E45), ChartGreen = Rgb(0x257846),
        ChartBlue = Rgb(0x3264B9), Good = Rgb(0x343434), GoodText = Rgb(0x1B5A38),
        Warn = Rgb(0x704500), Bad = Rgb(0xA43B46), BadText = Rgb(0x8A2430),
        Skip = Rgb(0x4C4C4C), SelectionShadow = Rgb(0x343434),
    };

    internal static FireGazeColorDomain CurrentDomain => domain;
    internal static Colors ForDomain(FireGazeColorDomain value) => value switch
    {
        FireGazeColorDomain.Information => Information,
        FireGazeColorDomain.Portrait => Portrait,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static Colors Current => ForDomain(domain);
    internal static IDisposable Enter(FireGazeColorDomain value)
    {
        _ = ForDomain(value);
        return new DomainScope(value);
    }

    public static Vector4 Window => Current.Window;
    public static Vector4 Panel => Current.Panel;
    public static Vector4 Raised => Current.Raised;
    public static Vector4 Hover => Current.Hover;
    public static Vector4 Selected => Current.Selected;
    public static Vector4 Text => Current.Text;
    public static Vector4 Muted => Current.Muted;
    public static Vector4 Border => Current.Border;
    public static Vector4 Action => Current.Action;
    public static Vector4 ActionHover => Current.ActionHover;
    public static Vector4 ActionActive => Current.ActionActive;
    public static Vector4 Focus => Current.Focus;
    public static Vector4 BrandEmber => Current.BrandEmber;
    public static Vector4 ChartRed => Current.ChartRed;
    public static Vector4 ChartGreen => Current.ChartGreen;
    public static Vector4 ChartBlue => Current.ChartBlue;
    public static Vector4 Good => Current.Good;
    public static Vector4 GoodText => Current.GoodText;
    public static Vector4 Warn => Current.Warn;
    public static Vector4 Bad => Current.Bad;
    public static Vector4 BadText => Current.BadText;
    public static Vector4 Skip => Current.Skip;
    public static Vector4 SelectionShadow => Current.SelectionShadow;
    public static Vector4 PortraitWindow => Portrait.Window;
    public static Vector4 PortraitPanel => Portrait.Panel;
    public static Vector4 PortraitRaised => Portrait.Raised;
    public static Vector4 PortraitHover => Portrait.Hover;
    public static Vector4 PortraitBorder => Portrait.Border;
    public static Vector4 White => Vector4.One;

    internal sealed record Colors
    {
        public required Vector4 Window { get; init; }
        public required Vector4 Panel { get; init; }
        public required Vector4 Raised { get; init; }
        public required Vector4 Hover { get; init; }
        public required Vector4 Selected { get; init; }
        public required Vector4 Text { get; init; }
        public required Vector4 Muted { get; init; }
        public required Vector4 Border { get; init; }
        public required Vector4 Action { get; init; }
        public required Vector4 ActionHover { get; init; }
        public required Vector4 ActionActive { get; init; }
        public required Vector4 Focus { get; init; }
        public required Vector4 BrandEmber { get; init; }
        public required Vector4 ChartRed { get; init; }
        public required Vector4 ChartGreen { get; init; }
        public required Vector4 ChartBlue { get; init; }
        public required Vector4 Good { get; init; }
        public required Vector4 GoodText { get; init; }
        public required Vector4 Warn { get; init; }
        public required Vector4 Bad { get; init; }
        public required Vector4 BadText { get; init; }
        public required Vector4 Skip { get; init; }
        public required Vector4 SelectionShadow { get; init; }
    }

    private sealed class DomainScope : IDisposable
    {
        private readonly FireGazeColorDomain previous = domain;
        private bool disposed;
        public DomainScope(FireGazeColorDomain value) => domain = value;
        public void Dispose()
        {
            if (disposed) return;
            domain = previous;
            disposed = true;
        }
    }

    private static Vector4 Rgb(uint value) => new(((value >> 16) & 255) / 255f,
        ((value >> 8) & 255) / 255f, (value & 255) / 255f, 1f);
}
