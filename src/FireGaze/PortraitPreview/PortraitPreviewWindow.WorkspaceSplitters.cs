using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private float? navigationSplitLogicalWidth;
    private float? imageSplitLogicalWidth;
    private float? hiddenNavigationSplitLogicalWidth;

    private PortraitWorkspaceGeometry GetWorkspaceLayout(Vector2 area)
        => PortraitWorkspaceLayout.For(area.X, area.Y, Scale, workspacePortraitVisible,
            workspacePortraitVisible ? navigationSplitLogicalWidth : hiddenNavigationSplitLogicalWidth, imageSplitLogicalWidth);

    private void DrawWorkspaceSplitters(Vector2 origin, PortraitWorkspaceGeometry layout)
    {
        if (layout.Stacked) return;
        if (!workspacePortraitVisible)
        {
            var delta = DrawWorkspaceSplitter("###PortraitWorkspaceSplitterHiddenImage", origin,
                layout.Navigation, layout.BodyHeight, layout.Gap);
            if (delta != 0)
                hiddenNavigationSplitLogicalWidth = PortraitWorkspaceLayout.MoveDivider(layout, Scale, 0, delta).Navigation;
            return;
        }
        var navigationDelta = DrawWorkspaceSplitter("###PortraitWorkspaceSplitterNavigation", origin,
            layout.Navigation, layout.BodyHeight, layout.Gap);
        var imageDelta = DrawWorkspaceSplitter("###PortraitWorkspaceSplitterImage", origin,
            layout.Image, layout.BodyHeight, layout.Gap);
        if (navigationDelta == 0 && imageDelta == 0) return;
        var widths = PortraitWorkspaceLayout.MoveDivider(layout, Scale, navigationDelta != 0 ? 0 : 1,
            navigationDelta != 0 ? navigationDelta : imageDelta);
        navigationSplitLogicalWidth = widths.Navigation;
        imageSplitLogicalWidth = widths.Image;
    }

    private static float DrawWorkspaceSplitter(string id, Vector2 origin, PortraitWorkspaceRegion left,
        float bodyHeight, float gap)
    {
        var thickness = MathF.Min(gap, 8f * Scale);
        if (thickness <= 0 || bodyHeight <= 0) return 0;
        var center = origin + new Vector2(left.X + left.Width + gap * .5f, 0);
        ImGui.SetCursorScreenPos(center - new Vector2(thickness * .5f, 0));
        ImGui.InvisibleButton(id, new Vector2(thickness, bodyHeight), ImGuiButtonFlags.MouseButtonLeft);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (hovered || active)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            ImGui.SetTooltip("拖动调整列宽");
        }
        var color = hovered || active ? FireGazePalette.Action : FireGazePalette.Border;
        if (!hovered && !active) color.W *= .5f;
        var halfLine = MathF.Min(.65f * Scale, thickness * .15f);
        ImGui.GetWindowDrawList().AddRectFilled(center - new Vector2(halfLine, 0),
            center + new Vector2(halfLine, bodyHeight), ImGui.GetColorU32(color));
        var delta = ImGui.GetIO().MouseDelta.X;
        return active && float.IsFinite(delta) ? delta : 0;
    }
}
