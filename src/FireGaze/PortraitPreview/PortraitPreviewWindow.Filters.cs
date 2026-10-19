using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private static string SourceLabel(OwnedSourceKind kind) => PortraitEquipmentSourceSummary.Label(kind);

    private void DrawSourceGuide()
    {
        if (!showSourceGuide) return;
        ImGui.TextWrapped("打开一次投影台、收藏柜、鞍囊或雇员背包后，就能在这里浏览其中的装备。");
        var retainers = PortraitEquipmentFilterSources.Build(feature.Sources, feature.SourceCharacter, feature.RetainerRoster)
            .Single(choice => choice.Kind == OwnedSourceKind.RetainerInventory);
        ImGui.TextWrapped("雇员背包：" + retainers.StatusText);
        if (ImGui.SmallButton("知道了###PortraitSourceGuide")) showSourceGuide = false;
        ImGui.Separator();
    }

    private void DrawEquipmentFilters(bool facewear = false)
    {
        if (facewear)
        {
            ImGui.TextWrapped("眼镜来自已解锁收藏，存储位置和染色筛选不适用。");
            ImGui.SetNextItemWidth(-1);
            DrawSortCombo();
        }
        else
        {
            var width = Math.Max(80 * Scale, (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2) / 3);
            ImGui.SetNextItemWidth(width);
            if (ImGui.BeginCombo("###PortraitSourceFilter", selectedSources.Count == 0 ? "存储位置" : $"存储位置 · {selectedSources.Count}"))
            {
                var choices = PortraitEquipmentFilterSources.Build(feature.Sources, feature.SourceCharacter, feature.RetainerRoster).ToDictionary(choice => choice.Kind);
                foreach (var kind in OwnedSourcePolicy.SupportedKinds)
                {
                    var choice = choices[kind];
                    var selected = selectedSources.Contains(kind);
                    if (!choice.CanSelect)
                    {
                        ImGui.PushStyleColor(ImGuiCol.Text, FireGazePalette.Muted);
                        var unreadLabel = kind == OwnedSourceKind.RetainerInventory ? choice.StatusText : "未读取";
                        if (ImGui.Selectable($"{SourceLabel(kind)} · {unreadLabel}", false, ImGuiSelectableFlags.DontClosePopups)) showSourceGuide = true;
                        ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip("先打开这个位置读取物品，再回来筛选。");
                        continue;
                    }
                    var state = kind == OwnedSourceKind.RetainerInventory ? choice.StatusText
                        : $"{choice.OwnedItemCount}" + (choice.HasUnread ? " · 部分未读" : "") + (choice.HasCached ? " · 已保存记录" : "");
                    if (ImGui.Checkbox($"{SourceLabel(kind)} · {state}", ref selected))
                    {
                        if (selected) selectedSources.Add(kind); else selectedSources.Remove(kind);
                        filtersChanged = true;
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(width);
            if (ImGui.BeginCombo("###PortraitDyeFilter", selectedDyes.Count == 0 ? "染色" : $"染色 · {selectedDyes.Count}"))
            {
                foreach (var channel in new byte[] { 0, 1, 2 })
                {
                    var selected = selectedDyes.Contains(channel);
                    if (ImGui.Checkbox(channel switch { 0 => "不可染色", 1 => "单染色", _ => "双染色" }, ref selected))
                    {
                        if (selected) selectedDyes.Add(channel); else selectedDyes.Remove(channel);
                        filtersChanged = true;
                    }
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(width);
            DrawSortCombo();
        }

        if (selectedSources.Count > 0 || selectedDyes.Count > 0 || search.Length > 0)
        {
            if (ImGui.SmallButton("清除筛选"))
            { selectedSources.Clear(); selectedDyes.Clear(); search = ""; filtersChanged = true; }
        }
    }

    private void DrawSortCombo()
    {
        if (!ImGui.BeginCombo("###PortraitEquipmentSort", SortLabel(equipmentSort))) return;
        foreach (var sort in Enum.GetValues<PortraitEquipmentSort>())
            if (ImGui.Selectable(SortLabel(sort), sort == equipmentSort)) { equipmentSort = sort; filtersChanged = true; }
        ImGui.EndCombo();
    }

    private static string SortLabel(PortraitEquipmentSort sort) => sort switch
    {
        PortraitEquipmentSort.ItemLevelAscending => "物品等级 ↑",
        PortraitEquipmentSort.ItemLevelDescending => "物品等级 ↓",
        PortraitEquipmentSort.RequiredLevelAscending => "装备所需等级 ↑",
        PortraitEquipmentSort.RequiredLevelDescending => "装备所需等级 ↓",
        PortraitEquipmentSort.IDAscending => "编号 ↑",
        PortraitEquipmentSort.IDDescending => "编号 ↓", _ => "名称排序",
    };
}
