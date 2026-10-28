namespace FireGaze.LocalPreview.Runtime;

public sealed unsafe partial class ActorPreviewRuntime : INativePhysicalCloseState
{
    private NativePhysicalCloseState? physicalClose;
    private NativePhysicalCloseState PhysicalClose => physicalClose ??= new(this);
    public bool PhysicalCloseComplete => disposed && PhysicalClose.Complete;
    public string? PhysicalCloseFailure => PhysicalClose.Failure;
    private bool TryDisposeNativeHook(IDisposable hook)
    {
        var complete = PhysicalClose.CloseHook(hook);
        if (!complete)
        {
            nativeClosing = true;
            try { log.Warning("[LocalPreview] {Failure}; callback and Original owner retained until process exit without retry.", PhysicalCloseFailure ?? "Unknown native close"); } catch { }
        }
        return complete;
    }
}
