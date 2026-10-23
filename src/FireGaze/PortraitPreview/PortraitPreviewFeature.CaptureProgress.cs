using System.Text.Json;
using Dalamud.Plugin.Ipc;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private ICallGateProvider<string>? captureProgress;

    private void InitializeCaptureProgress()
    {
        captureProgress = pluginInterface.GetIpcProvider<string>("FireGaze.PortraitPreview.CaptureProgress");
        // Deliberately no OnFramework dispatch or native/texture/service diagnostics.
        // This immutable managed view remains callable when the game thread stalls.
        try { captureProgress.RegisterFunc(ReadCaptureProgress); }
        catch
        {
            // This is the last constructor step. A failed partial registration
            // cannot leave a callback to a constructor which did not return.
            captureProgress.UnregisterFunc();
            captureProgress = null;
            throw;
        }
    }

    private string ReadCaptureProgress() => JsonSerializer.Serialize(new
    {
        version = typeof(Plugin).Assembly.GetName().Version?.ToString(),
        mvid = typeof(Plugin).Assembly.ManifestModule.ModuleVersionId,
        stages = photos.ReadCaptureProgress(),
        lastFailure = photos.ReadCaptureFailure(),
    });

    private void DisposeCaptureProgress() => captureProgress?.UnregisterFunc();
}
