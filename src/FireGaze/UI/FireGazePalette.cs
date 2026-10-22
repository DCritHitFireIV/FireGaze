using System.Numerics;

namespace FireGaze.UI;

internal enum FireGazeColorDomain { Information, Portrait }

/// <summary>Semantic colors resolve at use time so independent window domains cannot cache each other.</summary>
internal static class FireGazePalette
{
    [ThreadStatic] private static FireGazeColorDomain domain;

    private static readonly Colors Information = new()
    {
        Window = Rgb(0xEEF1F4), Panel = Rgb(0xF3F5F7), Raised = Rgb(0xF7F8FA),
        Hover = Rgb(0xE8E8E8), Selected = Rgb(0xD6E3EE), Text = Rgb(0x202A32),
        Muted = Rgb(0x536370), Border = Rgb(0x697F90), Action = Rgb(0x3B5F7B),
        ActionHover = Rgb(0x34546D), ActionActive = Rgb(0x294458), Focus = Rgb(0x365D7A),
        BrandEmber = Rgb(0xD45A31), ChartRed = Rgb(0xA84450), ChartGreen = Rgb(0x326A4C),
        ChartBlue = Rgb(0x3D6482), Good = Rgb(0x3C614C), GoodText = Rgb(0x2F6247),
        Warn = Rgb(0x755420), Bad = Rgb(0x834C55), BadText = Rgb(0x8D3F4B),
        Skip = Rgb(0x536370), SelectionShadow = Rgb(0x343434),
    };
    private static readonly Colors Portrait = new()
    {
        Window = Rgb(0xF4F5F7), Panel = Rgb(0xFCFCFD), Raised = Rgb(0xFAFAFB),
        Hover = Rgb(0xECECEC), Selected = Rgb(0xF2DDD9), Text = Rgb(0x292D32),
        Muted = Rgb(0x59616B), Border = Rgb(0x747C86), Action = Rgb(0xBA4A40),
        ActionHover = Rgb(0xA84037), ActionActive = Rgb(0x92382F), Focus = Rgb(0xA84037),
        BrandEmber = Rgb(0xD45A31), ChartRed = Rgb(0xA84450), ChartGreen = Rgb(0x326A4C),
        ChartBlue = Rgb(0x3D6482), Good = Rgb(0x3C614C), GoodText = Rgb(0x2F6247),
        Warn = Rgb(0x755420), Bad = Rgb(0x834C55), BadText = Rgb(0x8D3F4B),
        Skip = Rgb(0x536370), SelectionShadow = Rgb(0x343434),
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
