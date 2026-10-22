using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.PortraitPreview;
using FireGaze.UI;

internal static class PortraitEquipmentUiCases
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var scale in new[] { 1f, 1.5f, 2f })
        {
            var window = new PortraitPreviewWindow();
            ImGui.ResetItems(160 * scale, scale);
            window.DrawSlotsForTest();
            var icons = ImGui.Items.Where(item => item.Label == "###Slot").ToArray();
            check(icons.Length == 13, "equipment navigation preserves every equipment slot");
            check(icons.All(item => MathF.Abs(item.Maximum.X - item.Minimum.X - 76 * scale) < .02f),
                "equipment icons fill both navigation columns like beauty categories at " + scale + "x");
            check(icons.All(item => item.Minimum.X >= 100 && item.Maximum.X <= 100 + 160 * scale + .02f),
                "equipment navigation remains inside its available width");
        }

        var palette = new PortraitPreviewWindow();
        ImGui.CurrentViewport = ImGui.Known[3];
        ImGui.ResetItems(570, 1);
        ImGui.Size = new(570, 620);
        FireGazeWindowPlacement.Remember("FireGazePortraitPreview");
        palette.OpenPaletteForTest();
        ImGui.RegularWindowTitles.Clear();
        palette.DrawPaletteForTest();
        check(ImGui.RegularWindowTitles.Any(title => title.Contains("###PortraitDyePalette")),
            "dye selection uses a normal resizable window instead of the popup auto-size loop");
        check(ImGui.SizeConditionForTest == ImGuiCond.Appearing,
            "palette default size is applied only on appearance so manual resizing survives later frames");
        check(ImGui.NextSize.X >= 500 && ImGui.NextSize.X < 570 && ImGui.Maximum == new Vector2(776, 576),
            "default palette budgets twelve 32px colors and follows its current secondary viewport");
        var colors = ImGui.Items.Where(item => item.Label.StartsWith("###PortraitDye")).ToArray();
        check(colors.Length == 24 && colors.Take(12).Select(item => item.Minimum.Y).Distinct().Count() == 1
            && colors[12].Minimum.Y > colors[11].Minimum.Y,
            "default dye window renders twelve colors per row with a visible scrolling color area");

        ImGui.ResetItems(340, 1);
        ImGui.Size = new(340, 440);
        ImGui.Frame++;
        palette.DrawPaletteForTest();
        colors = ImGui.Items.Where(item => item.Label.StartsWith("###PortraitDye")).ToArray();
        check(ImGui.Size == new Vector2(340, 440) && colors.Length == 24 && colors.Count(item => item.Minimum.Y == colors[0].Minimum.Y) < 12,
            "narrowing the palette adapts its color columns without shrinking the player's window");
        ImGui.ResetItems(740, 1);
        ImGui.Size = new(740, 540);
        ImGui.Frame++;
        palette.DrawPaletteForTest();
        colors = ImGui.Items.Where(item => item.Label.StartsWith("###PortraitDye")).ToArray();
        check(ImGui.Size == new Vector2(740, 540) && colors.Count(item => item.Minimum.Y == colors[0].Minimum.Y) > 12,
            "widening the palette can display additional colors while retaining its manual size");
        palette.ModelForTest.IsActive = false;
        ImGui.RegularWindowTitles.Clear();
        palette.DrawPaletteForTest();
        check(ImGui.RegularWindowTitles.Count == 0,
            "ending a preview closes its dye window instead of leaving a previous session visible");
        palette.ModelForTest.IsActive = true;
        palette.DrawPaletteForTest();
        check(ImGui.RegularWindowTitles.Count == 0,
            "a new preview does not reopen the previous session's dye window");
        ImGui.CurrentViewport = ImGui.Main;
    }
}

namespace FireGaze.PortraitPreview
{
    internal enum PortraitSlot { MainHand, OffHand, Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, RightRing, LeftRing, Facewear }
    internal readonly record struct PortraitPiece(uint ItemID, byte Stain0, byte Stain1);
    internal sealed record PortraitLook(PortraitPiece[] Pieces);
    internal sealed record PortraitBrowserItem(byte DyeChannels);
    internal sealed partial class PlacementModel
    {
        public bool IsActive { get; set; } = true;
        public PortraitLook Current { get; } = new(Enumerable.Repeat(new PortraitPiece(1, 0, 0), 13).ToArray());
        public IReadOnlyList<PortraitDye> Dyes { get; } = Enumerable.Range(1, 24).Select(i => new PortraitDye((byte)i, "color " + i, 0x123456, 1, (byte)i)).ToArray();
        public PortraitBrowserItem GetItem(PortraitSlot slot, uint id) => new(2);
        public void Dye(PortraitSlot slot, int channel, byte stain) { }
    }
    internal sealed partial class PortraitPreviewWindow
    {
        private static readonly PortraitSlot[] Slots = Enum.GetValues<PortraitSlot>();
        private PortraitSlot selectedSlot = PortraitSlot.Head, dyeSlot = PortraitSlot.Head;
        private int dyeChannel = 0;
        private static string SlotName(PortraitSlot slot) => slot.ToString();
        private static Vector4 DyeColor(uint rgb) => Vector4.One;
        private static bool DrawItemIcon(string id, PortraitBrowserItem item, float size, bool selected, PortraitSlot? slot)
            => ImGui.Button(id, new(size));
        internal void DrawSlotsForTest() => DrawSlots(feature.Current);
        internal void DrawPaletteForTest() => DrawDyePalette();
        internal void OpenPaletteForTest()
        {
            GetType().GetField("showDyePalette", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(this, true);
            ImGui.OpenPopup("###PortraitDyePalette");
        }
    }
}

namespace Dalamud.Bindings.ImGui
{
    internal enum ImGuiColorEditFlags { NoTooltip }
    internal enum ImGuiHoveredFlags { AllowWhenDisabled }
    internal static partial class ImGui
    {
        internal static readonly List<Vector2[]> VectorPaths = [];
        private static readonly List<Vector2> VectorPath = [];
        internal static readonly List<string> RegularWindowTitles = [];
        internal static ImGuiCond SizeConditionForTest;
        public static void PushID(int id) => PushID(id.ToString());
        public static float GetCursorPosX() => cursor.X - Position.X;
        public static void SetCursorPosX(float value) => cursor.X = Position.X + value;
        public static void Spacing() => cursor.Y += GetStyle().ItemSpacing.Y;
        public static bool IsItemHovered(ImGuiHoveredFlags flags) => false;
        public static void CloseCurrentPopup() { }
        public static bool ColorButton(string label, Vector4 color, ImGuiColorEditFlags flags, Vector2 size) => Button(label, size);
        public static bool BeginChild(string id, Vector2 size, bool border) => true;
        public static void EndChild() { }
        internal sealed partial class DrawList
        {
            public void AddTriangleFilled(Vector2 a, Vector2 b, Vector2 c, uint color) { }
            public void PathLineTo(Vector2 point) => VectorPath.Add(point);
            public void PathStroke(uint color, ImDrawFlags flags, float thickness)
            { VectorPaths.Add(VectorPath.ToArray()); VectorPath.Clear(); }
        }
    }
}
