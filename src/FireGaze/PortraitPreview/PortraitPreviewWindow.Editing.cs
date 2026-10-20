using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private static readonly ConditionalWeakTable<IPortraitPreviewViewModel, PortraitHistoryPresentation> HistoryPresentation = new();
    private bool historyPhotosView;
    private bool historyViewChanged;
    private float historyListScroll;
    private float historyPhotoScroll;
    private int historyPhotoRequests;

    public bool HistoryPhotosView => historyPhotosView;
    public bool HistoryWindowOpen => showHistory;

    public void OpenHistory(bool photoView)
    {
        historyViewChanged |= historyPhotosView != photoView;
        historyPhotosView = photoView;
        showHistory = true;
    }

    private void DrawHistoryPopup()
    {
        if (!showHistory) return;
        PrepareHistoryWindow();
        if (FireGazeWindowPlacement.Begin("肖像历史记录###FireGazePortraitHistory", ref showHistory, "FireGazePortraitHistory"))
        {

            if (DrawTab("操作记录", !historyPhotosView, true) && historyPhotosView)
            { historyPhotosView = false; historyViewChanged = true; }
            ImGui.SameLine();
            if (DrawTab("照片", historyPhotosView, true) && !historyPhotosView)
            { historyPhotosView = true; historyViewChanged = true; }
            DrawHistoryRows(feature, true);
        }
        ImGui.End();
    }

    internal void DrawHistoryRows(IPortraitPreviewViewModel feature, bool allowRestore)
    {
        historyPhotoRequests = 0;
        if (!string.IsNullOrWhiteSpace(feature.HistoryStatus)) ImGui.TextWrapped(feature.HistoryStatus);
        if (!allowRestore) ImGui.TextWrapped("请先打开对应的肖像编辑器。");
        ImGui.Separator();
        ImGui.BeginChild("###PortraitHistoryEntries", new Vector2(0, 0), false);
        if (historyViewChanged)
        {
            ImGui.SetScrollY(historyPhotosView ? historyPhotoScroll : historyListScroll);
            historyViewChanged = false;
        }
        var records = feature.History;
        if (records.Count == 0) ImGui.TextUnformatted("暂无编辑记录。");
        var days = HistoryPresentation.GetValue(feature, _ => new PortraitHistoryPresentation()).Get(records);
        foreach (var day in days)
        {
            if (!ImGui.CollapsingHeader(PortraitHistoryLabels.DayHeader(day.Date, day.Records.Count), ImGuiTreeNodeFlags.DefaultOpen)) continue;
            var dayRecords = day.Records;
            if (historyPhotosView)
            {
                DrawHistoryPhotos(dayRecords, allowRestore);
                continue;
            }
            var clipper = new ImGuiListClipper();
            clipper.Begin(dayRecords.Count, ImGui.GetTextLineHeightWithSpacing());
            while (clipper.Step())
            for (var index = clipper.DisplayStart; index < clipper.DisplayEnd; index++)
            {
                var record = dayRecords[index];
                ImGui.PushID(record.ID.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var label = PortraitHistoryLabels.RecordLabel(record);
                if (ImGui.Selectable(label, record.IsCurrent) && allowRestore) feature.RestoreHistory(record.ID);
                FireGazeSymbols.DrawSelection(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), record.IsCurrent, ImGui.IsItemFocused());
                DrawHistoryTooltip(record, allowRestore);
                ImGui.PopID();
            }
            clipper.End();
        }
        if (historyPhotosView) historyPhotoScroll = ImGui.GetScrollY(); else historyListScroll = ImGui.GetScrollY();
        ImGui.EndChild();
    }

    private void DrawHistoryPhotos(IReadOnlyList<PortraitHistoryListItem> records, bool allowRestore)
    {
        var gap = ImGui.GetStyle().ItemSpacing;
        var layout = PortraitHistoryPhotoLayout.For(ImGui.GetContentRegionAvail().X, gap.X, Scale);
        var captionHeight = ImGui.GetTextLineHeightWithSpacing() * 3;
        var rowHeight = layout.ImageHeight + captionHeight + gap.Y;
        var clipper = new ImGuiListClipper();
        clipper.Begin((records.Count + layout.Columns - 1) / layout.Columns, rowHeight);
        while (clipper.Step())
        for (var row = clipper.DisplayStart; row < clipper.DisplayEnd; row++)
        for (var column = 0; column < layout.Columns; column++)
        {
            var index = row * layout.Columns + column;
            if (index >= records.Count) break;
            var record = records[index];
            if (column != 0) ImGui.SameLine();
            ImGui.PushID(record.ID.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ImGui.BeginGroup();
            var origin = ImGui.GetCursorScreenPos();
            var card = new Vector2(layout.Width, rowHeight - gap.Y);
            var clicked = ImGui.Selectable("###PortraitHistoryPhoto", record.IsCurrent, ImGuiSelectableFlags.None, card);
            var focused = ImGui.IsItemFocused();
            var draw = ImGui.GetWindowDrawList();
            var imageSize = new Vector2(layout.Width, layout.ImageHeight);
            if (historyPhotoRequests++ < 32 && photos?.Get(record.ID) is { } texture)
                draw.AddImage(texture.Handle, origin, origin + imageSize);
            else
            {
                draw.AddRectFilled(origin, origin + imageSize, ImGui.GetColorU32(FireGazePalette.Panel));
                var text = "暂无照片";
                draw.AddText(origin + (imageSize - ImGui.CalcTextSize(text)) * .5f, ImGui.GetColorU32(FireGazePalette.Muted), text);
            }
            FireGazeSymbols.DrawSelection(origin, origin + card, record.IsCurrent, focused);
            DrawHistoryTooltip(record, allowRestore);
            var caption = origin + new Vector2(4 * Scale, layout.ImageHeight + gap.Y);
            draw.PushClipRect(origin, origin + card, true);
            draw.AddText(caption, ImGui.GetColorU32(FireGazePalette.Text), $"{record.At.ToLocalTime():HH:mm:ss}{(record.IsCurrent ? " · 当前" : "")}");
            draw.AddText(caption + new Vector2(0, ImGui.GetTextLineHeightWithSpacing()), ImGui.GetColorU32(FireGazePalette.Text), record.Label);
            draw.PopClipRect();
            if (clicked && allowRestore) feature.RestoreHistory(record.ID);
            ImGui.EndGroup();
            ImGui.PopID();
        }
        clipper.End();
    }

    private static void DrawHistoryTooltip(PortraitHistoryListItem record, bool allowRestore)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !ImGui.IsItemFocused()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
        ImGui.TextUnformatted($"{record.At.ToLocalTime():yyyy-MM-dd HH:mm:ss}{(record.IsCurrent ? " · 当前" : "")}");
        ImGui.TextWrapped(record.Label);
        ImGui.Separator();
        ImGui.TextWrapped(allowRestore ? "恢复此刻的装备、美容、姿势、镜头、灯光及装饰" : "请先打开对应肖像编辑器");
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
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
        ImGui.TextWrapped("美容仅用于当前肖像，关闭预览后还原。");
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
