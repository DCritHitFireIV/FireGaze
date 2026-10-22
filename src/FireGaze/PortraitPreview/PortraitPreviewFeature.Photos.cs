namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitHistoryPhotos photos = null!;
    private long photoRecordsRevision = -1;
    private int photoRecordsBytes = -1;
    private long photoPublicationRevision = -1;
    private bool productPhotoPending;

    private void InitializePhotos(string directory)
    {
        photos = new(Path.Combine(directory, "portrait-history-thumbnails-v1.bin"), Plugin.Textures, Plugin.TextureReadback,
            new(Plugin.Framework, Plugin.GameGUI, Plugin.Textures, target.IsCurrent), history.Bytes,
            message => Plugin.Log.Information("[PortraitPhoto] {Message}", message),
            Path.Combine(directory, "portrait-favorite-photos-v1.json"),
            favoritesStore.IsReadOnly || favoritesTrashStore.IsReadOnly ? null : FavoriteSnapshotsForPhotos(),
            storage: storageBudget);
    }

    private void PollPhotos()
    {
        var changedRecords = photoRecordsRevision != history.NavigationRevision || photoRecordsBytes != history.Bytes;
        if (changedRecords)
        {
            photoRecordsRevision = history.NavigationRevision;
            photoRecordsBytes = history.Bytes;
            photos.SetRecords(history.GetAllRecords(), history.Bytes);
        }
        // Product photos are independent of the bounded diagnostic readback switch.
        // Every admission and asynchronous completion observes the live owner and
        // snapshot again; drawing never qualifies a capture from a cached frame.
        var mode = captureIsolation.Mode;
        var key = observedKey;
        var currentID = key is { } current ? history.CurrentRecordID(current) : null;
        var owner = CurrentCharacter();
        var ready = mode is "off" or "history" && session.IsActive && target.CanBlockSave
            && key is { } recordKey && owner is { } character && recordKey.ContentID == character.ContentID && observedEditor is not null
            && !pointerHeld && !sampledKeyboard && !lightDrag.IsActive
            && !LightingFitHistorySuppressed && !LightingFitMutationBlocked
            && !SourceCensusBlocksOperations && !OwnedViewportBlocksOperations
            && liveImageService?.IsBusy != true && liveImageService?.IsPhysicallyIdle != false
            && lightingFitCapture?.IsBusy != true && lightingFitCapture?.IsPhysicallyIdle != false
            && target.TryResolve(out var identity, out _, out _) && identity == observedIdentity
            && session.BlockSave(identity);
        var snapshot = ready ? target.CaptureEditorState(observedIdentity) : null;
        ready &= snapshot is not null && observedEditor!.SameForHistory(snapshot)
            && target.IsCurrent(observedIdentity) && CurrentCharacter() == owner;
        productPhotoPending = mode == "off" && ready
            && photos.NeedsCurrentCapture(currentID, snapshot, owner?.ContentID ?? 0);
        // A current native-live wrap is retired by its ordinary ContextBusy update.
        // Its automatic retry intent survives; no Stop/disable call is made here.
        ready &= !productPhotoPending || nativeLiveController?.IsPhysicallyIdle != false;
        var ownerKey = ready ? $"{key}|{observedIdentity}|{session.Revision}" : "";
        photos.Poll(ownerKey, observedIdentity, currentID, snapshot, ready, owner?.ContentID ?? 0);
        historyPhotoStatus = photos.HistoryPublicationStatus;
        if (changedRecords || photoPublicationRevision != photos.ImageRevision)
        {
            photoPublicationRevision = photos.ImageRevision;
            if (history.SynchronizePhotographedRecords(photos.GetPhotographedRecordIDs())) RefreshHistoryRows();
        }
    }
}
