using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    /// <summary>Draw one owned image lease; no native editor, capture task or readback is started here.</summary>
    private void DrawLightingSpaceImage(Vector2 available)
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(available);
        using var live = feature.AcquireCurrentLiveImage();
        var info = live is null ? (PortraitSpaceLiveImageInfo?)null
            : new(live.Sample.Scope, live.Width, live.Height);
        if (live is null || !live.Sample.IsValid
            || !PortraitLightingSpaceImage.TryFit(info, PortraitImageScope.Portrait, available, out var fit))
        {
            ImGui.TextDisabled("等待肖像成图，请暂停动作并稍候。");
            return;
        }
        var minimum = origin + fit.Offset;
        ImGui.GetWindowDrawList().AddImage(live.Texture.Handle, minimum, minimum + fit.Size);
    }
}
