using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private void DrawNativeLiveSetting()
    {
        var enabled = feature.NativeLiveEnabled;
        if (ImGui.Checkbox("实时铭牌画面", ref enabled)) feature.SetNativeLiveEnabled(enabled);
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused())
            ImGui.SetTooltip("在这里实时查看游戏铭牌编辑器的画面，方便一边试换一边对照。");
    }

    private void DrawNativeLivePreview()
    {
        if (!feature.NativeLiveEnabled) return;
        using var borrow = feature.TryBorrowNativeLive();
        if (borrow is null || !borrow.TryGetImage(out var image))
        {
            ImGui.TextDisabled("正在等待铭牌画面…");
            return;
        }
        var available = ImGui.GetContentRegionAvail();
        // The equipment grid and its source actions stay usable when this optional
        // thumbnail is enabled. The showcase owns the larger portrait presentation.
        var maximumHeight = MathF.Max(0, MathF.Min(180 * Scale, available.Y - 300 * Scale));
        if (maximumHeight <= 0) return;
        var width = MathF.Min(MathF.Min(available.X, 360 * Scale), maximumHeight * image.PixelWidth / image.PixelHeight);
        var height = width * image.PixelHeight / image.PixelWidth;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, height));
        ImGui.GetWindowDrawList().AddImage(image.Handle, origin, origin + new Vector2(width, height),
            image.UV0, image.UV1, image.Tint);
    }
}
