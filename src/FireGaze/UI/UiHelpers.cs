using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>界面小工具。</summary>
internal static class UiHelpers
{
    public static readonly Vector4 Muted = new(0.65f, 0.65f, 0.65f, 1f);
    public static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    public static readonly Vector4 Warn = new(0.95f, 0.75f, 0.35f, 1f);
    public static readonly Vector4 Bad = new(0.95f, 0.45f, 0.45f, 1f);
    public static readonly Vector4 Info = new(0.62f, 0.76f, 0.94f, 1f);
    public static readonly Vector4 Accent = new(0.55f, 0.75f, 1f, 1f);

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

    /// <summary>列表默认按严重度排序（死链 → 内容不合规 → 拒绝访问 → 连接失败 → 空 → 未检查 → 可用）。</summary>
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

    public static void ColoredWrapped(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>把长 URL 缩成「头…尾」的形式。</summary>
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

    /// <summary>带悬停提示的截断文本。</summary>
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
    /// 按当前单元格宽度显示文本：放得下就**完整显示**，放不下才用「头…尾」省略。
    /// 表格列宽可以拖，所以拖宽之后应该看得到完整内容，而不是不管多宽都写死省略。
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
            // 按「总宽 / 可用宽」的比例估算能放几个字符，并给省略号留位
            var keep = Math.Clamp((int)(text.Length * (available / full)) - 2, 8, text.Length);
            var candidate = Shorten(text, keep);

            // 中英混排时估算可能偏宽：实测一次，还超就再收一轮
            var width = ImGui.CalcTextSize(candidate).X;
            if (width > available)
            {
                keep = Math.Clamp((int)(candidate.Length * (available / width)) - 2, 8, candidate.Length);
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
