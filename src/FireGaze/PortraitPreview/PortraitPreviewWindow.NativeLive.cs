using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private void DrawWorkspacePortrait()
    {
        ImGui.TextUnformatted("当前铭牌");
        ImGui.TextDisabled("临时外观 · 关闭后还原");
        if (!string.IsNullOrWhiteSpace(feature.Status))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, feature.StatusIsError ? FireGazePalette.BadText : FireGazePalette.Muted);
            try { ImGui.TextWrapped(feature.Status); }
            finally { ImGui.PopStyleColor(); }
        }
        ImGui.Separator();
        using var borrow = feature.TryBorrowNativeLive();
        if (borrow is null || !borrow.TryGetImage(out var image))
        {
            ImGui.TextWrapped("正在等待铭牌画面。请确认游戏铭牌编辑器已加载完成，再重试显示。");
            UiHelpers.PushPrimaryButton();
            try
            {
                if (ImGui.Button("重试显示画面###PortraitRetryImage")) feature.SetNativeLiveEnabled(true);
            }
            finally { UiHelpers.PopPrimaryButton(); }
            return;
        }
        var available = ImGui.GetContentRegionAvail();
        var fitted = PortraitNativeLiveLayout.Fit(available, image.PixelWidth, image.PixelHeight);
        if (fitted.Size.X <= 0 || fitted.Size.Y <= 0) return;
        var origin = ImGui.GetCursorScreenPos() + fitted.Offset;
        ImGui.Dummy(available);
        ImGui.GetWindowDrawList().AddImage(image.Handle, origin, origin + fitted.Size,
            image.UV0, image.UV1, image.Tint);
    }
}
