namespace FireGaze.PortraitPreview;

internal enum PortraitPreviewMenuStatus
{
    EditorUnavailable,
    Loading,
    Ready,
    Active,
    RestoreWarning,
    TemporarilyUnavailable,
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
        string? restoreWarning,
        PortraitOpeningAvailability? availability = null)
    {
        var canOpen = availability?.CanOpenEditor ?? true;
        if (!string.IsNullOrWhiteSpace(restoreWarning))
            return new(PortraitPreviewMenuStatus.RestoreWarning, false, false, active, restoreWarning);
        if (active)
            return new(PortraitPreviewMenuStatus.Active, false, false, true, null);
        if (!editorAvailable)
            return new(PortraitPreviewMenuStatus.EditorUnavailable, canOpen, false, false, null);
        if (!hasIdentity || !canBlockSave)
            return new(PortraitPreviewMenuStatus.Loading, canOpen, false, false, null);
        var canPreview = availability is null || availability.EditorAlreadyOpen && !availability.HasBlockers && !availability.Opening;
        return new(canPreview ? PortraitPreviewMenuStatus.Ready : PortraitPreviewMenuStatus.TemporarilyUnavailable,
            canOpen, canPreview, false, null);
    }
}
