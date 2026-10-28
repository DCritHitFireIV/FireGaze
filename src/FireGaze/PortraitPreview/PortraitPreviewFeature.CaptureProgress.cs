using System.Text.Json;
using Dalamud.Plugin.Ipc;
using FireGaze.LocalPreview.Runtime;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private ICallGateProvider<string>? captureProgress;
    private bool captureProgressRegistrationAttempted;
    private Exception? captureProgressCloseFailure;

    private void InitializeCaptureProgress()
    {
        captureProgress = pluginInterface.GetIpcProvider<string>("FireGaze.PortraitPreview.CaptureProgress");
        // Deliberately no OnFramework dispatch or native/texture/service diagnostics.
        // This immutable managed view remains callable when the game thread stalls.
        try
        {
            captureProgressRegistrationAttempted = true;
            captureProgress.RegisterFunc(ReadCaptureProgress);
        }
        catch (Exception initializationFailure)
        {
            // This is the last constructor step. A failed partial registration
            // cannot leave a callback to a constructor which did not return.
            try { DisposeCaptureProgress(); }
            catch (Exception rollbackFailure)
            {
                throw new NativePhysicalCloseException(this, new AggregateException(initializationFailure, rollbackFailure));
            }
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

    private void DisposeCaptureProgress()
    {
        if (captureProgressCloseFailure is not null) throw captureProgressCloseFailure;
        if (!captureProgressRegistrationAttempted) return;
        // A failed backend return cannot prove removal. Record the attempt before
        // crossing it and keep both the provider and first failure for the parent.
        captureProgressRegistrationAttempted = false;
        try { captureProgress?.UnregisterFunc(); }
        catch (Exception failure) { throw captureProgressCloseFailure = failure; }
        captureProgress = null;
    }
}
