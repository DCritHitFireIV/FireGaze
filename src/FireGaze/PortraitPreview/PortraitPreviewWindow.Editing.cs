using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private void DrawUndoRedo()
    {
        ImGui.BeginDisabled(!feature.CanUndo);
        if (ImGui.Button("撤回")) feature.Undo();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!feature.CanRedo);
        if (ImGui.Button("重做")) feature.Redo();
        ImGui.EndDisabled();
    }

    private void DrawEditingToolbar()
    {
        ImGui.PushID("PortraitEditingToolbar");
        DrawUndoRedo();
        ImGui.SameLine();
        if (ImGui.Button("历史记录")) showHistory = !showHistory;
        ImGui.SameLine();
        var sync = feature.SyncPlate;
        if (ImGui.Checkbox("肖像实时同步铭牌", ref sync)) feature.SetSyncPlate(sync);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("只同步本地铭牌预览，关闭编辑器后还原，不保存到实际铭牌。");
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
        ImGui.TextWrapped("按日期保留编辑记录；点击一条即可恢复，恢复操作也会保留。达到 10MB 后逐步清理最早记录。");
        if (!string.IsNullOrWhiteSpace(feature.HistoryStatus)) ImGui.TextDisabled(feature.HistoryStatus);
        ImGui.Separator();
        ImGui.BeginChild("###PortraitHistoryEntries", new Vector2(0, 0), false);
        var records = feature.History;
        if (records.Count == 0) ImGui.TextDisabled("尚无记录。打开肖像编辑器后开始记录。");
        foreach (var day in records.OrderByDescending(record => record.At).GroupBy(record => record.At.ToLocalTime().Date))
        {
            if (!ImGui.CollapsingHeader($"{day.Key:yyyy-MM-dd} · {day.Count()} 条", ImGuiTreeNodeFlags.DefaultOpen)) continue;
            var dayRecords = day.ToArray();
            var clipper = new ImGuiListClipper();
            clipper.Begin(dayRecords.Length, ImGui.GetTextLineHeightWithSpacing());
            while (clipper.Step())
            for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var record = dayRecords[index];
                ImGui.PushID(record.ID.ToString(System.Globalization.CultureInfo.InvariantCulture));
                ImGui.BeginDisabled(!allowRestore);
                var label = $"{record.At.ToLocalTime():HH:mm:ss}  {record.Label}";
                if (ImGui.Selectable(label, record.IsCurrent)) feature.RestoreHistory(record.ID);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
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
        ImGui.BeginChild("###PortraitAppearanceGroups", new Vector2(198 * Scale, height), true);
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var current = group.Options.FirstOrDefault(option => option.Value == group.CurrentValue);
            if (i % 2 != 0) ImGui.SameLine();
            ImGui.PushID(group.ID);
            ImGui.BeginGroup();
            var active = selected.ID == group.ID;
            if (DrawAppearanceOption("###AppearanceCategory", current, 80 * Scale, active)) selectedAppearance = group.ID;
            ImGui.TextUnformatted(group.Label);
            ImGui.EndGroup();
            ImGui.PopID();
        }
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("###PortraitAppearanceOptions", new Vector2(0, height), true);
        ImGui.TextUnformatted(selected.Label);
        if (!string.IsNullOrWhiteSpace(selected.Description)) ImGui.TextDisabled(selected.Description);
        ImGui.Separator();
        var onlyColors = selected.Options.All(option => option.Color is not null);
        var edge = (onlyColors ? 34 : 82) * Scale;
        var spacing = ImGui.GetStyle().ItemSpacing;
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing.X) / (edge + spacing.X)));
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
                ImGui.BeginDisabled(!option.Available);
                if (DrawAppearanceOption("###AppearanceOption", option, edge, option.Value == selected.CurrentValue))
                    feature.ChangeAppearance(selected.ID, option.Value);
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        }
        clipper.End();
        ImGui.EndChild();
        ImGui.TextUnformatted("仅改变当前肖像；关闭 FireGaze修改 后还原美容。");
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
                ImGui.GetWindowDrawList().AddText(position + (new Vector2(size) - ImGui.CalcTextSize(text)) * .5f, 0xffffffff, text);
            }
            return clicked;
        }
        var origin = ImGui.GetCursorScreenPos();
        var pressed = ImGui.ColorButton(id, ImGui.ColorConvertU32ToFloat4(option.Color.Value), ImGuiColorEditFlags.NoTooltip, new Vector2(size));
        if (selected) ImGui.GetWindowDrawList().AddRect(origin, origin + new Vector2(size), 0xff80dcff, 3 * Scale, ImDrawFlags.None, 2 * Scale);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(option.Reason ?? option.Label);
        return pressed;
    }
}
