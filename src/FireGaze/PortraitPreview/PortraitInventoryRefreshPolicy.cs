namespace FireGaze.PortraitPreview;

/// <summary>Prevents a full native inventory walk from running while the portrait editor is active.</summary>
internal static class PortraitInventoryRefreshPolicy
{
    public static bool ShouldRefresh(bool portraitPreviewActive, DateTime now, DateTime nextRefresh)
        => !portraitPreviewActive && now >= nextRefresh;
}
