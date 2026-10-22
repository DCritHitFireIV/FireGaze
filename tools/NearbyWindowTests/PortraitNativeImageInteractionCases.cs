using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.PortraitPreview;

internal static class PortraitNativeImageInteractionCases
{
    internal static void Run(Action<bool, string> check)
    {
        var window = new PortraitPreviewWindow();
        ImGui.ResetItems(1120, 1);
        window.DrawImageToggleForTest();
        check(window.PortraitImageVisibleForTest && ImGui.Items.Count(item => item.Label == "隐藏铭牌画面###PortraitToggleImage") == 1,
            "portrait starts visible with a hide control in the persistent header");
        ImGui.ResetItems(1120, 1, "隐藏铭牌画面###PortraitToggleImage");
        window.DrawImageToggleForTest();
        check(!window.PortraitImageVisibleForTest && ImGui.ConsumedClicks == 1,
            "the hide control changes only local visibility once");
        ImGui.ResetItems(320, 1);
        window.ModelForTest.NativeLiveEnabled = true;
        window.DrawPortraitForTest();
        check(ImGui.Items.All(item => item.Label != "###draw-image" && item.Label != "###PortraitImageResize"),
            "hidden portrait submits neither an image nor a resize target");
        ImGui.ResetItems(1120, 1, "显示铭牌画面###PortraitToggleImage");
        window.DrawImageToggleForTest();
        check(window.PortraitImageVisibleForTest && ImGui.ConsumedClicks == 1
            && window.ModelForTest.NativeLiveCalls == 0 && window.ModelForTest.CloseCalls == 0,
            "the persistent show control restores the image without changing the preview or editor");

        ImGui.ResetImageInteraction();
        ImGui.ResetItems(320, 1, contentHeight: 600);
        window.DrawPortraitForTest();
        var first = ImGui.Items.Single(item => item.Label == "###draw-image");
        var handle = ImGui.Items.Single(item => item.Label == "###PortraitImageResize");
        check(handle.Maximum == first.Maximum && handle.Minimum.X >= first.Minimum.X && handle.Minimum.Y >= first.Minimum.Y,
            "resize handle belongs to the image bottom-right corner without crossing its bounds");
        var original = first.Maximum - first.Minimum;
        ImGui.ResetItems(320, 1, contentHeight: 600);
        ImGui.ImageGripActive = true; ImGui.ImageIO.MouseDelta = -original * .25f;
        window.DrawPortraitForTest();
        check(Math.Abs(window.PortraitImageFractionForTest - .5f) < .0001f,
            "dragging the actual image handle changes relative image size");
        ImGui.ResetImageInteraction(); ImGui.ResetItems(320, 1, contentHeight: 600);
        window.DrawPortraitForTest();
        var smaller = ImGui.Items.Single(item => item.Label == "###draw-image");
        check(Vector2.Distance(smaller.Maximum - smaller.Minimum, original * .5f) < .001f,
            "the next Draw uses the player's size rather than resetting to default");
        ImGui.ResetItems(640, 2, contentHeight: 1200);
        window.DrawPortraitForTest();
        var scaled = ImGui.Items.Single(item => item.Label == "###draw-image");
        check(Vector2.Distance(scaled.Maximum - scaled.Minimum, original) < .001f,
            "saved relative image size adapts to a proportionally larger window and font scale");
        ImGui.ResetItems(320, 1, contentHeight: 600);
        ImGui.ImageGripActive = true; ImGui.ImageDoubleClick = true;
        window.DrawPortraitForTest();
        check(window.PortraitImageFractionForTest == 1,
            "double-clicking the resize handle restores automatic fit without a separate settings flow");
        check(window.ModelForTest.NativeLiveCalls == 0 && window.ModelForTest.CloseCalls == 0
            && window.ModelForTest.UndoCalls == 0 && window.ModelForTest.RedoCalls == 0,
            "image visibility and resizing do not mutate character appearance or restart the session");
        ImGui.ResetImageInteraction(); ImGui.ResetItems(900, 1);
    }
}

namespace FireGaze.PortraitPreview
{
    internal sealed partial class PortraitPreviewWindow
    {
        internal bool PortraitImageVisibleForTest => workspacePortraitVisible;
        internal float PortraitImageFractionForTest => workspacePortraitFraction;
        internal void DrawImageToggleForTest() => DrawPortraitVisibilityToggle();
    }
}

namespace Dalamud.Bindings.ImGui
{
    internal enum ImGuiButtonFlags { MouseButtonLeft }
    internal enum ImGuiMouseButton { Left }
    internal enum ImGuiMouseCursor { ResizeNwse }
    internal static partial class ImGui
    {
        internal sealed class NativeImageIO { public Vector2 MouseDelta; }
        internal static readonly NativeImageIO ImageIO = new();
        internal static bool ImageGripActive, ImageDoubleClick;
        private static bool imageGripLast;
        internal static void ResetImageInteraction()
        { ImageGripActive = ImageDoubleClick = imageGripLast = false; ImageIO.MouseDelta = Vector2.Zero; }
        public static void SetCursorScreenPos(Vector2 value) => cursor = value;
        public static bool InvisibleButton(string label, Vector2 size, ImGuiButtonFlags flags)
        {
            imageGripLast = label == "###PortraitImageResize";
            Submit(label, size); return Click(label);
        }
        public static bool IsItemActive() => imageGripLast && ImageGripActive;
        public static bool IsMouseDown(ImGuiMouseButton button) => ImageGripActive && !ImageDoubleClick;
        public static bool IsMouseDoubleClicked(ImGuiMouseButton button) => ImageDoubleClick;
        public static NativeImageIO GetIO() => ImageIO;
        public static void SetMouseCursor(ImGuiMouseCursor cursor) { }
    }
}
