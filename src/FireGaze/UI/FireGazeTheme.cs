using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>The adapter seam lets the actual stack lifetime be verified without a game/native ImGui context.</summary>
internal interface IFireGazeStyleStack
{
    void PushColor(ImGuiCol role, Vector4 value);
    void PushFloat(ImGuiStyleVar role, float value);
    void PushVector(ImGuiStyleVar role, Vector2 value);
    void PopColors(int count);
    void PopVariables(int count);
}

/// <summary>Apply before WindowSystem.Draw/Begin, then restore the caller's complete style even on exceptions.</summary>
internal static class FireGazeTheme
{
    private static readonly ImGuiCol[] ColorRoles = Enum.GetValues<ImGuiCol>()
        .Distinct().Where(role => role.ToString() is not "Count" and not "COUNT")
        .ToArray();
    private static readonly (ImGuiCol Role, Vector4 Color)[] InformationColors =
        BuildColors(FireGazeColorDomain.Information);
    private static readonly (ImGuiCol Role, Vector4 Color)[] PortraitColors =
        BuildColors(FireGazeColorDomain.Portrait);
    internal static IReadOnlyList<(ImGuiCol Role, Vector4 Color)> Colors =>
        FireGazePalette.CurrentDomain == FireGazeColorDomain.Portrait ? PortraitColors : InformationColors;

    private static (ImGuiCol Role, Vector4 Color)[] BuildColors(FireGazeColorDomain domain)
    {
        var palette = FireGazePalette.ForDomain(domain);
        return ColorRoles.Select(role => (role, ColorFor(role.ToString(), palette))).ToArray();
    }

    // Keep the existing line metrics: list heights already derive from font/frame metrics.
    internal static readonly (ImGuiStyleVar Role, float Value, bool Scale)[] FloatStyles =
    [
        (ImGuiStyleVar.Alpha, 1f, false),
        (ImGuiStyleVar.DisabledAlpha, 1f, false),
        (ImGuiStyleVar.WindowRounding, 8f, true),
        (ImGuiStyleVar.WindowBorderSize, 1f, true),
        (ImGuiStyleVar.ChildRounding, 6f, true),
        (ImGuiStyleVar.ChildBorderSize, 1f, true),
        (ImGuiStyleVar.PopupRounding, 6f, true),
        (ImGuiStyleVar.PopupBorderSize, 1f, true),
        (ImGuiStyleVar.FrameRounding, 4f, true),
        (ImGuiStyleVar.FrameBorderSize, 1f, true),
        (ImGuiStyleVar.IndentSpacing, 21f, true),
        (ImGuiStyleVar.ScrollbarSize, 14f, true),
        (ImGuiStyleVar.ScrollbarRounding, 6f, true),
        (ImGuiStyleVar.GrabMinSize, 12f, true),
        (ImGuiStyleVar.GrabRounding, 3f, true),
        (ImGuiStyleVar.TabRounding, 4f, true),
    ];

    internal static readonly (ImGuiStyleVar Role, Vector2 Value, bool Scale)[] VectorStyles =
    [
        (ImGuiStyleVar.WindowPadding, new(10, 10), true),
        (ImGuiStyleVar.WindowMinSize, new(32, 32), true),
        (ImGuiStyleVar.WindowTitleAlign, new(0, .5f), false),
        (ImGuiStyleVar.FramePadding, new(4, 3), true),
        (ImGuiStyleVar.ItemSpacing, new(8, 4), true),
        (ImGuiStyleVar.ItemInnerSpacing, new(4, 4), true),
        (ImGuiStyleVar.CellPadding, new(4, 2), true),
        (ImGuiStyleVar.ButtonTextAlign, new(.5f, .5f), false),
        (ImGuiStyleVar.SelectableTextAlign, new(0, .5f), false),
    ];

    public static IDisposable Push() => Push(NativeStyleStack.Instance, ImGui.GetFontSize() / 17f);

    public static void Draw(Action draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        using var scope = Push();
        draw();
    }

    /// <summary>Complete white portrait domain, including every floating tool and semantic consumer.</summary>
    public static IDisposable PushPortrait() => PushPortrait(NativeStyleStack.Instance);

    internal static IDisposable PushPortrait(IFireGazeStyleStack stack)
    {
        ArgumentNullException.ThrowIfNull(stack);
        return new OverrideScope(stack, FireGazeColorDomain.Portrait);
    }

    internal static void Draw(Action draw, IFireGazeStyleStack stack, float scale)
    {
        ArgumentNullException.ThrowIfNull(draw);
        using var scope = Push(stack, scale);
        draw();
    }

    internal static IDisposable Push(IFireGazeStyleStack stack, float scale)
    {
        ArgumentNullException.ThrowIfNull(stack);
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        return new StyleScope(stack, scale);
    }

    private static Vector4 ColorFor(string role, FireGazePalette.Colors palette) => role switch
    {
        "Text" => palette.Text,
        "TextDisabled" => palette.Muted,
        "WindowBg" or "TitleBg" or "TitleBgCollapsed" or "DockingEmptyBg" => palette.Window,
        "ChildBg" or "MenuBarBg" or "TableRowBg" => palette.Panel,
        "PopupBg" or "FrameBg" or "Button" => palette.Raised,
        "TitleBgActive" or "TableHeaderBg" or "TableRowBgAlt" or "ScrollbarBg" => palette.Hover,
        "Border" or "Separator" or "TableBorderStrong" or "ResizeGrip" => palette.Border,
        "TableBorderLight" => palette.Hover,
        "BorderShadow" or "NavWindowingDimBg" or "ModalWindowDimBg" => Vector4.Zero,
        "FrameBgHovered" or "ButtonHovered" or "HeaderHovered" or "TabHovered" => palette.Hover,
        "FrameBgActive" or "ButtonActive" or "HeaderActive" or "Header" or "TextSelectedBg" or "DockingPreview" => palette.Selected,
        "ScrollbarGrab" => palette.Border,
        "ScrollbarGrabHovered" or "SliderGrab" or "CheckMark" or "PlotLines" => palette.Focus,
        "ScrollbarGrabActive" or "SliderGrabActive" or "SeparatorHovered" or "PlotLinesHovered" => palette.Focus,
        "SeparatorActive" => palette.Focus,
        "ResizeGripHovered" or "ResizeGripActive" => palette.Focus,
        "Tab" or "TabUnfocused" or "TabDimmed" => palette.Window,
        "TabActive" or "TabUnfocusedActive" or "TabSelected" or "TabDimmedSelected" => palette.Selected,
        "TabSelectedOverline" or "TabDimmedSelectedOverline" or "TextLink" or "NavCursor" or "NavHighlight"
            or "NavWindowingHighlight" or "DragDropTarget" => palette.Focus,
        "PlotHistogram" => palette.GoodText,
        "PlotHistogramHovered" => palette.Focus,
        // A future binding can add roles. Keep them readable and fixed rather than inherit a host color.
        _ => palette.Panel,
    };

    private sealed class StyleScope : IDisposable
    {
        private readonly IFireGazeStyleStack stack;
        private readonly IDisposable domain;
        private int colors;
        private int variables;
        private bool disposed;

        public StyleScope(IFireGazeStyleStack stack, float scale)
        {
            this.stack = stack;
            domain = FireGazePalette.Enter(FireGazeColorDomain.Information);
            try
            {
                foreach (var entry in Colors)
                {
                    stack.PushColor(entry.Role, entry.Color);
                    colors++;
                }
                foreach (var entry in FloatStyles)
                {
                    stack.PushFloat(entry.Role, entry.Scale ? entry.Value * scale : entry.Value);
                    variables++;
                }
                foreach (var entry in VectorStyles)
                {
                    stack.PushVector(entry.Role, entry.Scale ? entry.Value * scale : entry.Value);
                    variables++;
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                try
                {
                    if (variables > 0) stack.PopVariables(variables);
                }
                finally
                {
                    if (colors > 0) stack.PopColors(colors);
                }
            }
            finally
            {
                domain.Dispose();
            }
        }
    }

    private sealed class OverrideScope : IDisposable
    {
        private readonly IFireGazeStyleStack stack;
        private readonly IDisposable domain;
        private readonly int count;
        private bool disposed;

        public OverrideScope(IFireGazeStyleStack stack, FireGazeColorDomain desiredDomain)
        {
            this.stack = stack;
            domain = FireGazePalette.Enter(desiredDomain);
            try
            {
                foreach (var (role, color) in Colors) { stack.PushColor(role, color); count++; }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (count > 0) stack.PopColors(count);
            }
            finally
            {
                domain.Dispose();
            }
        }
    }

    private sealed class NativeStyleStack : IFireGazeStyleStack
    {
        public static readonly NativeStyleStack Instance = new();
        public void PushColor(ImGuiCol role, Vector4 value) => ImGui.PushStyleColor(role, value);
        public void PushFloat(ImGuiStyleVar role, float value) => ImGui.PushStyleVar(role, value);
        public void PushVector(ImGuiStyleVar role, Vector2 value) => ImGui.PushStyleVar(role, value);
        public void PopColors(int count) => ImGui.PopStyleColor(count);
        public void PopVariables(int count) => ImGui.PopStyleVar(count);
    }
}
