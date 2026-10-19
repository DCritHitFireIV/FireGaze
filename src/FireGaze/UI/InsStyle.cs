using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>
///     Existing list helpers keep their call sites and item IDs while using FireGaze's shared workbench.
/// </summary>
internal static class InsStyle
{
    public static readonly Vector4 Card = FireGazePalette.Panel;
    public static readonly Vector4 CardHover = FireGazePalette.Hover;
    public static readonly Vector4 CardActive = FireGazePalette.Selected;
    public static readonly Vector4 LikedRed = FireGazePalette.BadText;
    public static readonly Vector4 Neutral = FireGazePalette.Muted;
    public static readonly Vector4 Bright = FireGazePalette.Text;
    // Historical helper names remain source compatible; the primary action uses the shared neutral fill.
    public static readonly Vector4 Pink = FireGazePalette.Action;
    public static readonly Vector4 PinkHover = FireGazePalette.ActionHover;
    public static readonly Vector4 White = FireGazePalette.White;
    internal static readonly Vector4 AvatarText = FireGazePalette.Focus;

    /// <summary>Local list rounding; no line metrics or global style are changed.</summary>
    public static void PushRounded()
    {
        var scale = ImGui.GetFontSize() / 17f;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 6f * scale);
    }

    public static void PopRounded() => ImGui.PopStyleVar(3);

    /// <summary>
    ///     画一行资料底。跨列绘制仍由调用方管理单元格裁剪。
    /// </summary>
    public static void DrawCard(Vector2 min, Vector2 max, bool active, bool hovered)
    {
        var draw = ImGui.GetWindowDrawList();
        var color = active ? CardActive : hovered ? CardHover : Card;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(color), 6f * ImGui.GetFontSize() / 17f);
    }

    /// <summary>Primary action retains the original ID; native Button supplies focus and disabled handling.</summary>
    public static bool PinkButton(string label, float width)
    {
        var actual = new Vector2(width, ImGui.GetFrameHeight());
        UiHelpers.PushPrimaryButton();
        try
        {
            var clicked = ImGui.Button(label, actual);
            if (ImGui.IsItemFocused()) FireGazeSymbols.DrawFocus(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            return clicked;
        }
        finally { UiHelpers.PopPrimaryButton(); }
    }

    public static float PinkButtonWidth(string label)
        => ImGui.CalcTextSize(VisibleLabel(label)).X + (ImGui.GetStyle().FramePadding.X * 2f) + 6f;

    /// <summary>去掉 <c>###ID</c> 后缀，只留给用户看的字。</summary>
    private static string VisibleLabel(string label)
    {
        var index = label.IndexOf("###", StringComparison.Ordinal);
        return index >= 0 ? label[..index] : label;
    }

    /// <summary>Heart keeps its original item ID and hit region while gaining native navigation/disabled behavior.</summary>
    public static bool HeartButton(string id, bool liked, bool unsynced, float width)
    {
        var size = new Vector2(width, ImGui.GetFrameHeight());
        var pos = ImGui.GetCursorScreenPos();
        bool clicked;
        using (UiHelpers.Disabled(liked))
        {
            // Repaint the native label below, keeping the exact original ID instead of renaming it to a glyph.
            clicked = ImGui.Button(id, size);
        }
        var hovered = ImGui.IsItemHovered();
        var focused = ImGui.IsItemFocused();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(hovered ? FireGazePalette.Hover : FireGazePalette.Panel),
            4f * ImGui.GetFontSize() / 17f);
        var color = liked ? LikedRed : hovered ? Bright : Neutral;
        var text = unsynced ? "♥ *" : "♥";
        var textSize = ImGui.CalcTextSize(text);
        draw.AddText(pos + ((size - textSize) * 0.5f), ImGui.GetColorU32(color), text);
        if (focused) FireGazeSymbols.DrawFocus(pos, pos + size);
        return clicked && !liked;
    }

    /// <summary>圆形头像（图片）；纹理为空返回 false，交调用方画字母圆。</summary>
    public static bool DrawRoundIcon(ImTextureID texture, Vector2 pos, float size)
    {
        if (texture.IsNull)
        {
            return false;
        }

        ImGui.GetWindowDrawList().AddImageRounded(
            texture,
            pos,
            pos + new Vector2(size, size),
            new Vector2(0f, 0f),
            new Vector2(1f, 1f),
            ImGui.GetColorU32(White),
            6f * ImGui.GetFontSize() / 17f);
        return true;
    }

    /// <summary>
    ///     字母头像（无图插件的占位）：「插件汉化」与「插件发现」共用同一个形状与配色，
    ///     两个页签的头像列看起来是同一套东西。
    /// </summary>
    public static void DrawLetterAvatar(string name, float size)
    {
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            start,
            start + new Vector2(size, size),
            ImGui.GetColorU32(FireGazePalette.Selected),
            6f * ImGui.GetFontSize() / 17f);

        var initial = string.IsNullOrEmpty(name) ? "?" : System.Globalization.StringInfo.GetNextTextElement(name).ToUpperInvariant();
        var textSize = ImGui.CalcTextSize(initial);
        drawList.AddText(
            start + ((new Vector2(size, size) - textSize) * 0.5f),
            ImGui.GetColorU32(AvatarText),
            initial);

        ImGui.Dummy(new Vector2(size, size));
    }
}
