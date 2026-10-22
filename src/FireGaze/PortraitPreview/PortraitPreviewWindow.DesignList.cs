using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private bool showDesignShowcase;
    private bool showDesignList;
    private readonly HashSet<string> hiddenDesignRows = new(StringComparer.Ordinal);

    public void OpenDesignShowcase()
    {
        showDesignShowcase = true;
        IsOpen = true;
        BringToFront();
    }

    public PortraitDesignManifest DesignManifestForDiagnostics() => BuildDesignManifest();

    /// <summary>Compact entry placed in the workspace toolbar; the full surface is a nearby dashboard.</summary>
    private void DrawDesignShowcaseEntry()
    {
        if (ImGui.SmallButton("造型展示###PortraitDesignShowcase")) showDesignShowcase = true;
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused())
            ImGui.SetTooltip("把当前肖像的装备、染色和外貌整理成可分享的展示");
    }

    private void DrawDesignShowcaseWindow()
    {
        if (!showDesignShowcase || !feature.IsActive) return;
        FireGazeWindowPlacement.Prepare("FireGazePortraitDesignShowcase", new Vector2(780 * Scale, 700 * Scale), Scale,
            "FireGazePortraitPreview");
        FireGazeWindowPlacement.Constrain(new Vector2(320, 420) * Scale,
            new Vector2(float.MaxValue), Scale);
        if (!FireGazeWindowPlacement.Begin("造型展示###FireGazePortraitDesignShowcase", ref showDesignShowcase,
                "FireGazePortraitDesignShowcase")) { ImGui.End(); return; }

        var manifest = BuildDesignManifest();
        ImGui.PushStyleColor(ImGuiCol.Text, FireGazePalette.Action);
        ImGui.TextUnformatted("FIREGAZE  /  当前造型");
        ImGui.PopStyleColor();
        ImGui.Spacing();
        if (FireGazeSymbols.NeutralButton("造型全览###PortraitDesignAll", selected: !showDesignList)) showDesignList = false;
        ImGui.SameLine();
        if (FireGazeSymbols.NeutralButton("设计清单###PortraitDesignListToggle", selected: showDesignList)) showDesignList = true;
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip("只保留预览与实际穿着的差异，附带来源位置");
        if (hiddenDesignRows.Count > 0)
        {
            ContinueToolbar(ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X,
                ImGui.CalcTextSize($"恢复隐藏 ({hiddenDesignRows.Count})").X + ImGui.GetStyle().FramePadding.X * 2);
            if (ImGui.SmallButton($"恢复隐藏 ({hiddenDesignRows.Count})###PortraitDesignRestoreHidden")) hiddenDesignRows.Clear();
        }
        ImGui.Separator();

        var available = ImGui.GetContentRegionAvail();
        var split = available.X >= 620 * Scale;
        var portraitWidth = split ? Math.Min(252 * Scale, available.X * .34f) : available.X;
        var portraitHeight = split ? available.Y : Math.Max(150 * Scale, available.Y * .42f);
        ImGui.BeginChild("###PortraitDesignImage", new Vector2(portraitWidth, portraitHeight), true);
        var nativeLive = feature.NativeLiveEnabled;
        if (!nativeLive)
        {
            var area = ImGui.GetContentRegionAvail();
            var buttonSize = new Vector2(Math.Min(182 * Scale, area.X), ImGui.GetFrameHeight() * 1.2f);
            ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(Math.Max(0, (area.X - buttonSize.X) * .5f),
                Math.Max(0, (area.Y - buttonSize.Y) * .5f)));
            UiHelpers.PushPrimaryButton();
            try
            {
                if (ImGui.Button("显示铭牌画面###PortraitDesignShowImage", buttonSize)) feature.SetNativeLiveEnabled(true);
            }
            finally { UiHelpers.PopPrimaryButton(); }
            if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip("在造型旁显示游戏铭牌编辑器的实时画面");
        }
        else
        {
            if (ImGui.SmallButton("隐藏画面###PortraitDesignHideImage")) feature.SetNativeLiveEnabled(false);
            using var portrait = feature.TryBorrowNativeLive();
            if (portrait is not null && portrait.TryGetImage(out var live))
            {
                var imageArea = ImGui.GetContentRegionAvail();
                var aspect = live.PixelWidth / (float)Math.Max(1, live.PixelHeight);
                var width = Math.Min(Math.Max(1, imageArea.X - 16 * Scale), Math.Max(1, imageArea.Y - 16 * Scale) * aspect);
                var height = width / Math.Max(.01f, aspect);
                var offset = new Vector2(Math.Max(0, (imageArea.X - width) * .5f), Math.Max(0, (imageArea.Y - height) * .5f));
                var origin = ImGui.GetCursorScreenPos() + offset;
                ImGui.Dummy(imageArea);
                ImGui.GetWindowDrawList().AddImage(live.Handle, origin, origin + new Vector2(width, height), live.UV0, live.UV1, live.Tint);
            }
            else ImGui.TextDisabled("正在等待铭牌画面…");
        }
        ImGui.EndChild();
        if (split) ImGui.SameLine();

        ImGui.BeginChild("###PortraitDesignRows", new Vector2(0, 0), false);
        var rows = (showDesignList ? manifest.Differences() : manifest.ForShowcase()).Items;
        if (showDesignList)
        {
            var source = manifest.Sources.FirstOrDefault(value => value.Source.Kind == OwnedSourceKind.Equipped);
            if (source?.Status != OwnedReadStatus.Current)
                ImGui.TextDisabled(source?.Status == OwnedReadStatus.PossiblyStale ? "身上装备 · 记录过期" : "身上装备 · 尚未读取");
        }
        if (rows.Count == 0) ImGui.TextDisabled(showDesignList ? "暂无已确认的造型差异。" : "尚未读取到可展示的造型数据。");
        var previousKind = "";
        var visibleCount = 0;
        foreach (var row in rows)
        {
            if (hiddenDesignRows.Contains(row.Key)) continue;
            visibleCount++;
            if (!string.Equals(previousKind, row.KindLabel, StringComparison.Ordinal))
            {
                if (previousKind.Length > 0) ImGui.Spacing();
                ImGui.PushStyleColor(ImGuiCol.Text, FireGazePalette.Action);
                ImGui.TextUnformatted(row.KindLabel);
                ImGui.PopStyleColor();
                previousKind = row.KindLabel;
            }
            DrawDesignRow(row);
        }
        if (rows.Count > 0 && visibleCount == 0) ImGui.TextDisabled("已隐藏全部展示项，可在上方恢复。");
        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawDesignRow(PortraitDesignEntry row)
    {
        ImGui.PushID(row.Key);
        var available = ImGui.GetContentRegionAvail().X;
        var layout = PortraitDesignShowcaseLayout.Row(available, Scale);
        var iconSize = layout.IconSize;
        var rowX = ImGui.GetCursorPosX();
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(iconSize, iconSize));
        if (row.Icon > 0 && textures.GetFromGameIcon(new GameIconLookup(row.Icon)).TryGetWrap(out var icon, out _))
            ImGui.GetWindowDrawList().AddImage(icon.Handle, origin, origin + new Vector2(iconSize));
        else
            ImGui.GetWindowDrawList().AddRectFilled(origin, origin + new Vector2(iconSize),
                row.Color ?? ImGui.GetColorU32(FireGazePalette.Selected), 4 * Scale);
        ImGui.SameLine(rowX + layout.TextX);
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(rowX + layout.TextX + layout.TextWidth);
        ImGui.TextWrapped(row.Name);
        ImGui.PushStyleColor(ImGuiCol.Text, FireGazePalette.Muted);
        ImGui.TextWrapped(row.Source);
        ImGui.PopStyleColor();
        if (row.Kind != PortraitDesignEntryKind.Appearance && (row.Stain0 != 0 || row.Stain1 != 0))
        {
            ImGui.TextWrapped($"染色：{DesignDyeName(row.Stain0)} / {DesignDyeName(row.Stain1)}");
        }
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        ImGui.SameLine(rowX + layout.HideX);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Vector4.Zero);
        try
        {
            if (ImGui.Button("×###HideDesignRow", new Vector2(layout.HideSize, Math.Max(layout.HideSize, ImGui.GetFrameHeight()))))
                hiddenDesignRows.Add(row.Key);
            if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip("隐藏此项");
            if (ImGui.IsItemFocused()) FireGazeSymbols.DrawFocus(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        }
        finally { ImGui.PopStyleColor(3); }
        ImGui.Separator();
        ImGui.PopID();
    }

    private string DesignDyeName(byte id) => id == 0 ? "无" : feature.Dyes.FirstOrDefault(dye => dye.ID == id)?.Name ?? $"#{id}";

    private PortraitDesignManifest BuildDesignManifest()
    {
        var preview = BuildLookEntries(feature.Current, "肖像装备", PortraitDesignEntryKind.Preview, "位置未读取");
        var equipped = BuildEquippedEntries();
        var appearance = PortraitDesignManifest.AppearanceEntries(feature.AppearanceGroups, "preview", "预览美容师", "美容师");
        var actualAppearance = PortraitDesignManifest.AppearanceEntries(feature.ActualAppearanceGroups, "actual", "实际外貌", "当前角色外貌");
        return PortraitDesignManifest.Build(preview, equipped, appearance.Concat(actualAppearance), feature.Sources);
    }

    private IEnumerable<PortraitDesignEntry> BuildEquippedEntries()
    {
        var currentRead = feature.Sources.Any(source => source.Source.Kind == OwnedSourceKind.Equipped && source.Status == OwnedReadStatus.Current);
        foreach (var piece in PortraitDesignEntrySlotMap.ReadEquipped(feature.EquippedItems, currentRead))
        {
            var item = feature.GetItem(piece.Slot, piece.ItemID);
            yield return new PortraitDesignEntry($"equipped:{piece.Slot}", item?.Name ?? (piece.ItemID == 0 ? SlotName(piece.Slot) + " · 未穿戴" : $"物品 #{piece.ItemID}"),
                "实际穿着", (int)piece.ItemID, "身上装备 · " + SlotName(piece.Slot), item?.Icon ?? 0, PortraitDesignEntryKind.Equipped,
                piece.Stain0, piece.Stain1);
        }
        if (feature.ActualFacewear is { } glasses)
        {
            var item = feature.GetItem(PortraitSlot.Facewear, glasses);
            yield return new("equipped:Facewear", item?.Name ?? (glasses == 0 ? "眼镜 · 未穿戴" : $"眼镜 #{glasses}"),
                "实际穿着", glasses, "当前角色 · 眼镜", item?.Icon ?? 0, PortraitDesignEntryKind.Equipped);
        }
    }

    private IEnumerable<PortraitDesignEntry> BuildLookEntries(PortraitLook? look, string kindLabel,
        PortraitDesignEntryKind kind, string fallbackSource)
    {
        if (look is null) yield break;
        foreach (var slot in Slots)
        {
            if (!PortraitDesignEntrySlotMap.IsRendered(look, slot)) continue;
            var piece = look.Pieces[(int)slot];
            var item = feature.GetItem(slot, piece.ItemID);
            var locations = feature.StorageLocations(piece.ItemID);
            var source = piece.ItemID == 0 ? "当前肖像" : locations.Count > 0 ? string.Join("、", locations) : fallbackSource;
            yield return new PortraitDesignEntry($"{kind.ToString().ToLowerInvariant()}:{slot}",
                item?.Name ?? (piece.ItemID == 0 ? SlotName(slot) + " · 未穿戴" : $"物品 #{piece.ItemID}"), kindLabel, (int)piece.ItemID, source, item?.Icon ?? 0, kind,
                piece.Stain0, piece.Stain1);
        }
    }

}
