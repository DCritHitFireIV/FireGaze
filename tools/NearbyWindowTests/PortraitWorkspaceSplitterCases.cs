using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.PortraitPreview;

internal static class PortraitWorkspaceSplitterCases
{
    internal static void Run(Action<bool, string> check)
    {
        var window = new PortraitPreviewWindow();
        var origin = new Vector2(100);
        var area = new Vector2(1400, 700);
        ImGui.ResetImageInteraction(); ImGui.ResetItems(1400, 1);
        var start = window.WorkspaceLayoutForTest(area);
        ImGui.WorkspaceActiveSplitterId = "###PortraitWorkspaceSplitterNavigation";
        ImGui.ImageIO.MouseDelta = new(10000, 0);
        window.DrawWorkspaceSplittersForTest(origin, start);
        var limit = window.WorkspaceLayoutForTest(area);
        check(limit.Image.Width == 220 && limit.Options == start.Options,
            "actual navigation divider changes only its adjacent columns and stops at the photo minimum");
        check(ImGui.LastMouseCursor == ImGuiMouseCursor.ResizeEw,
            "active divider uses the horizontal resize cursor");
        ImGui.ResetItems(1400, 1); ImGui.ImageIO.MouseDelta = new(-10, 0);
        window.DrawWorkspaceSplittersForTest(origin, limit);
        var reverse = window.WorkspaceLayoutForTest(area);
        check(reverse.Navigation.Width == limit.Navigation.Width - 10 && reverse.Image.Width == 230,
            "actual reverse drag responds on the first frame after a width limit");

        ImGui.ResetImageInteraction(); ImGui.ResetItems(1400, 1);
        ImGui.WorkspaceActiveSplitterId = "###PortraitWorkspaceSplitterImage";
        ImGui.ImageIO.MouseDelta = new(10000, 0);
        window.DrawWorkspaceSplittersForTest(origin, reverse);
        var right = window.WorkspaceLayoutForTest(area);
        check(right.Navigation == reverse.Navigation && right.Options.Width == 320,
            "actual photo divider protects filter width without moving the navigation column");
        ImGui.ResetImageInteraction(); ImGui.ResetItems(1400, 1);
        var shortLayout = window.WorkspaceLayoutForTest(new(1400, 100));
        window.DrawWorkspaceSplittersForTest(origin, shortLayout);
        var targets = ImGui.Items.Where(item => item.Label.StartsWith("###PortraitWorkspaceSplitter", StringComparison.Ordinal)).ToArray();
        check(targets.Length == 2 && targets[0].Minimum.X >= origin.X + shortLayout.Navigation.Width
            && targets[0].Maximum.X <= origin.X + shortLayout.Image.X
            && targets[1].Minimum.X >= origin.X + shortLayout.Image.X + shortLayout.Image.Width
            && targets[1].Maximum.X <= origin.X + shortLayout.Options.X,
            "divider hit targets stay inside the actual smaller gap in a short viewport");
        ImGui.ResetItems(700, 1);
        window.DrawWorkspaceSplittersForTest(origin, window.WorkspaceLayoutForTest(new(700, 700)));
        check(!ImGui.Items.Any(item => item.Label.StartsWith("###PortraitWorkspaceSplitter", StringComparison.Ordinal)),
            "stacked narrow layout submits no stray column drag targets");
        check(window.WorkspaceLayoutForTest(area) == right,
            "temporary narrow and short viewports do not overwrite the player's stored column widths");
        ImGui.ResetItems(1400, 1, "隐藏铭牌画面###PortraitToggleImage"); window.DrawImageToggleForTest();
        var hidden = window.WorkspaceLayoutForTest(area);
        ImGui.ResetImageInteraction(); ImGui.ResetItems(1400, 1);
        ImGui.WorkspaceActiveSplitterId = "###PortraitWorkspaceSplitterHiddenImage";
        ImGui.ImageIO.MouseDelta = new(100, 0);
        window.DrawWorkspaceSplittersForTest(origin, hidden);
        check(window.WorkspaceLayoutForTest(area).Navigation.Width == hidden.Navigation.Width + 100,
            "the two-column hidden-photo workspace still permits divider dragging");
        ImGui.ResetItems(1400, 1, "显示铭牌画面###PortraitToggleImage"); window.DrawImageToggleForTest();
        check(window.WorkspaceLayoutForTest(area) == right,
            "showing the photo restores the three-column preferences independently of the hidden layout");
        ImGui.ResetImageInteraction(); ImGui.ResetItems(900, 1);
    }
}

namespace FireGaze.PortraitPreview
{
    internal sealed partial class PortraitPreviewWindow
    {
        internal PortraitWorkspaceGeometry WorkspaceLayoutForTest(Vector2 area) => GetWorkspaceLayout(area);
        internal void DrawWorkspaceSplittersForTest(Vector2 origin, PortraitWorkspaceGeometry layout) => DrawWorkspaceSplitters(origin, layout);
    }
}
