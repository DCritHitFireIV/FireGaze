using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Adapts an ordinary visible-backbuffer card photograph to the existing photo
/// container. The observed sample is detached Framework data; it is not a receipt
/// for an exact rendered game frame. Native UI covering the card remains visible.
/// The history service owns this task and its physical lifetime through close.
/// </summary>
internal sealed class PortraitPhotoCaptureSource(
    Func<PortraitCaptureSample?> readObservedSample,
    Func<PortraitCaptureSample, Task<PortraitCaptureLayers?>> capturePlate,
    Func<IDalamudTextureWrap, PortraitCaptureRect, Task<IDalamudTextureWrap>> preparePhoto)
{
    public object? Qualify(PortraitPhotoSource? requested)
    {
        var sample = readObservedSample();
        return requested is { IsReady: true, Scope: PortraitImageScope.VisibleAdventurerPlate } source
            && sample is { IsValid: true, Scope: PortraitImageScope.VisibleAdventurerPlate, Plate: { } plate }
            && plate.AccountID == sample.Owner.AccountID && plate.ContentID == sample.Owner.ContentID
            && source.VisualKey == plate.VisualKey ? sample : null;
    }

    public Task<PortraitThumbnailLayers?> CaptureAsync(PortraitIdentity identity, PortraitPhotoSource? requested,
        CancellationToken cancellationToken = default, PortraitPhotoTrace.Operation? operation = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Qualify(requested) is not PortraitCaptureSample sample || sample.Editor != identity)
            return Task.FromResult<PortraitThumbnailLayers?>(null);
        // Start the external synchronous prefix on Framework while the history
        // physical lease is already held. Do not cancel the scheduled public
        // viewport task; the owning service discards late work after it settles.
        return PrepareAsync(sample, capturePlate(sample), cancellationToken);
    }

    private async Task<PortraitThumbnailLayers?> PrepareAsync(PortraitCaptureSample expected,
        Task<PortraitCaptureLayers?> pending, CancellationToken cancellationToken)
    {
        PortraitCaptureLayers? captured = null;
        IDalamudTextureWrap? prepared = null;
        PortraitThumbnailLayers? result = null;
        Exception? failure = null;
        try
        {
            captured = await pending.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (captured is { RequiresComposition: false, Layers.Count: 1 }
                && readObservedSample() == expected
                && PortraitPlatePhotoGeometry.TryContain(captured.Width, captured.Height, 160, 256, out var fit))
            {
                var placement = new PortraitCaptureRect(fit.OffsetX / 160f, fit.OffsetY / 256f,
                    fit.Width / 160f, fit.Height / 256f);
                prepared = await preparePhoto(captured.Layers[0].Texture, placement).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (prepared.Width != 160 || prepared.Height != 256)
                    throw new InvalidOperationException("Unexpected visible Plate photo dimensions.");
                if (readObservedSample() == expected)
                { result = new(prepared, null, null); prepared = null; }
            }
        }
        catch (Exception error) { failure = error; }
        try { PortraitCaptureCleanupException.DisposeAll(captured, prepared); }
        catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException(failure, cleanup); }
        if (failure is not null)
        {
            try { PortraitCaptureCleanupException.DisposeAll(result); }
            catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result;
    }
}
