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
    // 取自 xiaohongshu 设计系统（深色模式）的 token：
    //   Surface #19191E（紫调近黑）/ 品牌红 #FF2E4D / 标题白 84% / 段落白 56%
    //   卡片圆角 12–16、按钮全胶囊、零阴影（层级靠间距与圆角）
    public static readonly Vector4 Card = new(0.102f, 0.102f, 0.125f, 1f);        // #1A1A20 卡片面（紫调）
    public static readonly Vector4 CardHover = new(0.122f, 0.122f, 0.149f, 1f);   // #1F1F26
    public static readonly Vector4 CardActive = new(0.137f, 0.137f, 0.169f, 1f);  // #23232B 展开态
    public static readonly Vector4 LikedRed = new(0.999f, 0.180f, 0.302f, 1f);    // #FF2E4D 已赞红心
    public static readonly Vector4 Neutral = new(1f, 1f, 1f, 0.56f);              // 段落文字（半透明白）
    public static readonly Vector4 Bright = new(1f, 1f, 1f, 0.84f);               // 标题文字
    public static readonly Vector4 Pink = new(0.999f, 0.180f, 0.302f, 1f);        // #FF2E4D 主 CTA
    public static readonly Vector4 PinkHover = new(1f, 0.278f, 0.384f, 1f);       // #FF4762 悬停提亮
    public static readonly Vector4 White = new(1f, 1f, 1f, 1f);

    /// <summary>整页圆角基调（成对调用）：卡片 12、按钮胶囊。</summary>
    public static void PushRounded()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 12f);
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
        draw.AddRectFilled(min, max, ImGui.GetColorU32(color), 14f);
        // xiaohongshu 规范：卡片不加左侧彩色描边（SaaS/dashboard 味）——展开态只用更亮的卡面区分
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
        draw.AddRectFilled(pos, pos + actual, ImGui.GetColorU32(hovered ? PinkHover : Pink), actual.Y * 0.5f);
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
