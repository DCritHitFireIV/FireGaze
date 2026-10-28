namespace FireGaze.LocalPreview.Runtime;

internal interface INativePhysicalCloseState
{
    bool PhysicalCloseComplete { get; }
    string? PhysicalCloseFailure { get; }
}

// A constructor cannot return an owner whose native rollback is unknown. Carry
// it to the parent so host/provider-scope drain cannot mistake a failed new for
// an absent component. The original initialization failure remains the cause.
internal sealed class NativePhysicalCloseException(IDisposable owner, Exception initializationFailure)
    : Exception("Native initialization failed and physical rollback is incomplete.", initializationFailure)
{
    internal IDisposable Owner { get; } = owner;
}
