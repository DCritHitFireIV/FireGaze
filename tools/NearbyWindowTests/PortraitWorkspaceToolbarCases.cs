using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.PortraitPreview;

internal static class PortraitWorkspaceToolbarCases
{
    internal static void Run(Action<bool, string> check)
    {
        var headerLabels = new[] { "装备###PortraitTab装备", "美容师###PortraitTab美容师", "造型展示###PortraitTab造型展示",
            "投影模板###PortraitPlateSwitcher", "收藏###PortraitFavoriteCurrent", "###PortraitFavoriteMenu" };
        var footerLabels = new[] { "###PortraitUndo", "###PortraitRedo", "历史记录", "铭牌同步预览", "结束预览###PortraitEndPreview" };
        foreach (var (width, scale) in new[] { (668f, 1f), (900f, 1f), (1120f, 1f), (1400f, 1.5f), (2240f, 2f) })
        {
            var window = new PortraitPreviewWindow();
            ImGui.ResetItems(width, scale);
            window.DrawHeaderForTest();
            var header = ImGui.Items.ToArray();
            var tag = $"{width}px/{scale}x";
            check(headerLabels.All(label => header.Count(item => item.Label == label) == 1),
                tag + ": top navigation exposes all three tasks templates and favorites once");
            check(footerLabels.All(label => header.All(item => item.Label != label)),
                tag + ": editing actions belong to the fixed footer");
            check(header.All(item => item.Minimum.X >= 100 && item.Maximum.X <= 100 + width + .02f),
                tag + ": top actions fit available width");
            check(header.Where(item => headerLabels.Contains(item.Label)).All(item => MathF.Abs(item.Maximum.Y - item.Minimum.Y - 36 * scale) < .02f),
                tag + ": every top action shares the task navigation height");
            var template = header.Single(item => item.Label == "投影模板###PortraitPlateSwitcher");
            var tab = header.Single(item => item.Label == "装备###PortraitTab装备");
            check(template.Maximum.X - template.Minimum.X <= (tab.Maximum.X - tab.Minimum.X) * 1.5f,
                tag + ": the four-character template action avoids a disproportionate fixed width");
            ImGui.ResetItems(width, scale);
            var hasFooter = window.DrawFooterForTest();
            check(hasFooter, tag + ": workspace draws its fixed footer");
            if (!hasFooter) continue;
            var footer = ImGui.Items.ToArray();
            check(footerLabels.All(label => footer.Count(item => item.Label == label) == 1),
                tag + ": every editing and restoration action remains visible once");
            check(footer.All(item => item.Minimum.X >= 100 && item.Maximum.X <= 100 + width + .02f),
                tag + ": fixed editing actions stay inside available width");
            var rows = footer.Where(item => footerLabels.Contains(item.Label)).Select(item => item.Minimum.Y).Distinct().Count();
            check(rows == (width / scale < 980 ? 2 : 1), tag + ": narrow footer uses two visible rows");
            check(new[] { "空间参照", "画面分析", "灯光拟合", "独立人物空间" }.All(label => header.Concat(footer).All(item => item.Label != label)),
                tag + ": deferred tools remain hidden");
        }
        foreach (var click in headerLabels.Concat(footerLabels).Concat(["我的收藏"]))
        {
            var window = new PortraitPreviewWindow();
            ImGui.ResetItems(1120, 1, click);
            window.DrawHeaderForTest(); window.DrawFooterForTest();
            var model = window.ModelForTest; var state = window.ToolStateForTest;
            var expected = click switch
            {
                "###PortraitUndo" => model.UndoCalls == 1,
                "###PortraitRedo" => model.RedoCalls == 1,
                "历史记录" => state.History,
                "收藏###PortraitFavoriteCurrent" => model.FavoriteCalls == 1,
                "###PortraitFavoriteMenu" => ImGui.OpenedPopups.SequenceEqual(["###PortraitFavoriteActions"]),
                "投影模板###PortraitPlateSwitcher" => state.Templates,
                "铭牌同步预览" => model.SyncCalls == 1 && model.SyncPlate,
                "结束预览###PortraitEndPreview" => model.CloseCalls == 1,
                "装备###PortraitTab装备" => !state.Beauty,
                "美容师###PortraitTab美容师" => state.Beauty,
                "造型展示###PortraitTab造型展示" => window.DesignTaskForTest,
                "我的收藏" => state.Favorites,
                _ => false,
            };
            check(expected && ImGui.ConsumedClicks == 1, $"actual {click} control invokes its intended route once");
            check(model.UndoCalls + model.RedoCalls + model.FavoriteCalls + model.SyncCalls + model.CloseCalls
                == (click is "###PortraitUndo" or "###PortraitRedo" or "收藏###PortraitFavoriteCurrent" or "铭牌同步预览" or "结束预览###PortraitEndPreview" ? 1 : 0),
                $"actual {click} control does not invoke unrelated mutations");
        }
        foreach (var isError in new[] { false, true })
        foreach (var liveImage in new[] { false, true })
        {
            var window = new PortraitPreviewWindow();
            window.ModelForTest.Status = "临时外观尚未还原，请等待加载后重试。";
            window.ModelForTest.StatusIsError = isError;
            window.ModelForTest.NativeLiveEnabled = liveImage;
            ImGui.ResetItems(320, 1, contentHeight: 160);
            window.DrawPortraitForTest();
            check(ImGui.Items.Any(item => item.Label == window.ModelForTest.Status),
                "portrait surface keeps the latest player status visible with or without its native image");
            check(ImGui.PushedColors.Contains((ImGuiCol.Text, isError ? FireGaze.UI.FireGazePalette.BadText : FireGaze.UI.FireGazePalette.Muted)),
                "persistent status distinguishes a failure from an informational state");
            check(window.ModelForTest.NativeLiveCalls == 0 && window.ModelForTest.CloseCalls == 0,
                "rendering persistent status does not repeat a player operation");
        }
        ImGui.ResetItems(1120, 1);
        new PortraitPreviewWindow().DrawFooterForTest();
        check(ImGui.VectorPaths.Count == 4,
            "undo and redo draw their curve and arrowhead without font glyphs");
        if (ImGui.VectorPaths.Count == 4)
        {
            var undo = ImGui.VectorPaths[1]; var redo = ImGui.VectorPaths[3];
            check(undo[1].X < undo[0].X && undo[1].X < undo[2].X
                && redo[1].X > redo[0].X && redo[1].X > redo[2].X,
                "undo arrowhead points left while redo arrowhead points right");
        }
        ImGui.CurrentViewport = ImGui.Known[3];
        var sourceWindow = new PortraitPreviewWindow();
        sourceWindow.DrawSourceForTest();
        check(ImGui.Maximum == new Vector2(776, 576),
            "source popup constraints follow the current 800x600 secondary viewport");
        ImGui.CurrentViewport = ImGui.Main;
        ImGui.ResetItems(900, 1);
    }
}

namespace Dalamud.Bindings.ImGui
{
    internal enum ImGuiDir { Down }
    internal enum ImGuiCol { Button, ButtonHovered, ButtonActive, Text }
    internal enum ImDrawFlags { None }
    internal static partial class ImGui
    {
        internal sealed record ItemRect(string Label, string ID, Vector2 Minimum, Vector2 Maximum);
        private sealed class Group(Vector2 start)
        { internal Vector2 Start = start, Minimum = new(float.PositiveInfinity), Maximum = new(float.NegativeInfinity); }
        internal static readonly List<ItemRect> Items = [];
        internal static readonly List<string> OpenedPopups = [];
        internal static readonly List<(ImGuiCol Role, Vector4 Value)> PushedColors = [];
        private static readonly Stack<string> IDs = [];
        private static readonly Stack<Group> Groups = [];
        private static Vector2 cursor, contentSize = new(900, 1200), itemMin, itemMax;
        private static float rowBottom;
        private static string? click;
        internal static float FontScale = 1;
        internal static int DisabledDepth, ConsumedClicks;
        internal static void ResetItems(float width, float scale, string? nextClick = null, float contentHeight = 2400)
        {
            Position = new(100, 100); cursor = Position; contentSize = new(width, contentHeight);
            FontScale = scale; rowBottom = 100; itemMin = itemMax = cursor;
            VectorPaths.Clear(); VectorPath.Clear(); Items.Clear(); OpenedPopups.Clear(); PushedColors.Clear(); IDs.Clear(); Groups.Clear();
            click = nextClick; DisabledDepth = ConsumedClicks = 0;
            BeginImageFrame();
        }
        public static Vector2 GetCursorScreenPos() => cursor;
        public static Vector2 GetContentRegionAvail() => contentSize - (cursor - Position);
        public static Vector2 GetItemRectMin() => itemMin;
        public static Vector2 GetItemRectMax() => itemMax;
        public static void PushID(string id) => IDs.Push(id);
        public static void PopID() => IDs.Pop();
        public static void BeginGroup() => Groups.Push(new(cursor));
        public static void EndGroup()
        {
            var group = Groups.Pop(); itemMin = group.Minimum; itemMax = group.Maximum;
            rowBottom = itemMax.Y; cursor = new(Groups.TryPeek(out var parent) ? parent.Start.X : Position.X, rowBottom + GetStyle().ItemSpacing.Y);
        }
        public static void SameLine(float offset = 0, float spacing = -1)
            => cursor = new(offset == 0 ? itemMax.X + (spacing < 0 ? GetStyle().ItemSpacing.X : spacing) : Position.X + offset, itemMin.Y);
        public static void Separator() => Submit("###separator", new(contentSize.X, 1));
        public static bool Button(string label, Vector2 size)
        {
            Submit(label, new(size.X > 0 ? size.X : CalcTextSize(label).X + GetStyle().FramePadding.X * 2,
                size.Y > 0 ? size.Y : GetFrameHeight()));
            return Click(label);
        }
        public static bool Checkbox(string label, ref bool value)
        {
            Submit(label, new(CalcTextSize(label).X + GetFrameHeight() + GetStyle().ItemInnerSpacing.X, GetFrameHeight()));
            if (!Click(label)) return false;
            value = !value; return true;
        }
        public static bool ArrowButton(string label, ImGuiDir direction) => Button(label, new(GetFrameHeight()));
        public static void TextDisabled(string label) => Submit(label, CalcTextSize(label));
        public static void TextUnformatted(string label) => Submit(label, CalcTextSize(label));
        public static void TextWrapped(string label) => Submit(label, new(MathF.Min(GetContentRegionAvail().X, CalcTextSize(label).X), GetFontSize()));
        public static bool SmallButton(string label) => Button(label, Vector2.Zero);
        public static bool IsItemHovered() => imageLastHovered;
        public static bool IsItemFocused() => false;
        public static void SetTooltip(string label) { }
        public static void PushStyleColor(ImGuiCol color, Vector4 value) => PushedColors.Add((color, value));
        public static void PopStyleColor(int count = 1) { }
        public static void OpenPopup(string label) => OpenedPopups.Add(label);
        public static bool BeginPopup(string label) => OpenedPopups.Contains(label) || click == "我的收藏";
        public static bool MenuItem(string label) => Click(label);
        public static void EndPopup() { }
        public static void Dummy(Vector2 size) => Submit("###image-space", size);
        public static uint GetColorU32(Vector4 color) => 0;
        public static DrawList GetWindowDrawList() => new();
        internal sealed partial class DrawList
        {
            public void AddRect(Vector2 a, Vector2 b, uint color, float rounding, ImDrawFlags flags, float thickness) => ImageGripBorders++;
            public void AddLine(Vector2 a, Vector2 b, uint color, float thickness) { }
            public void AddImage(nint handle, Vector2 a, Vector2 b, Vector2 uv0, Vector2 uv1, uint tint)
                => Items.Add(new("###draw-image", "###draw-image", a, b));
        }
        private static bool Click(string label)
        {
            if (DisabledDepth != 0 || click != label) return false;
            click = null; ConsumedClicks++; return true;
        }
        private static void Submit(string label, Vector2 size)
        {
            imageLastHovered = imageLastActive = false;
            itemMin = cursor; itemMax = cursor + size;
            var id = string.Join('/', IDs.Reverse().Append(label));
            Items.Add(new(label, id, itemMin, itemMax));
            foreach (var group in Groups) { group.Minimum = Vector2.Min(group.Minimum, itemMin); group.Maximum = Vector2.Max(group.Maximum, itemMax); }
            rowBottom = MathF.Max(rowBottom, itemMax.Y);
            cursor = new(Groups.TryPeek(out var parent) ? parent.Start.X : Position.X, rowBottom + GetStyle().ItemSpacing.Y);
        }
    }
}

namespace FireGaze.UI
{
    internal static class UiHelpers
    {
        private sealed class DisabledScope(bool disabled) : IDisposable
        { public void Dispose() { if (disabled) ImGui.DisabledDepth--; } }
        public static IDisposable Disabled(bool disabled)
        { if (disabled) ImGui.DisabledDepth++; return new DisabledScope(disabled); }
        public static void PushPrimaryButton() { }
        public static void PopPrimaryButton() { }
    }
}
