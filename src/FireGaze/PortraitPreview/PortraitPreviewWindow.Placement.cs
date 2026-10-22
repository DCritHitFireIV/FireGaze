using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private string lightingFitOwner = "FireGazePortraitPreview";

    public override void PreDraw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        NearbyWindowBounds? anchor = ownerBounds is { } owner
            ? new NearbyWindowBounds(ImGui.GetMainViewport().Pos + new Vector2(owner.X, owner.Y), new(owner.Width, owner.Height)) : null;
        var placement = FireGazeWindowPlacement.Prepare("FireGazePortraitPreview", new Vector2(1120, 760) * scale, scale, "FireGaze", anchor);
        var bounds = PortraitWindowLayout.ForViewport(FireGazeWindowPlacement.PreparedWorkSize, scale);
        Size = placement.Size / scale;
        SizeCondition = ImGuiCond.Appearing;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = bounds.Minimum, MaximumSize = bounds.Maximum };
    }

    private void PrepareHistoryWindow()
    {
        FireGazeWindowPlacement.Prepare("FireGazePortraitHistory", new Vector2(460, 570) * Scale, Scale, "FireGazePortraitPreview");
        FireGazeWindowPlacement.Constrain(Vector2.Zero, new Vector2(960, 1040) * Scale, Scale);
    }

    private void PrepareLightWindow()
    {
        FireGazeWindowPlacement.Prepare("FireGazePortraitLight", new Vector2(440, 520) * Scale, Scale, "FireGazePortraitLightingSpace");
        var bounds = PortraitLightWindowLayout.ForViewport(FireGazeWindowPlacement.PreparedWorkSize, Scale);
        ImGui.SetNextWindowSizeConstraints(bounds.Minimum, bounds.Maximum);
    }

    public void OpenLightingFit(string ownerID = "FireGazePortraitPreview")
    {
        lightingFitOwner = ownerID;
        feature.SetLiveCaptureScope(PortraitImageScope.Portrait);
        showLightingFit = true;
    }

    private void PrepareLightingFitWindow()
    {
        FireGazeWindowPlacement.Prepare("FireGazePortraitLightingFit", new Vector2(500, 740) * Scale, Scale, lightingFitOwner);
        FireGazeWindowPlacement.Constrain(new Vector2(360, 380) * Scale, FireGazeWindowPlacement.PreparedWorkSize, Scale);
    }
}
