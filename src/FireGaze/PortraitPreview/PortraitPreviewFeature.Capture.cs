using System.Security.Cryptography;
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

    public PortraitImageScope LiveCaptureScope => captureScope;

    public void SetLiveCaptureScope(PortraitImageScope scope) => Mutate(() =>
    {
        if (scope is not (PortraitImageScope.Portrait or PortraitImageScope.VisibleAdventurerPlate)) return;
        if (captureScope == scope) return;
        captureScope = scope;
        liveImageService?.Close();
        requestedCaptureViewport = null;
        captureStateKey = "";
    });

    private void InitializeLiveCapture()
    {
        var source = new PortraitLiveCaptureSource(Plugin.Framework, Plugin.GameGUI, Plugin.Objects,
            Plugin.AddonLifecycle, ReadFreshCaptureEditor);
        liveCaptureAPI = new PortraitLiveCaptureApi(source, new PortraitCaptureTextures(Plugin.Textures));
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
        PollLiveCapture();
        liveImageService?.Draw(pointerHeld || sampledKeyboard);
    }
}
