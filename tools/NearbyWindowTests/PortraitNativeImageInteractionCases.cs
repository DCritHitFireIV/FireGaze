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
        ImGui.ResetImageInteraction(); ImGui.ResetItems(320, 1, contentHeight: 600);
        ImGui.ImageIO.MousePos = new(260, 400); ImGui.ImageIO.MouseWheel = 5;
        window.DrawPortraitForTest();
        check(window.PortraitImageFractionForTest > 1.9f && window.PortraitImageFractionForTest < 2.1f,
            "hovered portrait wheel enlarges beyond the automatic fitted size");
        check(ImGui.ImageIO.MouseWheel == 0 && ImGui.ImageChildFlags.HasFlag(ImGuiWindowFlags.NoScrollWithMouse)
            && ImGui.ImageChildFlags.HasFlag(ImGuiWindowFlags.NoScrollbar),
            "portrait wheel is consumed inside an independent non-scrolling image view");
        check(ImGui.ImageClips.Count == 1 && ImGui.ImageClips[0].Minimum == new Vector2(100, 147)
            && ImGui.ImageClips[0].Maximum == new Vector2(420, 700),
            "enlarged texture is clipped to the image cell and cannot paint over nearby controls");
        var enlarged = ImGui.Items.Single(item => item.Label == "###draw-image");
        var reachable = ImGui.Items.Single(item => item.Label == "###PortraitImageResize");
        check(reachable.Maximum.X <= 420 && reachable.Maximum.Y <= 700
            && reachable.Minimum.X >= 100 && reachable.Minimum.Y >= 147
            && ImGui.ImageGripBackgrounds == 1 && ImGui.ImageGripBorders == 1,
            "enlarged portrait keeps its outlined light resize grip within the visible cell");
        var zoom = window.PortraitImageFractionForTest;
        ImGui.ResetImageInteraction(); ImGui.ResetItems(320, 1, contentHeight: 600);
        ImGui.ImageIO.MousePos = new(260, 400); ImGui.ImageCanvasActive = ImGui.ImageDragging = true;
        ImGui.ImageIO.MouseDelta = new(45, -35);
        window.DrawPortraitForTest();
        var panned = ImGui.Items.Single(item => item.Label == "###draw-image");
        check(Vector2.Distance(panned.Minimum - enlarged.Minimum, new(45, -35)) < .01f
            && window.PortraitImageFractionForTest == zoom,
            "dragging the enlarged image pans its contents without resizing the cell or photo");
        reachable = ImGui.Items.Single(item => item.Label == "###PortraitImageResize");
        check(reachable.Maximum == new Vector2(420, 700),
            "the enlarged picture's active handle moves to the clipped viewport corner");
        ImGui.ResetImageInteraction(); ImGui.ResetItems(320, 1, contentHeight: 600);
        ImGui.ImageIO.MousePos = reachable.Maximum - new Vector2(3);
        ImGui.ImageGripActive = ImGui.ImageCanvasActive = ImGui.ImageDragging = true;
        ImGui.ImageIO.MouseDelta = new(-10, -15);
        var beforePan = window.PortraitImagePanForTest;
        window.DrawPortraitForTest();
        check(window.PortraitImageFractionForTest < zoom && Vector2.Distance(window.PortraitImagePanForTest, beforePan) < .00001f
            && ImGui.ImageHoverOwner == "###PortraitImageResize",
            "corner grip wins the overlapping image hit target and resizes instead of panning");
        ImGui.ResetImageInteraction(); ImGui.ResetItems(320, 1, contentHeight: 600);
        ImGui.ImageIO.MousePos = new(260, 400); ImGui.ImageDoubleClick = true;
        window.DrawPortraitForTest();
        check(window.PortraitImageFractionForTest == 1 && window.PortraitImagePanForTest == Vector2.Zero,
            "double-clicking the image restores automatic fit and centered position");
        foreach (var mode in new[] { "outside", "hidden", "unready" })
        {
            var isolated = new PortraitPreviewWindow();
            isolated.ModelForTest.NativeLiveEnabled = mode != "unready";
            if (mode == "hidden")
            {
                ImGui.ResetItems(1120, 1, "隐藏铭牌画面###PortraitToggleImage");
                isolated.DrawImageToggleForTest();
            }
            ImGui.ResetImageInteraction(); ImGui.ResetItems(320, 1, contentHeight: 600);
            ImGui.ImageIO.MousePos = mode == "outside" ? new(900, 400) : new(260, 400);
            ImGui.ImageIO.MouseWheel = 1;
            isolated.DrawPortraitForTest();
            check(ImGui.ImageIO.MouseWheel == 1 && isolated.PortraitImageFractionForTest == 1,
                mode + ": non-interactive picture does not consume another control's wheel");
        }
        check(window.ModelForTest.NativeLiveCalls == 0 && window.ModelForTest.CloseCalls == 0
            && window.ModelForTest.UndoCalls == 0 && window.ModelForTest.RedoCalls == 0,
            "wheel zoom and canvas pan remain local display operations");
        ImGui.ResetImageInteraction(); ImGui.ResetItems(900, 1);
    }
}

namespace FireGaze.PortraitPreview
{
    internal sealed partial class PortraitPreviewWindow
    {
        internal bool PortraitImageVisibleForTest => workspacePortraitVisible;
        internal float PortraitImageFractionForTest => workspacePortraitFraction;
        internal Vector2 PortraitImagePanForTest => workspacePortraitPan;
        internal void DrawImageToggleForTest() => DrawPortraitVisibilityToggle();
    }
}

namespace Dalamud.Bindings.ImGui
{
    internal enum ImGuiButtonFlags { MouseButtonLeft }
    internal enum ImGuiMouseButton { Left }
    internal enum ImGuiMouseCursor { ResizeNwse, ResizeEw }
    internal static partial class ImGui
    {
        internal sealed class NativeImageIO { public Vector2 MouseDelta, MousePos = new(float.NaN); public float MouseWheel, MouseWheelH; }
        internal static readonly NativeImageIO ImageIO = new();
        internal static bool ImageGripActive, ImageCanvasActive, ImageDoubleClick, ImageDragging;
        internal static string? WorkspaceActiveSplitterId;
        internal static ImGuiMouseCursor? LastMouseCursor;
        private static bool imageLastHovered, imageLastActive;
        internal static string? ImageHoverOwner;
        internal static ImGuiWindowFlags ImageChildFlags;
        internal static readonly List<(Vector2 Minimum, Vector2 Maximum)> ImageClips = [];
        internal static int ImageGripBackgrounds, ImageGripBorders;
        internal static bool NavigationOverflow, NavigationChild;
        internal static readonly List<bool> NavigationScrollbarFrames = [];
        private static readonly Stack<(Vector2 Position, Vector2 Cursor, Vector2 Content, float Bottom, Vector2 Min, Vector2 Max, Vector2 ChildSize)> ImageChildren = [];
        internal static void BeginImageFrame()
        {
            imageLastHovered = imageLastActive = false; ImageHoverOwner = null;
            ImageClips.Clear(); ImageChildFlags = 0; ImageGripBackgrounds = ImageGripBorders = 0;
            ImageChildren.Clear();
        }
        internal static void ResetImageInteraction()
        {
            ImageGripActive = ImageCanvasActive = ImageDoubleClick = ImageDragging = false;
            WorkspaceActiveSplitterId = null; LastMouseCursor = null;
            ImageIO.MouseDelta = Vector2.Zero; ImageIO.MousePos = new(float.NaN); ImageIO.MouseWheel = ImageIO.MouseWheelH = 0;
            BeginImageFrame();
        }
        public static void SetCursorScreenPos(Vector2 value) => cursor = value;
        public static bool InvisibleButton(string label, Vector2 size, ImGuiButtonFlags flags)
        {
            Submit(label, size);
            if (label is "###PortraitImageResize" or "###PortraitImageCanvas")
            {
                var inside = ImageIO.MousePos.X >= itemMin.X && ImageIO.MousePos.X < itemMax.X
                    && ImageIO.MousePos.Y >= itemMin.Y && ImageIO.MousePos.Y < itemMax.Y;
                // ImGui keeps the first submitted hovered ID. The picture must not steal the corner's active ID.
                imageLastHovered = inside && ImageHoverOwner is null;
                if (imageLastHovered) ImageHoverOwner = label;
                imageLastActive = label == "###PortraitImageResize" ? ImageGripActive
                    : ImageCanvasActive && !ImageGripActive && ImageHoverOwner != "###PortraitImageResize";
            }
            else if (label.StartsWith("###PortraitWorkspaceSplitter", StringComparison.Ordinal))
            {
                imageLastHovered = ImageIO.MousePos.X >= itemMin.X && ImageIO.MousePos.X < itemMax.X
                    && ImageIO.MousePos.Y >= itemMin.Y && ImageIO.MousePos.Y < itemMax.Y;
                imageLastActive = WorkspaceActiveSplitterId == label;
            }
            return Click(label);
        }
        public static bool IsItemActive() => imageLastActive;
        public static bool IsMouseDown(ImGuiMouseButton button) => (ImageGripActive || ImageCanvasActive) && !ImageDoubleClick;
        public static bool IsMouseDragging(ImGuiMouseButton button) => ImageDragging;
        public static bool IsMouseDoubleClicked(ImGuiMouseButton button) => ImageDoubleClick;
        public static Vector2 GetMousePos() => ImageIO.MousePos;
        public static NativeImageIO GetIO() => ImageIO;
        public static void SetMouseCursor(ImGuiMouseCursor cursor) => LastMouseCursor = cursor;
        public static bool BeginChild(string id, Vector2 size, bool border, ImGuiWindowFlags flags)
        {
            ImageChildren.Push((Position, cursor, contentSize, rowBottom, itemMin, itemMax, size));
            ImageChildFlags = flags; Position = cursor; contentSize = size; rowBottom = cursor.Y;
            NavigationChild = id == "###PortraitWorkspaceNavigation";
            if (NavigationChild)
            {
                var scrollbar = flags.HasFlag(ImGuiWindowFlags.AlwaysVerticalScrollbar) || NavigationOverflow;
                NavigationScrollbarFrames.Add(scrollbar);
                if (scrollbar) contentSize.X -= GetStyle().ScrollbarSize;
            }
            return true;
        }
        private static void EndImageChild()
        {
            if (!ImageChildren.TryPop(out var parent)) return;
            if (NavigationChild)
            {
                NavigationOverflow = rowBottom - Position.Y > parent.ChildSize.Y;
                NavigationChild = false;
            }
            Position = parent.Position; cursor = parent.Cursor; contentSize = parent.Content;
            rowBottom = parent.Bottom; itemMin = parent.Min; itemMax = parent.Max;
            Submit("###portrait-child-space", parent.ChildSize);
        }
        public static void PushClipRect(Vector2 min, Vector2 max, bool intersect) => ImageClips.Add((min, max));
        public static void PopClipRect() { }
        internal sealed partial class DrawList
        {
            public void AddRectFilled(Vector2 a, Vector2 b, uint color, float rounding = 0)
                => ImageGripBackgrounds++;
        }
    }
}
