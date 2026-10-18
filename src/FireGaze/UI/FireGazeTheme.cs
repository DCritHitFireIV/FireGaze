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
    internal static readonly (ImGuiCol Role, Vector4 Color)[] Colors = Enum.GetValues<ImGuiCol>()
        .Distinct().Where(role => role.ToString() is not "Count" and not "COUNT")
        .Select(role => (role, ColorFor(role.ToString()))).ToArray();

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

    private static Vector4 ColorFor(string role) => role switch
    {
        "Text" => FireGazePalette.Text,
        "TextDisabled" => FireGazePalette.Muted,
        "WindowBg" or "TitleBg" or "TitleBgCollapsed" or "DockingEmptyBg" => FireGazePalette.Window,
        "ChildBg" or "MenuBarBg" or "TableRowBg" => FireGazePalette.Panel,
        "PopupBg" or "FrameBg" or "Button" => FireGazePalette.Raised,
        "TitleBgActive" or "TableHeaderBg" or "TableRowBgAlt" or "ScrollbarBg" => FireGazePalette.Hover,
        "Border" or "Separator" or "TableBorderStrong" or "ResizeGrip" => FireGazePalette.Border,
        "TableBorderLight" => FireGazePalette.Hover,
        "BorderShadow" or "NavWindowingDimBg" or "ModalWindowDimBg" => Vector4.Zero,
        "FrameBgHovered" or "ButtonHovered" or "HeaderHovered" or "TabHovered" => FireGazePalette.Hover,
        "FrameBgActive" or "ButtonActive" or "HeaderActive" or "Header" or "TextSelectedBg" or "DockingPreview" => FireGazePalette.Selected,
        "ScrollbarGrab" => FireGazePalette.Border,
        "ScrollbarGrabHovered" or "SliderGrab" or "CheckMark" or "PlotLines" => FireGazePalette.Focus,
        "ScrollbarGrabActive" or "SliderGrabActive" or "SeparatorHovered" or "PlotLinesHovered" => FireGazePalette.Focus,
        "SeparatorActive" => FireGazePalette.Focus,
        "ResizeGripHovered" or "ResizeGripActive" => FireGazePalette.Focus,
        "Tab" or "TabUnfocused" or "TabDimmed" => FireGazePalette.Window,
        "TabActive" or "TabUnfocusedActive" or "TabSelected" or "TabDimmedSelected" => FireGazePalette.Selected,
        "TabSelectedOverline" or "TabDimmedSelectedOverline" or "TextLink" or "NavCursor" or "NavHighlight"
            or "NavWindowingHighlight" or "DragDropTarget" => FireGazePalette.Focus,
        "PlotHistogram" => FireGazePalette.GoodText,
        "PlotHistogramHovered" => FireGazePalette.Focus,
        // A future binding can add roles. Keep them readable and fixed rather than inherit a host color.
        _ => FireGazePalette.Panel,
    };

    private sealed class StyleScope : IDisposable
    {
        private readonly IFireGazeStyleStack stack;
        private int colors;
        private int variables;
        private bool disposed;

        public StyleScope(IFireGazeStyleStack stack, float scale)
        {
            this.stack = stack;
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
                if (variables > 0) stack.PopVariables(variables);
            }
            finally
            {
                if (colors > 0) stack.PopColors(colors);
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
