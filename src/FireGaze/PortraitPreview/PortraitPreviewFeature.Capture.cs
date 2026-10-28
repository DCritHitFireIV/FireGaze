using System.Security.Cryptography;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;

namespace FireGaze.PortraitPreview;

/// <summary>Owns high-resolution, local-only capture. Native identity is refreshed on Framework.</summary>
internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitLiveImageService? liveImageService;
    private PortraitLiveCaptureApi? liveCaptureAPI;
    private PortraitCaptureViewport? requestedCaptureViewport;
    private string captureStateKey = "";
    private DateTime nextCaptureRetry;
    private PortraitImageScope captureScope = PortraitImageScope.Portrait;
    private PortraitCaptureViewport? photoCaptureViewport;
    private PortraitCaptureSample? observedPlatePhotoSample;
    private Func<bool>? allowPlatePhotoObservation;

    public PortraitImageScope LiveCaptureScope => captureScope;

    private PortraitPhotoSource CurrentPhotoSource()
        => captureScope == PortraitImageScope.Portrait ? PortraitPhotoSource.Portrait
            : new(captureScope, Volatile.Read(ref observedPlatePhotoSample)?.Plate?.VisualKey);

    private bool ProductWorkspacePlateCaptureAllowed
        => captureScope == PortraitImageScope.VisibleAdventurerPlate && session.IsActive
            && !SourceCensusBlocksOperations && !OwnedViewportBlocksOperations
            && !LightingFitHistorySuppressed && !LightingFitMutationBlocked
            && !productPhotoPending && !photos.IsBusy && photos.IsPhysicallyIdle
            && liveImageService?.Diagnostics.cleanupFailures == 0;

    public PortraitWorkspaceImageBorrow? TryBorrowWorkspaceImage()
    {
        if (captureScope != PortraitImageScope.VisibleAdventurerPlate || !session.IsActive) return null;
        var captured = liveImageService?.AcquireCurrent();
        if (captured is null || !IsCurrentWorkspaceSample(captured.Sample))
        {
            captured?.Dispose();
            return null;
        }

        var sample = captured.Sample;
        var plate = sample.Plate!;
        var image = new PortraitWorkspaceImage(captured.Texture.Handle.Handle,
            Vector2.Zero, new(1, 1), uint.MaxValue, captured.Width, captured.Height,
            sample.Scope, sample.Owner.ContentID, sample.Editor.Editor.ToString("X"),
            sample.Editor.Generation, plate.VisualKey);
        return new(() => image, captured);
    }

    private bool IsCurrentWorkspaceSample(PortraitCaptureSample sample)
    {
        var observed = Volatile.Read(ref observedPlatePhotoSample);
        return sample.IsValid && sample.Scope == PortraitImageScope.VisibleAdventurerPlate
            && sample.Plate is { VisualKey: { Length: > 0 } }
            && observed is { IsValid: true, Plate: { } observedPlate }
            && sample.Owner == observed.Owner && sample.Editor == observed.Editor
            && string.Equals(sample.Plate.VisualKey, observedPlate.VisualKey, StringComparison.Ordinal)
            && observedIdentity.IsValid && observedIdentity == sample.Editor
            && observedKey is { ContentID: var contentID } && contentID == sample.Owner.ContentID
            && session.BlockSave(sample.Editor);
    }

    public void SetLiveCaptureScope(PortraitImageScope scope) => Mutate(() =>
    {
        if (scope is not (PortraitImageScope.Portrait or PortraitImageScope.VisibleAdventurerPlate)) return;
        if (captureScope == scope) return;
        if (!CancelLightingFitForEdit()) return;
        if (!FlushHistoryForSourceChange()) return;
        if (scope == PortraitImageScope.VisibleAdventurerPlate) StopNativeLive(PortraitNativeLiveStopReason.Disabled);
        photos?.InvalidateCurrentCapture();
        CancelPendingFavoriteSave("照片来源已改变，本次收藏未保存，请重新收藏。");
        captureScope = scope;
        Volatile.Write(ref observedPlatePhotoSample, null);
        liveImageService?.Close();
        requestedCaptureViewport = null;
        captureStateKey = "";
        if (scope == PortraitImageScope.Portrait
            && session.IsActive
            && target.TryResolve(out var identity, out _, out _))
            nativeLiveController?.RearmAutomaticSession(session.Revision, identity);
    });

    private void InitializeLiveCapture()
    {
        var source = new PortraitLiveCaptureSource(Plugin.Framework, Plugin.GameGUI, Plugin.Objects,
            Plugin.AddonLifecycle, ReadFreshCaptureEditor);
        liveCaptureAPI = new PortraitLiveCaptureApi(source, new PortraitCaptureTextures(Plugin.Textures, Plugin.TextureReadback));
        liveImageService = new PortraitLiveImageService(liveCaptureAPI);
    }

    private PortraitCaptureEditorFrame? ReadFreshCaptureEditor()
    {
        if (!session.IsActive || !target.TryResolve(out var identity, out _, out _)) return null;
        var snapshot = target.CaptureEditorState(identity);
        if (snapshot is null || !snapshot.Composition.Paused) return null;
        var json = snapshot.ToJSON();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new(identity, key);
    }

    private void PollLiveCapture()
    {
        if (liveImageService is null || !session.IsActive || !target.CanBlockSave)
        {
            liveImageService?.Close();
            requestedCaptureViewport = null;
            captureStateKey = "";
            return;
        }
        var viewport = ImGui.GetMainViewport();
        var current = new PortraitCaptureViewport(viewport.ID, viewport.Pos, viewport.Size);
        if (!current.IsValid || requestedCaptureViewport == current) return;
        requestedCaptureViewport = current;
        liveImageService.Request(captureScope, current);
        nextCaptureRetry = DateTime.UtcNow.AddSeconds(1);
    }

    private void ObserveLiveCaptureState()
    {
        // This observation also supports ordinary product photos when diagnostic
        // live capture is off. Consumers receive only this detached managed value.
        PortraitCaptureSample? photoSample = null;
        if (captureScope == PortraitImageScope.VisibleAdventurerPlate && session.IsActive
            && allowPlatePhotoObservation?.Invoke() != false
            && photoCaptureViewport is { IsValid: true } viewport && liveCaptureAPI is not null
            && liveCaptureAPI.TrySample(captureScope, viewport, out var visible)
            && visible is { IsValid: true, Plate: { } plate }
            && plate.AccountID == visible.Owner.AccountID && plate.ContentID == visible.Owner.ContentID)
            photoSample = visible;
        Volatile.Write(ref observedPlatePhotoSample, photoSample);
        if (!captureIsolation.LiveEnabled) return;
        var frame = ReadFreshCaptureEditor();
        if (frame is null)
        {
            captureStateKey = "";
            requestedCaptureViewport = null;
            return;
        }
        if (frame.StateKey != captureStateKey)
        {
            captureStateKey = frame.StateKey;
            requestedCaptureViewport = null;
        }
        if (liveImageService?.Error is not null && DateTime.UtcNow >= nextCaptureRetry)
            requestedCaptureViewport = null;
    }

    public PortraitCapturedImage? AcquireCurrentLiveImage() => liveImageService?.AcquireCurrent();

    private void DrawLiveCapture()
    {
        // ImGui viewport access stays on Draw; no native game owner is read here.
        var viewport = ImGui.GetMainViewport();
        photoCaptureViewport = new(viewport.ID, viewport.Pos, viewport.Size);
        if (LightingFitHistorySuppressed) return;
        // Diagnostic live capture is one consumer of the service, but the product
        // visible-plate workspace is another. The latter must remain independent of
        // the temporary diagnostic lease, otherwise mode=off/history never requests
        // or draws its owned plate image.
        if (!captureIsolation.LiveEnabled && !ProductWorkspacePlateCaptureAllowed) return;
        PollLiveCapture();
        liveImageService?.Draw(pointerHeld || sampledKeyboard);
    }
}
