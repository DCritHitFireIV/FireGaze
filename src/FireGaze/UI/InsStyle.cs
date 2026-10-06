using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>
///     ins 风（Instagram 设计语言）在 ImGui 里的克制移植：圆角、卡片面色差、圆形头像、
///     一屏一处渐变（展开行的左侧条）、已赞红心、粉色 CTA。
///     游戏内不刷浅色底——搬的是结构，不是白底。守则见 ~/.pi/agent/skills/ins-style-ui/SKILL.md。
/// </summary>
internal static class InsStyle
{
    public static readonly Vector4 Card = new(0.12f, 0.12f, 0.12f, 1f);          // #1F1F1F 卡片面
    public static readonly Vector4 CardHover = new(0.145f, 0.145f, 0.15f, 1f);
    public static readonly Vector4 CardActive = new(0.16f, 0.16f, 0.17f, 1f);
    public static readonly Vector4 LikedRed = new(0.929f, 0.286f, 0.337f, 1f);    // #ED4956 已赞红心
    public static readonly Vector4 Neutral = new(0.66f, 0.66f, 0.66f, 1f);
    public static readonly Vector4 Bright = new(0.95f, 0.95f, 0.95f, 1f);
    public static readonly Vector4 Pink = new(0.757f, 0.208f, 0.518f, 1f);        // #C13584（白字对比 ≈5.2:1）
    public static readonly Vector4 PinkHover = new(0.882f, 0.188f, 0.424f, 1f);   // #E1306C
    public static readonly Vector4 White = new(1f, 1f, 1f, 1f);

    private static readonly Vector4[] Gradient =
    [
        new(0.514f, 0.227f, 0.706f, 1f),   // #833AB4
        new(0.882f, 0.188f, 0.424f, 1f),   // #E1306C
        new(0.992f, 0.604f, 0.235f, 1f),   // #FD9A3C
    ];

    /// <summary>整页圆角基调（成对调用）。</summary>
    public static void PushRounded()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 8f);
    }

    public static void PopRounded() => ImGui.PopStyleVar(3);

    /// <summary>
    ///     画一行卡片底。跨列绘制前调用方要自己 <c>PushClipRect(min, max, false)</c> 顶掉单元格裁剪。
    ///     展开态额外在左侧画 3px 的 IG 渐变竖条（一屏只此一处渐变）。
    /// </summary>
    public static void DrawCard(Vector2 min, Vector2 max, bool active, bool hovered)
    {
        var draw = ImGui.GetWindowDrawList();
        var color = active ? CardActive : hovered ? CardHover : Card;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(color), 12f);

        if (!active)
        {
            return;
        }

        var stripMin = new Vector2(min.X + 2f, min.Y + 8f);
        var stripMax = new Vector2(min.X + 5f, max.Y - 8f);
        if (stripMax.Y - stripMin.Y < 6f)
        {
            return;
        }

        var third = (stripMax.Y - stripMin.Y) / 3f;
        var top = stripMin.Y;
        var c0 = ImGui.GetColorU32(Gradient[0]);
        var c1 = ImGui.GetColorU32(Gradient[1]);
        var c2 = ImGui.GetColorU32(Gradient[2]);
        draw.AddRectFilledMultiColor(stripMin, new Vector2(stripMax.X, top + third), c0, c0, c1, c1);
        draw.AddRectFilledMultiColor(
            new Vector2(stripMin.X, top + third), new Vector2(stripMax.X, top + (third * 2f)), c1, c1, c1, c1);
        draw.AddRectFilledMultiColor(
            new Vector2(stripMin.X, top + (third * 2f)), stripMax, c1, c1, c2, c2);
    }

    /// <summary>IG 粉 CTA（圆角胶囊、白字）；返回是否点击。宽度按标签 + 内边距实算。</summary>
    public static bool PinkButton(string label, float width)
    {
        var visible = VisibleLabel(label);
        var size = new Vector2(width, 0f);
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(label, size);
        var hovered = ImGui.IsItemHovered();
        var actual = new Vector2(width, ImGui.GetFrameHeight());
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + actual, ImGui.GetColorU32(hovered ? PinkHover : Pink), 10f);
        var textSize = ImGui.CalcTextSize(visible);
        draw.AddText(pos + ((actual - textSize) * 0.5f), ImGui.GetColorU32(White), visible);
        return clicked;
    }

    public static float PinkButtonWidth(string label)
        => ImGui.CalcTextSize(VisibleLabel(label)).X + (ImGui.GetStyle().FramePadding.X * 2f) + 6f;

    /// <summary>去掉 <c>###ID</c> 后缀，只留给用户看的字。</summary>
    private static string VisibleLabel(string label)
    {
        var index = label.IndexOf("###", StringComparison.Ordinal);
        return index >= 0 ? label[..index] : label;
    }

    /// <summary>红心按钮：已赞 #ED4956、未赞中性、悬停提亮；命中区不小于 40px。已赞时点击不再触发。</summary>
    public static bool HeartButton(string id, bool liked, bool unsynced, float width)
    {
        var size = new Vector2(width, ImGui.GetFrameHeight());
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var color = liked ? LikedRed : hovered ? Bright : Neutral;
        var text = unsynced ? "♥ *" : "♥";
        var textSize = ImGui.CalcTextSize(text);
        draw.AddText(pos + ((size - textSize) * 0.5f), ImGui.GetColorU32(color), text);
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
            size / 2f);
        return true;
    }
}
