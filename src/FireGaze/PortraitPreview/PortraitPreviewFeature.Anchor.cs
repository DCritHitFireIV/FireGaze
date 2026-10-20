using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitEditorBounds buttonOwnerBounds;

    private void SetPreviewAnchorBounds(PortraitEditorBounds bounds)
        => buttonOwnerBounds = bounds;

    private void DrawPreviewAnchor()
    {
        if (!editorAvailable) return;
        var viewport = ImGui.GetMainViewport();
        var label = IsActive ? "关闭预览" : "FireGaze预览";
        var style = ImGui.GetStyle();
        var size = Vector2.Max(style.WindowMinSize,
            new Vector2(ImGui.CalcTextSize(label).X + style.FramePadding.X * 2, ImGui.GetFrameHeight()) + style.WindowPadding * 2);
        var bounds = buttonOwnerBounds;
        var placement = NearbyWindowLayout.Place(viewport.WorkPos, viewport.WorkSize,
            viewport.Pos + new Vector2(bounds.X, bounds.Y + 30), new(bounds.Width, bounds.Height), size, ImGui.GetFontSize() / 17f);
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowPos(placement.Position, ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(Vector2.Zero, placement.Size);
        ImGui.SetNextWindowBgAlpha(1f);
        if (ImGui.Begin("###FireGazePortraitAnchor", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            if (ImGui.Button(label))
            {
                if (IsActive) ClosePreview(); else OpenPreview();
            }
        }
        ImGui.End();
    }
}
