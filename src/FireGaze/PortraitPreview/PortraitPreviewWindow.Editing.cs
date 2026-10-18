using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private static readonly ConditionalWeakTable<IPortraitPreviewViewModel, PortraitHistoryPresentation> HistoryPresentation = new();

    private void DrawUndoRedo()
    {
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        using (UiHelpers.Disabled(!feature.CanUndo))
        {
            if (ImGui.Button("撤回")) feature.Undo();
        }
        ContinueToolbar(right, ImGui.CalcTextSize("重做").X + ImGui.GetStyle().FramePadding.X * 2);
        using (UiHelpers.Disabled(!feature.CanRedo))
        {
            if (ImGui.Button("重做")) feature.Redo();
        }
    }

    private static void ContinueToolbar(float right, float nextWidth)
    {
        var gap = ImGui.GetStyle().ItemSpacing.X;
        if (PortraitPreviewLayout.FitsInline(right, ImGui.GetItemRectMax().X, nextWidth, gap))
            ImGui.SameLine(0, gap);
    }

    private void DrawEditingToolbar()
    {
        ImGui.PushID("PortraitEditingToolbar");
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        DrawUndoRedo();
        ContinueToolbar(right, ImGui.CalcTextSize("历史记录").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button("历史记录")) showHistory = !showHistory;
        ContinueToolbar(right, ImGui.CalcTextSize("肖像实时同步铭牌").X + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        var sync = feature.SyncPlate;
        if (ImGui.Checkbox("肖像实时同步铭牌", ref sync)) feature.SetSyncPlate(sync);
        if (ImGui.IsItemHovered() || ImGui.IsItemFocused()) ImGui.SetTooltip("仅同步自己看到的铭牌预览。关闭 FireGaze修改 后还原，不会保存到铭牌。");
        if (sync) ImGui.TextWrapped("同步本地铭牌预览 · 关闭 FireGaze修改 后还原");
        ImGui.PopID();
    }

    private void DrawHistoryPopup()
    {
        if (!showHistory) return;
        ImGui.SetNextWindowSize(new Vector2(460, 570) * Scale, ImGuiCond.FirstUseEver);
        if (ImGui.Begin("肖像历史记录###FireGazePortraitHistory", ref showHistory))
            DrawHistoryRows(feature, true);
        ImGui.End();
    }

    internal static void DrawHistoryRows(IPortraitPreviewViewModel feature, bool allowRestore)
    {
        if (!string.IsNullOrWhiteSpace(feature.HistoryStatus)) ImGui.TextWrapped(feature.HistoryStatus);
        if (!allowRestore) ImGui.TextWrapped("请先打开对应的肖像编辑器。");
        ImGui.Separator();
        ImGui.BeginChild("###PortraitHistoryEntries", new Vector2(0, 0), false);
        var records = feature.History;
        if (records.Count == 0) ImGui.TextUnformatted("暂无编辑记录。");
        var days = HistoryPresentation.GetValue(feature, _ => new PortraitHistoryPresentation()).Get(records);
        foreach (var day in days)
        {
            if (!ImGui.CollapsingHeader($"{day.Date:yyyy-MM-dd} · {day.Records.Count} 条", ImGuiTreeNodeFlags.DefaultOpen)) continue;
            var dayRecords = day.Records;
            var clipper = new ImGuiListClipper();
            clipper.Begin(dayRecords.Count, ImGui.GetTextLineHeightWithSpacing());
            while (clipper.Step())
            for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var record = dayRecords[index];
                ImGui.PushID(record.ID.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var label = $"{record.At.ToLocalTime():HH:mm:ss}  {record.Label}{(record.IsCurrent ? "  · 当前" : "")}";
                if (ImGui.Selectable(label, record.IsCurrent) && allowRestore) feature.RestoreHistory(record.ID);
                FireGazeSymbols.DrawSelection(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), record.IsCurrent, ImGui.IsItemFocused());
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) || ImGui.IsItemFocused())
                    ImGui.SetTooltip(allowRestore ? "恢复此刻的装备、美容、姿势、镜头、灯光及装饰" : "请先打开对应肖像编辑器");
                ImGui.PopID();
            }
            clipper.End();
        }
        ImGui.EndChild();
    }

    private void DrawAppearance()
    {
        var groups = feature.AppearanceGroups;
        var selected = groups.FirstOrDefault(group => group.ID == selectedAppearance) ?? groups.FirstOrDefault();
        if (selected is null)
        {
            ImGui.TextWrapped("等待当前角色的美容师选项加载。");
            return;
        }
        selectedAppearance = selected.ID;
        var height = Math.Max(220 * Scale, ImGui.GetContentRegionAvail().Y - ImGui.GetTextLineHeightWithSpacing() * 2);
        var sideWidth = PortraitPreviewLayout.SideWidth(ImGui.GetContentRegionAvail().X, Scale);
        ImGui.BeginChild("###PortraitAppearanceGroups", new Vector2(sideWidth, height), true);
        var categoryEdge = PortraitPreviewLayout.GridEdge((ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * .5f, 80 * Scale);
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var current = group.Options.FirstOrDefault(option => option.Value == group.CurrentValue);
            if (i % 2 != 0) ImGui.SameLine();
            ImGui.PushID(group.ID);
            ImGui.BeginGroup();
            var categoryTop = ImGui.GetCursorPosY();
            var active = selected.ID == group.ID;
            if (DrawAppearanceOption("###AppearanceCategory", current, categoryEdge, active)) selectedAppearance = group.ID;
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + categoryEdge);
            ImGui.TextWrapped(group.Label);
            ImGui.PopTextWrapPos();
            var captionSpace = categoryTop + categoryEdge + ImGui.GetStyle().ItemSpacing.Y
                + ImGui.GetTextLineHeightWithSpacing() * 2 - ImGui.GetCursorPosY();
            if (captionSpace > 0) ImGui.Dummy(new Vector2(categoryEdge, captionSpace));
            ImGui.EndGroup();
            ImGui.PopID();
        }
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("###PortraitAppearanceOptions", new Vector2(0, height), true);
        ImGui.TextUnformatted(selected.Label);
        if (!string.IsNullOrWhiteSpace(selected.Description)) ImGui.TextWrapped(selected.Description);
        ImGui.Separator();
        var currentOption = selected.Options.FirstOrDefault(option => option.Value == selected.CurrentValue);
        ImGui.TextWrapped($"当前：{currentOption?.Label ?? "尚未读取"}");
        inspectedItem = null;
        var onlyColors = selected.Options.All(option => option.Color is not null);
        var spacing = ImGui.GetStyle().ItemSpacing;
        var gridHeight = Math.Max(82 * Scale, ImGui.GetContentRegionAvail().Y - ImGui.GetTextLineHeightWithSpacing() * 3);
        ImGui.BeginChild("###PortraitAppearanceGrid", new Vector2(0, gridHeight), false);
        var edge = PortraitPreviewLayout.GridEdge(ImGui.GetContentRegionAvail().X, (onlyColors ? 34 : 82) * Scale);
        var columns = PortraitPreviewLayout.Columns(ImGui.GetContentRegionAvail().X, edge, spacing.X);
        var clipper = new ImGuiListClipper();
        clipper.Begin((selected.Options.Count + columns - 1) / columns, edge + spacing.Y);
        while (clipper.Step())
        {
            for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= selected.Options.Count) break;
                if (column != 0) ImGui.SameLine();
                var option = selected.Options[index];
                ImGui.PushID(option.Value);
                if (DrawAppearanceOption("###AppearanceOption", option, edge, option.Value == selected.CurrentValue) && option.Available)
                    feature.ChangeAppearance(selected.ID, option.Value);
                ImGui.PopID();
            }
        }
        clipper.End();
        ImGui.EndChild();
        if (selected.Options.Count == 0) ImGui.TextWrapped("暂未找到当前角色可用的选项。");
        if (inspectedItem is { } inspected)
        {
            ImGui.TextWrapped(inspected.Name);
            if (!inspected.Available) ImGui.TextWrapped(string.IsNullOrWhiteSpace(inspected.Reason) ? "当前角色无法预览此选项。" : inspected.Reason);
        }
        ImGui.EndChild();
        ImGui.TextWrapped("美容仅用于当前肖像，关闭 FireGaze修改 后还原。");
        if (!string.IsNullOrWhiteSpace(feature.Status)) ImGui.TextWrapped(feature.Status);
    }

    private bool DrawAppearanceOption(string id, PortraitAppearanceOption? option, float size, bool selected)
    {
        if (option?.Color is null)
        {
            var item = option is null ? null : new PortraitBrowserItem((uint)option.Value, option.Label, option.Icon, 0, option.Available, option.Reason ?? "");
            var clicked = DrawItemIcon(id, item, size, selected, null);
            if (option is { Icon: 0 })
            {
                var text = option.Label;
                if (text.Length > 5) text = option.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var position = ImGui.GetItemRectMin();
                ImGui.GetWindowDrawList().AddText(position + (new Vector2(size) - ImGui.CalcTextSize(text)) * .5f, ImGui.GetColorU32(FireGazePalette.Text), text);
            }
            return clicked;
        }
        var origin = ImGui.GetCursorScreenPos();
        var pressed = ImGui.ColorButton(id, ImGui.ColorConvertU32ToFloat4(option.Color.Value), ImGuiColorEditFlags.NoTooltip, new Vector2(size));
        FireGazeSymbols.DrawSelection(origin, origin + new Vector2(size), selected, ImGui.IsItemFocused());
        if (!option.Available) DrawUnavailableMarker(origin);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) || ImGui.IsItemFocused())
        {
            inspectedItem = new PortraitBrowserItem((uint)option.Value, option.Label, option.Icon, 0, option.Available, option.Reason ?? "");
            ImGui.SetTooltip(string.IsNullOrWhiteSpace(option.Reason) ? option.Label : $"{option.Label}\n{option.Reason}");
        }
        return pressed;
    }
}
