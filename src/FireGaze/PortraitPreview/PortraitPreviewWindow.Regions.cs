using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private static void DrawWorkspaceNavigationRegion(Vector2 origin, PortraitWorkspaceRegion region, Action draw)
        // The responsive two-column grid becomes shorter when a scrollbar consumes
        // width. Reserve that gutter before measuring, so near-fit content cannot
        // alternate between overflowing and fitting on successive frames.
        => DrawWorkspaceRegion("###PortraitWorkspaceNavigation", origin, region, draw,
            ImGuiWindowFlags.AlwaysVerticalScrollbar);

    private static void DrawWorkspaceRegion(string id, Vector2 origin, PortraitWorkspaceRegion region, Action draw,
        ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
        ImGui.SetCursorScreenPos(origin + new Vector2(region.X, region.Y));
        var visible = ImGui.BeginChild(id, new Vector2(region.Width, region.Height), false, flags);
        try { if (visible) draw(); }
        finally { ImGui.EndChild(); }
    }
}
