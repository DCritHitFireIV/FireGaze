using FireGaze.PortraitPreview;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private readonly DevelopmentCameraPluginBoundary pluginInterface = new();
    private bool disposed;
    public bool SourceCensusBlocksOperations;
    private bool LightingFitMutationBlocked;
    private bool productPhotoPending;
    private DevelopmentCameraCaptureBoundary? liveImageService;
    private DevelopmentCameraCaptureBoundary? lightingFitCapture;
    public readonly Queue<Action> DevelopmentCameraQueuedActions = new();
    public bool DevelopmentCameraPreservedCensus;
    public long DevelopmentCameraSessionRevision => session.Revision;
    public void SetDevelopmentCameraGate(string gate)
    {
        switch (gate)
        {
            case "not-dev": pluginInterface.IsDev = false; break;
            case "disposed": disposed = true; break;
            case "diagnostic": SourceCensusBlocksOperations = true; break;
            case "fit": LightingFitMutationBlocked = true; break;
            case "suppressed": LightingFitHistorySuppressed = true; break;
            case "photo-busy": photos.IsBusy = true; break;
            case "photo-drain": photos.IsPhysicallyIdle = false; break;
            case "photo-pending": productPhotoPending = true; break;
            case "live-busy": liveImageService = new() { IsBusy = true }; break;
            case "live-drain": liveImageService = new() { IsPhysicallyIdle = false }; break;
            case "fit-busy": lightingFitCapture = new() { IsBusy = true }; break;
            case "fit-drain": lightingFitCapture = new() { IsPhysicallyIdle = false }; break;
            case "pointer": pointerHeld = true; break;
            case "keyboard": FireGaze.Plugin.Keys.Held = true; break;
            case "no-save": target.CanBlockSave = false; break;
            case "not-ready": target.Ready = false; break;
            case "not-current": target.Current = false; break;
            case "owner": Owner = new(10, 99); break;
            case "account": Owner = new(99, 20); break;
            case "generation": target.Identity = target.Identity with { Generation = 99 }; break;
            case "no-generation": target.Identity = target.Identity with { Generation = 0 }; break;
            case "session": _ = session.Close(); _ = session.Open(target.Identity); break;
            default: throw new ArgumentException(gate);
        }
    }
    public void DrainDevelopmentCameraCommands()
    {
        FireGaze.Plugin.Framework.IsInFrameworkUpdateThread = true;
        while (DevelopmentCameraQueuedActions.TryDequeue(out var action)) action();
    }
}

internal sealed class DevelopmentCameraPluginBoundary { public bool IsDev = true; }
internal sealed class DevelopmentCameraCaptureBoundary
{
    public bool IsBusy;
    public bool IsPhysicallyIdle = true;
}
