using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.PortraitPreview;

internal static class PortraitWorkspaceToolbarCases
{
    internal static void Run(Action<bool, string> check)
    {
        // The preview window owns editing history and plate sync. Deferred spatial,
        // image-analysis, lighting-fit and independent-scene entries stay hidden
        // from this compact header until those features are ready for release.
        var stableLabels = new[] { "↶###PortraitUndo", "↷###PortraitRedo", "历史记录", "收藏###PortraitFavoriteCurrent", "###PortraitFavoriteMenu",
            "投影模板###PortraitPlateSwitcher", "铭牌同步预览", "装备###PortraitTab装备", "美容师###PortraitTab美容师" };
        var deferredLabels = new[] { "空间参照", "画面分析", "灯光拟合", "独立人物空间" };
        foreach (var (width, scale) in new[] { (900f, 1f), (260f, 1f), (450f, 1.5f), (700f, 2f), (900f, 2f) })
        {
            var window = new PortraitPreviewWindow();
            ImGui.ResetItems(width, scale);
            window.DrawHeaderForTest();
            var items = ImGui.Items.ToArray();
            ImGui.ItemRect Item(string label) => items.Single(item => item.Label == label);
            var history = Item(stableLabels[2]); var undo = Item(stableLabels[0]); var redo = Item(stableLabels[1]);
            var plate = Item(stableLabels[5]); var sync = Item(stableLabels[6]);
            var equipment = Item(stableLabels[7]); var beauty = Item(stableLabels[8]);
            var tag = $"{width}px/{scale:0.#}x";
            check(history.Minimum.Y == undo.Minimum.Y && undo.Minimum.Y == redo.Minimum.Y
                && history.Maximum.X <= undo.Minimum.X,
                $"{tag}: history and glyph actions share one compact header row with actions on the right");
            var favorite = Item(stableLabels[3]); var favoriteMenu = Item(stableLabels[4]);
            check(favorite.Minimum.Y == history.Minimum.Y && favoriteMenu.Minimum.Y == history.Minimum.Y
                && favoriteMenu.Maximum.X < undo.Minimum.X,
                $"{tag}: history and favorites share the left group before the right-aligned glyph actions");
            check(plate.Minimum.Y == sync.Minimum.Y && plate.Maximum.X < sync.Minimum.X,
                $"{tag}: actual template and sync draw remain one atomic group");
            check(equipment.Minimum.Y == beauty.Minimum.Y,
                $"{tag}: equipment and beautician subtabs remain one row");
            check(equipment.Minimum.Y > Math.Max(plate.Maximum.Y, sync.Maximum.Y),
                $"{tag}: appearance subtabs remain below the editing and library controls");
            check(deferredLabels.All(label => items.All(item => item.Label != label)),
                $"{tag}: deferred spatial, analysis, lighting and 3D entries stay hidden");
            check(items.Where(item => stableLabels.Contains(item.Label)).All(item => item.Minimum.X >= 100
                && item.Maximum.X <= 100 + width + .01f),
                $"{tag}: actual controls fit the available content width");
            check(stableLabels.All(label => items.Count(item => item.Label == label) == 1)
                && Item("↶###PortraitUndo").ID == "PortraitEditingToolbar/↶###PortraitUndo"
                && sync.ID == "PortraitEditingToolbar/铭牌同步预览"
                && plate.ID == "投影模板###PortraitPlateSwitcher",
                $"{tag}: labels and stable widget IDs are preserved exactly once");

            window.ModelForTest.NativeLiveEnabled = true;
            ImGui.ResetItems(width, scale);
            window.DrawHeaderForTest();
            var withImage = ImGui.Items.ToArray();
            check(stableLabels.All(label =>
            {
                var before = Item(label); var after = withImage.Single(item => item.Label == label);
                return before.Minimum == after.Minimum && before.Maximum == after.Maximum;
            }), $"{tag}: enabling optional imagery never displaces stable toolbar controls or subtabs");
            var toggle = withImage.Single(item => item.Label == "实时铭牌画面");
            var image = withImage.Single(item => item.Label == "###draw-image");
            check(toggle.Minimum.Y > equipment.Maximum.Y && image.Minimum.Y > toggle.Maximum.Y,
                $"{tag}: native-image toggle is adjacent to its image below the stable tool tiers");
        }

        foreach (var scale in new[] { 1f, 2f })
        {
            var window = new PortraitPreviewWindow();
            window.ModelForTest.NativeLiveEnabled = true;
            ImGui.ResetItems(900 * scale, scale, contentHeight: 650 * scale);
            window.DrawHeaderForTest();
            var image = ImGui.Items.Single(item => item.Label == "###draw-image");
            check(image.Maximum.Y - image.Minimum.Y <= 180 * scale + .01f
                && image.Maximum.Y <= 100 + 650 * scale - 300 * scale + .01f,
                $"{scale}x: optional native picture leaves room for equipment and source actions");
        }

        foreach (var click in stableLabels.Concat(["实时铭牌画面", "我的收藏"]))
        {
            var window = new PortraitPreviewWindow();
            ImGui.ResetItems(900, 1, click);
            window.DrawHeaderForTest();
            var model = window.ModelForTest; var state = window.ToolStateForTest;
            var expected = click switch
            {
                "↶###PortraitUndo" => model.UndoCalls == 1,
                "↷###PortraitRedo" => model.RedoCalls == 1,
                "历史记录" => state.History,
                "收藏###PortraitFavoriteCurrent" => model.FavoriteCalls == 1,
                "###PortraitFavoriteMenu" => ImGui.OpenedPopups.SequenceEqual(["###PortraitFavoriteActions"]),
                "投影模板###PortraitPlateSwitcher" => state.Templates,
                "铭牌同步预览" => model.SyncCalls == 1 && model.SyncPlate,
                "装备###PortraitTab装备" => !state.Beauty,
                "美容师###PortraitTab美容师" => state.Beauty,
                "实时铭牌画面" => model.NativeLiveCalls == 1 && model.NativeLiveEnabled,
                "我的收藏" => state.Favorites,
                _ => false,
            };
            check(expected && ImGui.ConsumedClicks == 1, $"actual {click} control keeps its original once-only action");
            check(model.UndoCalls + model.RedoCalls + model.FavoriteCalls + model.SyncCalls + model.NativeLiveCalls
                == (click is "↶###PortraitUndo" or "↷###PortraitRedo" or "收藏###PortraitFavoriteCurrent" or "铭牌同步预览" or "实时铭牌画面" ? 1 : 0),
                $"actual {click} control does not invoke unrelated view-model routes");
        }
        ImGui.ResetItems(900, 1);
    }
}

namespace Dalamud.Bindings.ImGui
{
    internal enum ImGuiDir { Down }
    internal enum ImGuiCol { Button, ButtonHovered, ButtonActive }
    internal enum ImDrawFlags { None }
    internal static partial class ImGui
    {
        internal sealed record ItemRect(string Label, string ID, Vector2 Minimum, Vector2 Maximum);
        private sealed class Group(Vector2 start)
        { internal Vector2 Start = start, Minimum = new(float.PositiveInfinity), Maximum = new(float.NegativeInfinity); }
        internal static readonly List<ItemRect> Items = [];
        internal static readonly List<string> OpenedPopups = [];
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
            Items.Clear(); OpenedPopups.Clear(); IDs.Clear(); Groups.Clear();
            click = nextClick; DisabledDepth = ConsumedClicks = 0;
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
        public static bool IsItemHovered() => false;
        public static bool IsItemFocused() => false;
        public static void SetTooltip(string label) { }
        public static void PushStyleColor(ImGuiCol color, Vector4 value) { }
        public static void PopStyleColor(int count = 1) { }
        public static void OpenPopup(string label) => OpenedPopups.Add(label);
        public static bool BeginPopup(string label) => OpenedPopups.Contains(label) || click == "我的收藏";
        public static bool MenuItem(string label) => Click(label);
        public static void EndPopup() { }
        public static void Dummy(Vector2 size) => Submit("###image-space", size);
        public static uint GetColorU32(Vector4 color) => 0;
        public static DrawList GetWindowDrawList() => new();
        internal sealed class DrawList
        {
            public void AddRect(Vector2 a, Vector2 b, uint color, float rounding, ImDrawFlags flags, float thickness) { }
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
