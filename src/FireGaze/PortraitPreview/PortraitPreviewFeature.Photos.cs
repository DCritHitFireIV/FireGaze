namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitHistoryPhotos photos = null!;
    private PortraitPhotoReadbackRuntime? photoReadbackRuntime;
    private PortraitPhotoCaptureSource? platePhotoCapture;
    private long photoRecordsRevision = -1;
    private int photoRecordsBytes = -1;
    private bool? photoHistoryReferencesKnown;
    private long photoPublicationRevision = -1;
    private bool productPhotoPending;

    private void InitializePhotos(string directory)
    {
        photoReadbackRuntime = new(pluginInterface.UiBuilder, new PortraitPixelReadbackD3D11(pluginInterface.UiBuilder),
            error => Plugin.Log.Error("[PortraitPhoto] Nonblocking readback stopped: {Error}", error));
        allowPlatePhotoObservation = () => !SourceCensusBlocksOperations && !OwnedViewportBlocksOperations
            && !LightingFitHistorySuppressed && !LightingFitMutationBlocked;
        platePhotoCapture = new(() => Volatile.Read(ref observedPlatePhotoSample),
            sample => liveCaptureAPI?.CaptureAsync(sample) ?? Task.FromResult<PortraitCaptureLayers?>(null),
            (texture, placement) => new PortraitCaptureSoftwareCompositor(Plugin.Textures, photoReadbackRuntime.Provider)
                .ComposeAsync(160, 256, [new(texture, placement)]));
        photos = new(Path.Combine(directory, "portrait-history-thumbnails-v1.bin"), Plugin.Textures, photoReadbackRuntime.Provider,
            new(Plugin.Framework, Plugin.GameGUI, Plugin.Textures, target.IsCurrent), history.Bytes,
            message => Plugin.Log.Information("[PortraitPhoto] {Message}", message),
            Path.Combine(directory, "portrait-favorite-photos-v1.json"),
            favoritesStore.IsReadOnly || favoritesTrashStore.IsReadOnly ? null : FavoriteSnapshotsForPhotos(),
            storage: storageBudget,
            qualifyCapture: source => platePhotoCapture.Qualify(source),
            captureSource: (identity, source, token, operation) => platePhotoCapture.CaptureAsync(identity, source, token, operation));
    }

    private void PollPhotos()
    {
        var referencesKnown = !history.IsReadOnly;
        var changedRecords = photoRecordsRevision != history.NavigationRevision || photoRecordsBytes != history.Bytes
            || photoHistoryReferencesKnown != referencesKnown;
        if (changedRecords)
        {
            photoRecordsRevision = history.NavigationRevision;
            photoRecordsBytes = history.Bytes;
            photoHistoryReferencesKnown = referencesKnown;
            photos.SetRecords(history.GetAllRecords(), history.Bytes, referencesKnown);
        }
        // Product photos are independent of the bounded diagnostic readback switch.
        // Every admission and asynchronous completion observes the live owner and
        // snapshot again; drawing never qualifies a capture from a cached frame.
        var mode = captureIsolation.Mode;
        var photoSource = CurrentPhotoSource();
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
            && photos.NeedsCurrentCapture(currentID, snapshot, owner?.ContentID ?? 0, photoSource);
        // History takes fresh independent SRV references and copies into its own pixels.
        // A stable same-owner live display may remain retained during that read-only copy;
        // pending acquisition/retirement or uncertain cleanup still excludes admission.
        ready &= !productPhotoPending || nativeLiveController?.IsPhysicallyIdle != false
            || owner is { } photoOwner && nativeLiveController?.CanShareReadOnlyPhotoSource(observedIdentity,
                new(photoOwner.AccountID, photoOwner.ContentID)) == true;
        var ownerKey = ready ? $"{key}|{observedIdentity}|{session.Revision}" : "";
        photos.Poll(ownerKey, observedIdentity, currentID, snapshot, ready, owner?.ContentID ?? 0, photoSource);
        historyPhotoStatus = photos.HistoryPublicationStatus;
        if (changedRecords || photoPublicationRevision != photos.ImageRevision)
        {
            photoPublicationRevision = photos.ImageRevision;
            if (history.SynchronizePhotographedRecords(photos.GetPhotographedRecordIDs())) RefreshHistoryRows();
        }
    }
}
