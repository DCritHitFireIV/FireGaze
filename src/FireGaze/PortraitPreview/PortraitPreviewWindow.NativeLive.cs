using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private void DrawNativeLiveSetting()
    {
        var enabled = feature.NativeLiveEnabled;
        if (ImGui.Checkbox("动态肖像画面", ref enabled)) feature.SetNativeLiveEnabled(enabled);
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused())
            ImGui.SetTooltip("显示编辑器当前已经生成的肖像画面。默认关闭，不修改取景或灯光。");
    }

    private void DrawNativeLivePreview()
    {
        if (!feature.NativeLiveEnabled) return;
        using var borrow = feature.TryBorrowNativeLive();
        if (borrow is null || !borrow.TryGetImage(out var image))
        {
            ImGui.TextDisabled("正在等待动态肖像画面…");
            return;
        }
        var available = ImGui.GetContentRegionAvail().X;
        var width = MathF.Min(available, 360 * Scale);
        var height = width * image.PixelHeight / image.PixelWidth;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, height));
        ImGui.GetWindowDrawList().AddImage(image.Handle, origin, origin + new Vector2(width, height),
            image.UV0, image.UV1, image.Tint);
    }
}
