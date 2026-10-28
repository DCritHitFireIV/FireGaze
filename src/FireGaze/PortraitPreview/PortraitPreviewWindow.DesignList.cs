using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private bool showDesignShowcase;
    private bool showDesignList;
    private bool hideDesignSources;
    private float designDisplayScale = 1;
    private readonly HashSet<string> hiddenDesignRows = new(StringComparer.Ordinal);

    public void OpenDesignShowcase()
    {
        showDesignShowcase = true;
        IsOpen = true;
        BringToFront();
    }

    public PortraitDesignManifest DesignManifestForDiagnostics() => BuildDesignManifest();

    private void DrawDesignNavigation()
    {
        if (FireGazeSymbols.NeutralButton("造型全览###PortraitDesignAll", selected: !showDesignList)) showDesignList = false;
        if (FireGazeSymbols.NeutralButton("只看差异###PortraitDesignListToggle", selected: showDesignList)) showDesignList = true;
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip("对比肖像预览与实际穿着，保留已确认的外观差异");
        ImGui.Spacing();
        ImGui.TextWrapped("装备、染色和外貌的来源会随展示项列出。");
        ImGui.Checkbox("隐藏位置说明###PortraitDesignHideSources", ref hideDesignSources);
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip("隐藏展示项下方的位置与来源说明，并收起这一行。");
        ImGui.Spacing();
        ImGui.TextUnformatted($"展示大小 · {designDisplayScale * 100:0}%");
        ImGui.BeginDisabled(designDisplayScale <= .75f);
        if (ImGui.Button("缩小###PortraitDesignScaleDown"))
            designDisplayScale = PortraitDesignShowcaseLayout.NormalizeDisplayScale(designDisplayScale - .1f);
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(designDisplayScale >= 2);
        if (ImGui.Button("放大###PortraitDesignScaleUp"))
            designDisplayScale = PortraitDesignShowcaseLayout.NormalizeDisplayScale(designDisplayScale + .1f);
        ImGui.EndDisabled();
        if (ImGui.Button("恢复默认大小###PortraitDesignScaleReset")) designDisplayScale = 1;
        if (hiddenDesignRows.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextWrapped($"已隐藏 {hiddenDesignRows.Count} 项");
            if (ImGui.Button("恢复隐藏项###PortraitDesignRestoreHidden")) hiddenDesignRows.Clear();
        }
    }

    private void DrawDesignShowcaseContents()
    {
        var baseScale = Scale;
        // Only this workspace child is scaled. Determine its inherited scale first,
        // then restore it even if a source read or texture lookup throws.
        var inheritedFontSize = ImGui.GetFontSize();
        ImGui.SetWindowFontScale(1);
        var naturalFontSize = ImGui.GetFontSize();
        var inheritedFontScale = naturalFontSize > 0 ? inheritedFontSize / naturalFontSize : 1;
        ImGui.SetWindowFontScale(inheritedFontScale * PortraitDesignShowcaseLayout.NormalizeDisplayScale(designDisplayScale));
        try { DrawScaledDesignShowcaseContents(baseScale); }
        finally { ImGui.SetWindowFontScale(inheritedFontScale); }
    }

    private void DrawScaledDesignShowcaseContents(float baseScale)
    {
        var manifest = BuildDesignManifest();
        ImGui.TextUnformatted(showDesignList ? "造型差异" : "当前造型");
        ImGui.TextDisabled("预览与实际穿着 · 不改动角色装备");
        ImGui.Separator();
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
            DrawDesignRow(row, baseScale);
        }
        if (rows.Count > 0 && visibleCount == 0) ImGui.TextDisabled("已隐藏全部展示项，可在上方恢复。");
    }

    private void DrawDesignRow(PortraitDesignEntry row, float baseScale)
    {
        ImGui.PushID(row.Key);
        var available = ImGui.GetContentRegionAvail().X;
        var layout = PortraitDesignShowcaseLayout.Row(available, baseScale, designDisplayScale);
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
        var source = PortraitDesignShowcaseText.SourceFor(row, hideDesignSources);
        if (source.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, FireGazePalette.Muted);
            ImGui.TextWrapped(source);
            ImGui.PopStyleColor();
        }
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
