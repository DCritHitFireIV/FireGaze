using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

internal readonly record struct PluginListColumns(float Content, float Actions, float Gap);
internal readonly record struct PluginListSegments(int BeforeCount, int ExpandedIndex, int AfterStart, int AfterCount);
internal readonly record struct DiscoveryActionPositions(float LikeX, float NavigateX, float LibraryX);

internal static class PluginListLayout
{
    public const float IconSize = 40f;
    public static float MainSlotWidth() => MaxWidth("写入并重载", "一键汉化", "重新汉化", "点击更新", "重试");
    public static float OpenSlotWidth() => MaxWidth("启用插件", "设置", "打开");
    private static float MaxWidth(params string[] labels) => labels.Max(UiHelpers.LabelWidth);

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
