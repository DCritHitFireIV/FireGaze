using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

internal static class NearbyWindowIntegrationCases
{
    internal static void Run(Action<bool,string> check)
    {
        ImGui.CurrentViewport = new(2, new(2000,100), new(1600,900));
        ImGui.Position = new(2600,200); ImGui.Size = new(400,700);
        FireGazeWindowPlacement.Remember("secondary-owner");
        // Drawing is now back inside the base window on the main viewport. The stored owner,
        // not the caller, determines where its child appears.
        ImGui.CurrentViewport = ImGui.Main;
        var result = FireGazeWindowPlacement.Prepare("secondary-child", new(420,570), 1, "secondary-owner");
        check(result.Position == new Vector2(3012,200) && ImGui.NextPosition == result.Position,
            "a child of a remembered secondary owner stays beside that owner");
        check(ImGui.NextViewport == 2 && ImGui.PositionCondition == ImGuiCond.Appearing,
            "owner viewport is selected and an already-open child is not continuously repositioned");
        FireGazeWindowPlacement.Constrain(new(300,200), new(5000,5000), 1);
        check(ImGui.Maximum == new Vector2(1576,876), "size constraint uses the prepared owner's work area");

        ImGui.CurrentViewport = ImGui.Known[3];
        FireGazeWindowPlacement.Remember("already-open-child");
        ImGui.Frame++;
        ImGui.CurrentViewport = ImGui.Main; ImGui.NextViewport = 999;
        FireGazeWindowPlacement.Prepare("already-open-child", new(420,570), 1, "secondary-owner");
        check(ImGui.NextViewport == 999, "an already-open child is not forced back to its parent's viewport each frame");
        FireGazeWindowPlacement.Constrain(new(300,200), new(5000,5000), 1);
        check(ImGui.Maximum == new Vector2(776,576), "an open child keeps constraints from its own viewport after being moved");

        // The native boundary returns false for a collapsed window, but it still has a
        // viewport and can be dragged. Forgetting it makes a later frame treat it as new.
        var collapsedVisible = true;
        var collapsedOpen = true;
        for (var frame = 0; frame < 4; frame++)
        {
            ImGui.Frame++;
            ImGui.CurrentViewport = ImGui.Known[3];
            ImGui.BeginVisible = false;
            collapsedVisible = FireGazeWindowPlacement.Begin("Collapsed###collapsed-child", ref collapsedOpen, "collapsed-child");
        }
        ImGui.Frame++; ImGui.CurrentViewport = ImGui.Main; ImGui.NextViewport = 999;
        FireGazeWindowPlacement.Prepare("collapsed-child", new(420,570), 1, "secondary-owner");
        check(!collapsedVisible && ImGui.NextViewport == 999,
            "a collapsed child remains owned by its moved viewport across multiple frames");
        FireGazeWindowPlacement.Constrain(new(300,200), new(5000,5000), 1);
        check(ImGui.Maximum == new Vector2(776,576), "collapsed child keeps its actual viewport constraints");
        ImGui.BeginVisible = true;

        // A closed platform viewport must not retain an unsafe pointer or off-screen child.
        ImGui.Known.Remove(2);
        result = FireGazeWindowPlacement.Prepare("closed-secondary-child", new(420,570), 1, "secondary-owner");
        check(result.Position.X >= 0 && result.Position.X + result.Size.X <= 1920 && ImGui.NextViewport == 1,
            "retired owner viewport falls back to a reachable main viewport");

        ImGui.CurrentViewport = new(3, new(5000,200), new(800,600));
        ImGui.Position = new(5200,300); ImGui.Size = new(500,450);
        FireGazeWindowPlacement.PrepareModal(new(600,400));
        check(ImGui.NextPosition == new Vector2(5450,525) && ImGui.NextViewport == 3,
            "a modal centers over the actual secondary owner and stays in its work area");
    }
}

namespace Dalamud.Bindings.ImGui
{
    internal enum ImGuiCond { Appearing, Always }
    [Flags]
    internal enum ImGuiWindowFlags { NoDecoration = 1, AlwaysAutoResize = 2, NoSavedSettings = 4, NoFocusOnAppearing = 8, NoScrollbar = 16, NoScrollWithMouse = 32 }
    internal readonly record struct ImGuiViewportPtr(uint ID, Vector2 WorkPos, Vector2 WorkSize)
    { public bool IsNull => ID == 0; public Vector2 Pos => WorkPos; }
    internal static partial class ImGui
    {
        internal static readonly ImGuiViewportPtr Main = new(1, Vector2.Zero, new(1920,1080));
        internal static readonly Dictionary<uint,ImGuiViewportPtr> Known = new() { [1]=Main, [2]=new(2,new(2000,100),new(1600,900)), [3]=new(3,new(5000,200),new(800,600)) };
        internal static ImGuiViewportPtr CurrentViewport = Main;
        internal static Vector2 Position, Size, NextPosition, NextSize, Minimum, Maximum;
        internal static uint NextViewport;
        internal static int Frame = 10;
        internal static bool BeginVisible = true;
        internal static ImGuiCond PositionCondition;
        public static ImGuiViewportPtr GetMainViewport() => Main;
        public static ImGuiViewportPtr GetWindowViewport() => CurrentViewport;
        public static ImGuiViewportPtr FindViewportByID(uint id) => Known.GetValueOrDefault(id);
        public static Vector2 GetWindowPos() => Position;
        public static Vector2 GetWindowSize() => Size;
        public static float GetFontSize() => 17 * FontScale;
        public static int GetFrameCount() => Frame;
        public static bool Begin(string title, ref bool open)
        {
            RegularWindowTitles.Add(title);
            if (title.Contains("###PortraitDyePalette"))
            {
                if (RegularWindowTitles.Count(t => t.Contains("###PortraitDyePalette")) == 1) Size = NextSize;
                ResetItems(Math.Max(1, Size.X - 30), FontScale, contentHeight: Math.Max(1, Size.Y - 30));
            }
            return BeginVisible;
        }
        public static bool Begin(string title, ImGuiWindowFlags flags) => BeginVisible;
        public static bool Button(string label) => Button(label, Vector2.Zero);
        public static Vector2 CalcTextSize(string label) => new(label.Split("##")[0].Length * 8 * FontScale, 17 * FontScale);
        public static float GetFrameHeight() => 25 * FontScale;
        public static Style GetStyle() => new();
        internal sealed class Style
        {
            public Vector2 FramePadding => new Vector2(4) * FontScale;
            public Vector2 ItemSpacing => new Vector2(8, 4) * FontScale;
            public Vector2 ItemInnerSpacing => new Vector2(4) * FontScale;
            public Vector2 WindowPadding => new(8);
            public float ScrollbarSize => 14 * FontScale;
            public Vector2 WindowMinSize => new(32);
        }
        public static void SetNextWindowBgAlpha(float alpha) { }
        public static void End() { }
        public static void SetNextWindowViewport(uint id) => NextViewport=id;
        public static void SetNextWindowPos(Vector2 position, ImGuiCond condition, Vector2 pivot=default) { NextPosition=position; PositionCondition=condition; }
        public static void SetNextWindowSize(Vector2 size, ImGuiCond condition) { NextSize=size; SizeConditionForTest=condition; }
        public static void SetNextWindowSizeConstraints(Vector2 minimum, Vector2 maximum) { Minimum=minimum; Maximum=maximum; }
    }
}
