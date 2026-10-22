namespace FireGaze.LocalPreview.Core;

public static class LocalPreviewStatusPresentation
{
    public static string UserStatusSummary(bool enabled, bool initialized, bool runtimeReady, int activePreviewCount)
    {
        if (!initialized || !runtimeReady)
            return "试用功能正在初始化，请稍后再试。";

        var state = enabled ? "已开启" : "已关闭";
        return $"本地试用{state}，当前有 {Math.Max(0, activePreviewCount)} 项试用。";
    }

    public static string? VisibleMessage(bool initialized, bool runtimeReady, bool coexistenceAllowed, string? lastMessage)
    {
        if (!initialized || !runtimeReady)
            return "试用功能正在初始化，请稍后再试。";
        if (!coexistenceAllowed)
            return null;
        return lastMessage;
    }
}
