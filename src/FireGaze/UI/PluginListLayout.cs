using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

internal readonly record struct PluginListColumns(float Content, float Actions, float Gap);
internal readonly record struct PluginListSegments(int BeforeCount, int ExpandedIndex, int AfterStart, int AfterCount);
internal readonly record struct DiscoveryActionPositions(float LikeX, float NavigateX, float LibraryX);
internal readonly record struct PluginListActionPosition(float X, int Row);

internal static class PluginListLayout
{
    public const float IconSize = 40f;
    public const float IconTextGap = 12f;
    public const float DetailInset = IconSize + IconTextGap;
    public const float SearchWidth = 300f;
    public const float FilterWidth = 160f;
    public static float MainSlotWidth() => MaxWidth("写入并重载", "一键汉化", "更新汉化", "点击更新", "重试");
    public static float OpenSlotWidth() => MaxWidth("启用插件", "设置", "打开");
    private static float MaxWidth(params string[] labels) => labels.Max(UiHelpers.LabelWidth);

    public static float RefreshSlotWidth() => MaxWidth("刷新", "刷新中…");

    public static float MeasureFoldedRowHeight(float textLineHeight, float frameHeight, float spacing)
        => MathF.Max(frameHeight + 16f, MathF.Max(IconSize, textLineHeight * 3f + spacing * 2f) + MathF.Max(4f, spacing));

    public static float MeasureFoldedTextOffset(float rowHeight, float textHeight, float spacing)
        => MathF.Max(0f, (rowHeight - textHeight - spacing * 2f) * 0.5f);

    public static void BeginFoldedRow(float height)
        => ImGui.TableNextRow(ImGuiTableRowFlags.None, height + ImGui.GetStyle().CellPadding.Y * 2f);

    public static float FoldedRowHeight() => MeasureFoldedRowHeight(ImGui.GetTextLineHeight(),
        ImGui.GetFrameHeight(), ImGui.GetStyle().ItemSpacing.Y);

    public static float MeasureHeaderEnd(float origin, float current, float frameHeight, float spacing)
        => MathF.Max(current, origin + 3f * (frameHeight + spacing));

    public static int MeasureToolbarRows(float width, float[] controlWidths, float spacing)
    {
        var rows = 1;
        var used = 0f;
        foreach (var control in controlWidths)
        {
            var next = used > 0 ? used + spacing + control : control;
            if (used > 0 && next > width)
            {
                rows++;
                used = control;
            }
            else used = next;
        }
        return rows;
    }

    private static int ToolbarRows() => MeasureToolbarRows(
        ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X,
        [RefreshSlotWidth(), SearchWidth, MaxWidth("状态", "排序") + FilterWidth + ImGui.GetStyle().ItemSpacing.X,
            UiHelpers.LabelWidth("翻译设置…"), UiHelpers.LabelWidth("反馈…")], 10f);

    public static void ToolbarSameLineOrWrap(float width)
    {
        // The preceding item already advanced the cursor to the next row.
        if (ImGui.GetItemRectMax().X + 10f + width <= ImGui.GetWindowDrawList().GetClipRectMax().X)
            ImGui.SameLine(0, 10f);
    }

    public static void FinishToolbarRow(float origin)
        => ImGui.SetCursorPosY(MathF.Max(ImGui.GetCursorPosY(),
            origin + ToolbarRows() * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y)));

    public static void FinishHeader(float origin)
    {
        var minimum = MeasureHeaderEnd(origin, ImGui.GetCursorPosY(),
            ImGui.GetFrameHeight(), ImGui.GetStyle().ItemSpacing.Y);
        ImGui.SetCursorPosY(MathF.Max(minimum, origin + (ToolbarRows() + 2f)
            * (ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y)));
        ImGui.Separator();
    }

    public static float MeasureSharedActionsWidth(float discovery, float translation) => MathF.Max(discovery, translation);

    public static float ActionsColumnWidth()
    {
        var padding = ImGui.GetStyle().CellPadding.X * 2f + 8f;
        var discovery = padding + MathF.Max(40f, ImGui.GetFontSize() * 2.6f) + 12f
            + MaxWidth("加入自己的库", "启用") + 10f
            + MaxWidth("一键安装", "安装中…", "去汉化", "安装器") + ImGui.GetStyle().ItemSpacing.X;
        var translation = padding + MainSlotWidth() + OpenSlotWidth()
            + UiHelpers.LabelWidth("还原原文") + UiHelpers.LabelWidth("一键上传") + 46f + ImGui.GetFrameHeight() + 4f;
        return MeasureSharedActionsWidth(discovery, translation);
    }

    public static float MeasureDetailValueX(float origin, float fontSize) => origin + fontSize * 5.2f;

    public static void DrawDetailValue(string label, string value)
    {
        var startX = ImGui.GetCursorPosX();
        ImGui.TextDisabled(label);
        ImGui.SameLine(MeasureDetailValueX(startX, ImGui.GetFontSize()));
        ImGui.TextWrapped(value);
    }

    public static PluginListColumns MeasureLegacyColumns(float width, float scrollbarSize, float actionsWidth)
    {
        var gap = MathF.Min(MathF.Max(0, width), MathF.Max(0, scrollbarSize) + 48f);
        var usable = MathF.Max(0, width - gap);
        var contentMinimum = MathF.Min(180f, usable * .5f);
        var actions = MathF.Min(MathF.Max(0, actionsWidth), usable - contentMinimum);
        return new(usable - actions, actions, gap);
    }

    /// <summary>Position every visible action within its column; zero-width slots keep their identity but take no space.</summary>
    public static PluginListActionPosition[] MeasureActionFlow(float width, float[] widths, float[] spacing, bool alignRight)
    {
        if (widths.Length != spacing.Length) throw new ArgumentException("Action spacing must match its slots.");
        var result = new PluginListActionPosition[widths.Length];
        var rowWidths = new List<float>();
        var row = 0;
        var used = 0f;
        for (var i = 0; i < widths.Length; i++)
        {
            if (widths[i] <= 0) { result[i] = new(0, -1); continue; }
            var x = used > 0 ? used + spacing[i] : 0;
            if (used > 0 && x + widths[i] > width)
            {
                rowWidths.Add(used);
                row++;
                x = 0;
            }
            result[i] = new(x, row);
            used = x + widths[i];
        }
        rowWidths.Add(used);
        if (alignRight)
            for (var i = 0; i < result.Length; i++)
                if (result[i].Row >= 0)
                    result[i] = result[i] with { X = result[i].X + MathF.Max(0, width - rowWidths[result[i].Row]) };
        return result;
    }

    public static float ActionFlowHeight(PluginListActionPosition[] positions, float frameHeight, float spacing)
    {
        var rows = positions.Length == 0 ? 0 : positions.Max(p => p.Row) + 1;
        return rows <= 0 ? 0 : rows * frameHeight + (rows - 1) * spacing;
    }

    public static float RowHeightForActions(float columnWidth)
    {
        var frame = ImGui.GetFrameHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var available = MathF.Max(0, columnWidth - ImGui.GetStyle().CellPadding.X * 2f);
        var translation = MeasureActionFlow(available,
            [MainSlotWidth() + 4f + frame, OpenSlotWidth(), UiHelpers.LabelWidth("还原原文"), UiHelpers.LabelWidth("一键上传")],
            [0, 12, 12, 10], false);
        var discovery = MeasureActionFlow(available,
            [InsStyle.PinkButtonWidth("加入自己的库"), MaxWidth("一键安装", "安装中…", "去汉化", "安装器"), MathF.Max(40f, ImGui.GetFontSize() * 2.6f)],
            [0, ImGui.GetStyle().ItemSpacing.X, ImGui.GetStyle().ItemSpacing.X], true);
        return MathF.Max(FoldedRowHeight(), MathF.Max(ActionFlowHeight(translation, frame, spacing),
            ActionFlowHeight(discovery, frame, spacing)) + spacing * 2f);
    }

    public static PluginListSegments SplitRows(int count, int expandedIndex)
        => expandedIndex >= 0 && expandedIndex < count
            ? new(expandedIndex, expandedIndex, expandedIndex + 1, count - expandedIndex - 1)
            : new(count, -1, count, 0);

    public static DiscoveryActionPositions MeasureDiscoveryActions(float right, float likeWidth,
        float navigateWidth, float libraryWidth, float spacing)
    {
        var like = right - likeWidth;
        var navigate = navigateWidth > 0 ? like - spacing - navigateWidth : like;
        var library = libraryWidth > 0 ? navigate - spacing - libraryWidth : navigate;
        return new(like, navigate, library);
    }

}
