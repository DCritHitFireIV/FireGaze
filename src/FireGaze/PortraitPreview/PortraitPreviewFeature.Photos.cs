namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitHistoryPhotos photos = null!;
    private PortraitHistoryListItem[]? photoRows;

    private void InitializePhotos(string directory)
        => photos = new(Path.Combine(directory, "portrait-history-thumbnails-v1.bin"), Plugin.Textures, Plugin.TextureReadback,
            new(Plugin.Framework, Plugin.GameGUI, Plugin.Textures, target.IsCurrent), history.Bytes,
            message => Plugin.Log.Information("[PortraitPhoto] {Message}", message));

    private void PollPhotos()
    {
        if (LightingFitHistorySuppressed) return;
        if (!ReferenceEquals(photoRows, historyRows))
        {
            photoRows = historyRows;
            var owner = CurrentCharacter();
            photos.SetRecords(owner is { } character ? history.GetRecordsForOwner(character.ContentID) : [], history.Bytes);
        }
        var ready = false;
        captureIsolation.RunHistory(() => ready = session.IsActive && observedKey is not null && observedEditor is not null
            && !pointerHeld && !sampledKeyboard && target.TryResolve(out var identity, out _, out _)
            && identity == observedIdentity);
        var key = observedKey;
        var currentID = key is { } current ? history.CurrentRecordID(current) : null;
        var ownerKey = ready ? $"{key}|{observedIdentity}" : "";
        var snapshot = ready && photos.NeedsStateValidation ? target.CaptureEditorState(observedIdentity) : observedEditor;
        photos.Poll(ownerKey, observedIdentity, currentID, snapshot, ready && snapshot is not null);
    }
}
