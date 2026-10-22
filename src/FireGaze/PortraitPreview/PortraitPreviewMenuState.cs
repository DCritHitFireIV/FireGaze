namespace FireGaze.PortraitPreview;

internal enum PortraitPreviewMenuStatus
{
    EditorUnavailable,
    Loading,
    Ready,
    Active,
    RestoreWarning,
}

internal readonly record struct PortraitPreviewMenuState(
    PortraitPreviewMenuStatus Status,
    bool CanOpenEditor,
    bool CanOpenPreview,
    bool CanOpenWindow,
    string? RestoreWarning)
{
    internal static PortraitPreviewMenuState Create(
        bool editorAvailable,
        bool hasIdentity,
        bool canBlockSave,
        bool active,
        string? restoreWarning)
    {
        if (!string.IsNullOrWhiteSpace(restoreWarning))
            return new(PortraitPreviewMenuStatus.RestoreWarning, !active, false, active, restoreWarning);
        if (active)
            return new(PortraitPreviewMenuStatus.Active, false, false, true, null);
        if (!editorAvailable)
            return new(PortraitPreviewMenuStatus.EditorUnavailable, true, false, false, null);
        if (!hasIdentity || !canBlockSave)
            return new(PortraitPreviewMenuStatus.Loading, true, false, false, null);
        return new(PortraitPreviewMenuStatus.Ready, true, true, false, null);
    }
}
