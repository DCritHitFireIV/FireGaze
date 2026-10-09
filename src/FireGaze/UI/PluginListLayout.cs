using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

internal readonly record struct PluginListColumns(float Content, float Actions, float Gap);
internal readonly record struct PluginListSegments(int BeforeCount, int ExpandedIndex, int AfterStart, int AfterCount);
internal readonly record struct DiscoveryActionPositions(float LikeX, float NavigateX, float LibraryX);

internal static class PluginListLayout
{
    public const float IconSize = 40f;
    public const float IconTextGap = 12f;
    public const float DetailInset = IconSize + IconTextGap;
    public const float SearchWidth = 300f;
    public const float FilterWidth = 160f;
    public static float MainSlotWidth() => MaxWidth("写入并重载", "一键汉化", "重新汉化", "点击更新", "重试");
    public static float OpenSlotWidth() => MaxWidth("启用插件", "设置", "打开");
    private static float MaxWidth(params string[] labels) => labels.Max(UiHelpers.LabelWidth);

    public static float RefreshSlotWidth() => MaxWidth("刷新", "刷新中…");

    public static float MeasureFoldedRowHeight(float textLineHeight, float frameHeight, float spacing)
        => MathF.Max(frameHeight + 16f, MathF.Max(IconSize, textLineHeight * 3f + spacing * 2f) + 4f);

    public static float FoldedRowHeight() => MeasureFoldedRowHeight(ImGui.GetTextLineHeight(),
        ImGui.GetFrameHeight(), ImGui.GetStyle().ItemSpacing.Y);

    public static float MeasureHeaderEnd(float origin, float current, float frameHeight, float spacing)
        => MathF.Max(current, origin + 3f * (frameHeight + spacing));

    public static void FinishHeader(float origin)
    {
        ImGui.SetCursorPosY(MeasureHeaderEnd(origin, ImGui.GetCursorPosY(),
            ImGui.GetFrameHeight(), ImGui.GetStyle().ItemSpacing.Y));
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
            + UiHelpers.LabelWidth("还原原文") + UiHelpers.LabelWidth("一键上传") + 46f;
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
        => new(MathF.Max(180f, width - actionsWidth - scrollbarSize - 48f), actionsWidth, scrollbarSize + 48f);

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
