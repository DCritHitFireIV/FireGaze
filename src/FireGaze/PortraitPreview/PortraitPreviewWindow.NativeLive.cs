using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private bool workspacePortraitVisible = true;
    private float workspacePortraitFraction = 1f;

    private void DrawPortraitVisibilityToggle()
    {
        if (HeaderActionButton(workspacePortraitVisible ? "隐藏铭牌画面###PortraitToggleImage" : "显示铭牌画面###PortraitToggleImage"))
            workspacePortraitVisible = !workspacePortraitVisible;
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused())
            ImGui.SetTooltip(workspacePortraitVisible ? "隐藏画面，为试换选项腾出空间" : "显示当前铭牌画面");
    }

    private void DrawWorkspacePortrait()
    {
        if (!workspacePortraitVisible) return;
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
            // Framework owns readiness and retries. Draw never prompts the player to repair
            // loading state, mutates the editor, or substitutes a previous session's image.
            ImGui.Dummy(ImGui.GetContentRegionAvail());
            return;
        }
        var available = ImGui.GetContentRegionAvail();
        var maximum = PortraitNativeLiveLayout.Fit(available, image.PixelWidth, image.PixelHeight);
        var fitted = PortraitNativeLiveLayout.Fit(available, image.PixelWidth, image.PixelHeight, workspacePortraitFraction);
        if (fitted.Size.X <= 0 || fitted.Size.Y <= 0) return;
        var origin = ImGui.GetCursorScreenPos() + fitted.Offset;
        ImGui.Dummy(available);
        ImGui.GetWindowDrawList().AddImage(image.Handle, origin, origin + fitted.Size,
            image.UV0, image.UV1, image.Tint);
        DrawPortraitResizeGrip(origin, fitted.Size, maximum.Size);
    }

    private void DrawPortraitResizeGrip(Vector2 origin, Vector2 size, Vector2 maximum)
    {
        var edge = MathF.Min(18f * Scale, MathF.Min(size.X, size.Y));
        if (edge <= 0) return;
        var afterImage = ImGui.GetCursorScreenPos();
        var corner = origin + size;
        ImGui.SetCursorScreenPos(corner - new Vector2(edge));
        ImGui.InvisibleButton("###PortraitImageResize", new Vector2(edge), ImGuiButtonFlags.MouseButtonLeft);
        var active = ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        if (hovered || active)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNwse);
            ImGui.SetTooltip("拖动缩放画面 · 双击恢复适应窗口");
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) workspacePortraitFraction = 1f;
            else if (active && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                workspacePortraitFraction = PortraitNativeLiveLayout.ResizeFraction(workspacePortraitFraction,
                    ImGui.GetIO().MouseDelta, maximum, 80f * Scale);
        }
        var color = ImGui.GetColorU32(active || hovered ? FireGazePalette.Action : FireGazePalette.Border);
        var draw = ImGui.GetWindowDrawList();
        for (var line = 1; line <= 3; line++)
        {
            var inset = edge * line / 4;
            draw.AddLine(corner - new Vector2(inset, 1), corner - new Vector2(1, inset), color, 1.5f * Scale);
        }
        ImGui.SetCursorScreenPos(afterImage);
    }
}
