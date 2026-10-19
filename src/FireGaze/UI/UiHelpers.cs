using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>
///     界面小工具。
/// </summary>
internal static class UiHelpers
{
    public static Vector4 Muted => FireGazePalette.Muted;
    public static Vector4 Good => FireGazePalette.GoodText;
    public static Vector4 Warn => FireGazePalette.Warn;
    public static Vector4 Bad => FireGazePalette.BadText;
    public static Vector4 Info => FireGazePalette.Focus;
    public static Vector4 Accent => FireGazePalette.Focus;

    /// <summary>Use in place of the native pair so disabled input also has a distinct readable appearance.</summary>
    public static void BeginDisabled(bool disabled = true) => FireGazeDisabled.Begin(disabled);
    public static void EndDisabled() => FireGazeDisabled.End();
    public static bool IsDisabled => FireGazeDisabled.IsDisabled;

    /// <summary>Prefer this using scope when an early return or exception may leave the drawing block.</summary>
    public static IDisposable Disabled(bool disabled = true) => FireGazeDisabled.Push(disabled);

    /// <summary>
    ///     「跳过」的终态色（中文插件 · 不汉化等）；具体状态仍由文字说明。
    /// </summary>
    public static Vector4 Skip => FireGazePalette.Skip;

    public static Vector4 StatusColor(RepoStatus status) => status switch
    {
        RepoStatus.Ok => Good,
        RepoStatus.Empty => Muted,
        RepoStatus.Invalid => Warn,
        RepoStatus.Dead => Bad,
        RepoStatus.Blocked => Warn,
        RepoStatus.Unreachable => Info,
        _ => Muted,
    };

    /// <summary>
    ///     列表默认按严重度排序（死链 → 内容不合规 → 拒绝访问 → 连接失败 → 空 → 未检查 → 可用）。
    /// </summary>
    public static int SeverityRank(RepoStatus status) => status switch
    {
        RepoStatus.Dead => 0,
        RepoStatus.Invalid => 1,
        RepoStatus.Blocked => 2,
        RepoStatus.Unreachable => 3,
        RepoStatus.Empty => 4,
        RepoStatus.Unknown => 5,
        _ => 6,
    };

    public static void ColoredText(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>
    ///     主操作使用深色实底，在长列表中保持明确的操作位置。
    ///     必须配套 <see cref="PopPrimaryButton" />（Push/Pop 数量要成对）。
    /// </summary>
    public static void PushPrimaryButton()
    {
        PushSemanticButton(FireGazeButtonStyle.Primary);
    }

    public static void PopPrimaryButton() => ImGui.PopStyleColor(4);

    /// <summary>
    ///     「启用插件」这类正向动作的按钮，沿用中性深色实底。
    ///     必须配套 <see cref="PopEnableButton" />。
    /// </summary>
    public static void PushEnableButton()
    {
        PushSemanticButton(FireGazeButtonStyle.Enable);
    }

    public static void PopEnableButton() => ImGui.PopStyleColor(4);

    /// <summary>
    ///     破坏性动作的按钮（还原原文 / 删除）使用红系实底，并以操作文字说明后果。
    ///     必须配套 <see cref="PopDangerButton" />。
    /// </summary>
    public static void PushDangerButton()
    {
        PushSemanticButton(FireGazeButtonStyle.Danger);
    }

    public static void PopDangerButton() => ImGui.PopStyleColor(4);

    internal static FireGazeButtonColors SemanticButtonColors(FireGazeButtonStyle style, bool disabled)
    {
        if (disabled) return new(FireGazePalette.Hover, FireGazePalette.Hover, FireGazePalette.Hover, FireGazePalette.Muted);
        if (style == FireGazeButtonStyle.Primary)
            return new(FireGazePalette.Action, FireGazePalette.ActionHover, FireGazePalette.ActionActive, FireGazePalette.White);
        var fill = style == FireGazeButtonStyle.Enable ? FireGazePalette.Good : FireGazePalette.Bad;
        return new(fill, fill * new Vector4(.88f, .88f, .88f, 1), fill * new Vector4(.76f, .76f, .76f, 1), FireGazePalette.White);
    }

    private static void PushSemanticButton(FireGazeButtonStyle style)
    {
        var colors = SemanticButtonColors(style, IsDisabled);
        ImGui.PushStyleColor(ImGuiCol.Button, colors.Fill);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, colors.Hover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, colors.Active);
        ImGui.PushStyleColor(ImGuiCol.Text, colors.Text);
    }

    /// <summary>按钮标签的可见宽度（去掉 <c>###ID</c> 后缀）：给 <see cref="SameLineOrWrap" /> 估位置用。</summary>
    public static float LabelWidth(string label)
    {
        var index = label.IndexOf("###", StringComparison.Ordinal);
        var visible = index >= 0 ? label[..index] : label;
        return ImGui.CalcTextSize(visible).X + (ImGui.GetStyle().FramePadding.X * 2);
    }

    /// <summary>
    ///     行尾按钮的「放不下就换行」。
    ///     <para>
    ///     2026-10-03 血教训：不能用 <c>GetContentRegionAvail()</c> 判断——它在表格单元格里恒返回整列宽，
    ///     不随已用宽度变化，于是所有按钮都排在同一行、超出列裁剪区被切掉且点不到
    ///     （用户实测：插件行「一键上传」永远露不出来，改窗口大小也没用）。
    ///     改用「上一项右边界 + 当前裁剪区右边界」判断，表格单元格与普通窗口都成立。
    ///     </para>
    /// </summary>
    public static void SameLineOrWrap(float neededWidth, float spacing = 10f)
    {
        var clipRight = ImGui.GetWindowDrawList().GetClipRectMax().X;
        var lastRight = ImGui.GetItemRectMax().X;
        if (lastRight + spacing + neededWidth > clipRight)
        {
            ImGui.NewLine();
        }
        else
        {
            ImGui.SameLine(0, spacing);
        }
    }

    /// <summary>
    ///     弹窗里按钮点完要收工时用：CloseCurrentPopup 只是「标记关闭」，EndPopup 才是真的结束这一帧的弹窗——
    ///     少了它 ImGui 窗口栈失衡，下一帧会弹一串 assertion failed（2026-10-02 用户实测：“还原原文”点完报错）。
    ///     <para>调完必须立即 return，不要再往下画这个弹窗。</para>
    /// </summary>
    public static void ClosePopupAndEnd()
    {
        ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    public static void ColoredWrapped(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>
    ///     把长 URL 缩成「头…尾」的形式。
    /// </summary>
    public static string Shorten(string url, int max = 78)
    {
        if (url.Length <= max)
        {
            return url;
        }

        var head = max * 2 / 3;
        var tail = max - head - 1;
        return url[..head] + "…" + url[^tail..];
    }

    /// <summary>
    ///     算「头…尾」要保留几个字符。下限不能超过文本长度本身
    ///     —— 短文本 + 窄列时 Math.Clamp(min &gt; max) 会抛 ArgumentException，
    ///     2026-09-22 就是这个把「参与翻译」整窗炸掉。抽出来是为了能在 fgtest 里离线跑。
    /// </summary>
    internal static int KeepCount(int length, float available, float full)
    {
        if (length <= 0 || full <= 0f)
        {
            return Math.Max(length, 0);
        }

        return Math.Clamp((int)(length * (available / full)) - 2, Math.Min(8, length), length);
    }

    /// <summary>
    ///     带悬停提示的截断文本。
    /// </summary>
    public static void Truncated(string text, int max, string? tooltip = null)
    {
        ImGui.TextUnformatted(Shorten(text, max));
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(tooltip))
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 42f);
            ImGui.TextUnformatted(tooltip);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    /// <summary>
    ///     按当前单元格宽度显示文本：放得下就**完整显示**，放不下才用「头…尾」省略。
    ///     表格列宽可以拖，所以拖宽之后应该看得到完整内容，而不是不管多宽都写死省略。
    /// </summary>
    public static void Fitted(string text, string? tooltip = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var available = MathF.Max(0, MathF.Min(ImGui.GetContentRegionAvail().X,
            ImGui.GetWindowDrawList().GetClipRectMax().X - ImGui.GetCursorScreenPos().X) - 2f);
        var fitted = FitText(text, available, static value => ImGui.CalcTextSize(value).X);
        if (fitted.Length == 0) ImGui.Dummy(new Vector2(available, ImGui.GetTextLineHeight()));
        else ImGui.TextUnformatted(fitted);

        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 42f);
            ImGui.TextUnformatted(tooltip);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    internal static string FitText(string text, float available, Func<string, float> measure)
    {
        if (string.IsNullOrEmpty(text) || !float.IsFinite(available) || available <= 0) return string.Empty;
        if (measure(text) <= available) return text;
        const string ellipsis = "…";
        if (measure(ellipsis) > available) return string.Empty;
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(text);
        var low = 0;
        var high = elements.Length - 1;
        var best = ellipsis;
        while (low <= high)
        {
            var keep = (low + high) / 2;
            var head = (keep * 2 + 2) / 3;
            var tail = keep - head;
            var candidate = text[..(head == elements.Length ? text.Length : elements[head])] + ellipsis
                + (tail == 0 ? string.Empty : text[elements[elements.Length - tail]..]);
            if (measure(candidate) <= available) { best = candidate; low = keep + 1; }
            else high = keep - 1;
        }
        return best;
    }
}
