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
    public static readonly Vector4 Accent = new(0.55f, 0.75f, 1f, 1f);

    public static Vector4 StatusColor(RepoStatus status) => status switch
    {
        RepoStatus.Ok => Good,
        RepoStatus.Empty => Muted,
        RepoStatus.Invalid => Warn,
        RepoStatus.Dead => Bad,
        RepoStatus.Blocked => Warn,
        RepoStatus.Unreachable => Muted,
        _ => Muted,
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
}
