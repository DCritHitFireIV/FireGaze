using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private bool workspacePortraitVisible = true;
    private float workspacePortraitFraction = 1f;
    private Vector2 workspacePortraitPan;

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
        if (available.X <= 0 || available.Y <= 0) return;
        // Keep wheel input inside this fixed viewport. Its content never resizes the
        // workspace column or forwards scroll to the equipment list or outer window.
        var childVisible = ImGui.BeginChild("###PortraitImageViewport", available, false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        try
        {
            if (!childVisible) return;
            available = ImGui.GetContentRegionAvail();
            var maximum = PortraitNativeLiveLayout.Fit(available, image.PixelWidth, image.PixelHeight);
            var fitted = PortraitNativeLiveLayout.Fit(available, image.PixelWidth, image.PixelHeight,
                workspacePortraitFraction, workspacePortraitPan);
            if (fitted.Size.X <= 0 || fitted.Size.Y <= 0) return;
            var viewport = ImGui.GetCursorScreenPos();
            var visibleMinimum = Vector2.Max(viewport, viewport + fitted.Offset);
            var visibleMaximum = Vector2.Min(viewport + available, viewport + fitted.Offset + fitted.Size);

            // Submit the corner first: ImGui's first hovered ID owns the overlap,
            // so the pan target cannot steal an active resize gesture.
            var grip = DrawPortraitResizeGrip(visibleMinimum, visibleMaximum, maximum.Size);
            ImGui.SetCursorScreenPos(visibleMinimum);
            ImGui.InvisibleButton("###PortraitImageCanvas", visibleMaximum - visibleMinimum,
                ImGuiButtonFlags.MouseButtonLeft);
            var hovered = ImGui.IsItemHovered();
            var active = ImGui.IsItemActive();
            if (hovered || active)
            {
                ImGui.SetTooltip("滚轮缩放 · 拖动画面移动 · 双击恢复适应窗口");
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) ResetPortraitTransform();
                else if (active && !grip.Active && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                    workspacePortraitPan = PortraitNativeLiveLayout.PanBy(available, fitted.Size,
                        workspacePortraitPan, ImGui.GetIO().MouseDelta);
            }
            var wheel = ImGui.GetIO().MouseWheel;
            if ((hovered || grip.Hovered) && wheel != 0)
            {
                (workspacePortraitFraction, workspacePortraitPan) = PortraitNativeLiveLayout.ZoomAt(available,
                    image.PixelWidth, image.PixelHeight, workspacePortraitFraction, workspacePortraitPan,
                    ImGui.GetMousePos() - viewport, wheel);
                ImGui.GetIO().MouseWheel = 0;
            }
            fitted = PortraitNativeLiveLayout.Fit(available, image.PixelWidth, image.PixelHeight,
                workspacePortraitFraction, workspacePortraitPan);
            workspacePortraitPan = PortraitNativeLiveLayout.PanBy(available, fitted.Size, workspacePortraitPan, Vector2.Zero);
            var origin = viewport + fitted.Offset;
            ImGui.PushClipRect(viewport, viewport + available, true);
            try
            {
                var draw = ImGui.GetWindowDrawList();
                draw.AddImage(image.Handle, origin, origin + fitted.Size, image.UV0, image.UV1, image.Tint);
                DrawPortraitResizeMark(Vector2.Max(viewport, origin),
                    Vector2.Min(viewport + available, origin + fitted.Size), grip.Hovered || grip.Active);
            }
            finally { ImGui.PopClipRect(); }
            ImGui.SetCursorScreenPos(viewport);
            ImGui.Dummy(available);
        }
        finally { ImGui.EndChild(); }
    }

    private (bool Hovered, bool Active) DrawPortraitResizeGrip(Vector2 minimum, Vector2 corner, Vector2 maximum)
    {
        var edge = MathF.Min(18f * Scale, MathF.Min(corner.X - minimum.X, corner.Y - minimum.Y));
        if (edge <= 0) return default;
        ImGui.SetCursorScreenPos(corner - new Vector2(edge));
        ImGui.InvisibleButton("###PortraitImageResize", new Vector2(edge), ImGuiButtonFlags.MouseButtonLeft);
        var active = ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        if (hovered || active)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNwse);
            ImGui.SetTooltip("拖动缩放画面 · 双击恢复适应窗口");
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) ResetPortraitTransform();
            else if (active && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                workspacePortraitFraction = PortraitNativeLiveLayout.ResizeFraction(workspacePortraitFraction,
                    ImGui.GetIO().MouseDelta, maximum);
        }
        return (hovered, active);
    }

    private void ResetPortraitTransform()
    {
        workspacePortraitFraction = 1f;
        workspacePortraitPan = Vector2.Zero;
    }

    private static void DrawPortraitResizeMark(Vector2 minimum, Vector2 corner, bool highlighted)
    {
        var edge = MathF.Min(18f * Scale, MathF.Min(corner.X - minimum.X, corner.Y - minimum.Y));
        if (edge <= 0) return;
        var color = ImGui.GetColorU32(highlighted ? FireGazePalette.Action : FireGazePalette.Border);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(corner - new Vector2(edge), corner, ImGui.GetColorU32(FireGazePalette.Raised), 3f * Scale);
        draw.AddRect(corner - new Vector2(edge), corner, color, 3f * Scale, ImDrawFlags.None, 1f * Scale);
        for (var line = 1; line <= 3; line++)
        {
            var inset = edge * line / 4;
            draw.AddLine(corner - new Vector2(inset, 1), corner - new Vector2(1, inset), color, 1.5f * Scale);
        }
    }
}
