using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>
///     界面小工具。
/// </summary>
internal static class UiHelpers
{
    public static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);
    public static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    public static readonly Vector4 Warn = new(0.95f, 0.75f, 0.35f, 1f);
    public static readonly Vector4 Bad = new(0.95f, 0.45f, 0.45f, 1f);
    public static readonly Vector4 Info = new(0.62f, 0.76f, 0.94f, 1f);
    public static readonly Vector4 Accent = new(0.55f, 0.75f, 1f, 1f);

    /// <summary>
    ///     「跳过」的终态色（中文插件 · 不汉化等）：特意不用 Info 蓝——蓝在本页签表示「有进度/待处理」，
    ///     复用会让「不汉化」被读成「正在处理」（2026-10-02 v4 定向复评 F8）。
    /// </summary>
    public static readonly Vector4 Skip = new(0.78f, 0.70f, 0.95f, 1f);

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
    ///     「要做的事」那种主按钮：比普通按钮深一点、蓝一点，198 行列表里能一眼看到落点。
    ///     必须配套 <see cref="PopPrimaryButton" />（Push/Pop 数量要成对）。
    /// </summary>
    public static void PushPrimaryButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.23f, 0.37f, 0.55f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.28f, 0.44f, 0.64f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.20f, 0.33f, 0.50f, 1f));
    }

    public static void PopPrimaryButton() => ImGui.PopStyleColor(3);

    /// <summary>
    ///     「启用插件」这类正向动作的按钮：偏绿，与主色（蓝）区分，用户能一眼看到。
    ///     必须配套 <see cref="PopEnableButton" />。
    /// </summary>
    public static void PushEnableButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.42f, 0.28f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.52f, 0.34f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.15f, 0.36f, 0.24f, 1f));
    }

    public static void PopEnableButton() => ImGui.PopStyleColor(3);

    /// <summary>
    ///     破坏性动作的按钮（还原原文 / 删除）：红系实底，与主色（蓝）、启用（绿）一眼区分。
    ///     跨页规则（2026-10-03 HCI 评审）：主操作=蓝（<see cref="PushPrimaryButton" />）、正向启用=绿、
    ///     破坏动作=红；橙色只用于警示文字/徽标，不做按钮底色。必须配套 <see cref="PopDangerButton" />。
    /// </summary>
    public static void PushDangerButton()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.20f, 0.20f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.66f, 0.25f, 0.25f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.48f, 0.17f, 0.17f, 1f));
    }

    public static void PopDangerButton() => ImGui.PopStyleColor(3);

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

        var available = ImGui.GetContentRegionAvail().X - 2f;
        var full = ImGui.CalcTextSize(text).X;

        if (available <= 1f || full <= available)
        {
            ImGui.TextUnformatted(text);
        }
        else
        {
            // 按「总宽 / 可用宽」的比例估算能放几个字符，并给省略号留位。
            var keep = KeepCount(text.Length, available, full);
            var candidate = Shorten(text, keep);

            // 中英混排时估算可能偏宽：实测一次，还超就再收一轮
            var width = ImGui.CalcTextSize(candidate).X;
            if (width > available)
            {
                keep = KeepCount(candidate.Length, available, width);
                candidate = Shorten(text, keep);
            }

            ImGui.TextUnformatted(candidate);
        }

        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 42f);
            ImGui.TextUnformatted(tooltip);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
}
