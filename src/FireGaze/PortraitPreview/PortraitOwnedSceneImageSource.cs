namespace FireGaze.PortraitPreview;

/// <summary>Detached identity for an owned slot texture; no pointer or GPU object is retained.</summary>
internal readonly record struct PortraitOwnedSceneImageDescriptor(PortraitOwnedSceneImageOwner Owner,
    long ResourceGeneration, int Width, int Height, float U0, float V0, float U1, float V1)
{
    internal bool IsValid => Owner is { IsValid: true } && ResourceGeneration > 0
        && Width > 0 && Height > 0 && (long)Width * Height <= 16_384 * 16_384
        && float.IsFinite(U0) && float.IsFinite(V0) && float.IsFinite(U1) && float.IsFinite(V1)
        && U0 >= 0 && V0 >= 0 && U0 < U1 && V0 < V1 && U1 <= 1 && V1 <= 1;
}

/// <summary>
/// Owns the asynchronous copy boundary for an owned scene. Native descriptor/SRV acquisition
/// must happen inside the supplied runtime's synchronous ready-view callback. Only the copied
/// disposable texture crosses the callback; this class never stores a native pointer.
/// </summary>
internal sealed class PortraitOwnedSceneImageSource<T> : IDisposable where T : class, IDisposable
{
    private readonly PortraitOwnedSceneTextureLifetime<T> lifetime = new();
    private PortraitOwnedSceneImageDescriptor? descriptor;
    private PortraitOwnedSceneController? controller;
    // This is the lifetime's local admission generation. The descriptor's resource generation
    // is an opaque native identity and need not be contiguous.
    private long copyGeneration = 1;
    private bool disposed;

    internal PortraitOwnedSceneImageDescriptor? Descriptor => descriptor;
    internal int InFlight => lifetime.InFlight;
    internal bool HasCurrentFrame => !disposed && lifetime.HasCurrentFrame;
    internal int FrameCount => lifetime.FrameCount;
    internal int Borrowers => lifetime.Borrowers;
    internal bool IsPhysicallyClean => lifetime.CleanupFailures == 0;
    internal event Action? FramePublished { add => lifetime.FramePublished += value; remove => lifetime.FramePublished -= value; }

    internal bool TrySetDescriptor(PortraitOwnedSceneController owner, PortraitOwnedSceneImageDescriptor value)
    {
        if (disposed || owner is null || !value.IsValid || !owner.IsImageOwnerCurrent(value.Owner)) return false;
        if (ReferenceEquals(controller, owner) && descriptor == value) return true;
        if (descriptor is not null) copyGeneration = lifetime.InvalidateSource();
        if (!ReferenceEquals(controller, owner))
        {
            if (controller is not null) controller.ImageOwnerChanged -= InvalidateOwner;
            controller = owner;
            controller.ImageOwnerChanged += InvalidateOwner;
        }
        descriptor = value;
        return true;
    }

    internal bool TryCopy(PortraitOwnedSceneController owner, IPortraitOwnedSceneLease lease,
        PortraitOwnedSceneImageDescriptor expected, Func<nint, Task<T>> copy)
    {
        if (disposed || owner is null || !ReferenceEquals(controller, owner) || lease is null || copy is null || descriptor != expected
            || !expected.IsValid || !owner.IsImageOwnerCurrent(expected.Owner) || !lease.IsCurrent
            || lease.Source != expected.Owner.Source || lease.Slot != expected.Owner.Slot || lease.Epoch != expected.Owner.LeaseEpoch) return false;
        var started = false;
        var used = owner.TryUseReady(expected.Owner, lease, view =>
        {
            // The runtime callback is the only point where an implementation may resolve its
            // owned slot descriptor and retain a provider wrapper. The async task receives only
            // that detached wrapper through the caller's copy function.
            started = lifetime.TryCopy(copyGeneration, () => copy(view));
        });
        return used && started;
    }

    private void InvalidateOwner() => copyGeneration = lifetime.InvalidateSource();

    // Called exclusively by the RT adapter while already inside TryUseReady. The factory
    // target contains only retained provider inputs; a view/Texture*/manager must never be
    // captured by the task or its factory. Reentering TryUseReady here would hit runtime busy.
    internal bool TryCopyWithinReadyView(PortraitOwnedSceneController owner, IPortraitOwnedSceneLease lease,
        PortraitOwnedSceneImageDescriptor value, Func<Task<T>> managedFactory,
        PortraitOwnedSceneFirstFrameIdentity? receipt = null)
        => lease is not null && lease.IsCurrent && lease.Source == value.Owner.Source
            && lease.Slot == value.Owner.Slot && lease.Epoch == value.Owner.LeaseEpoch
            && TrySetDescriptor(owner, value) && lifetime.TryCopy(copyGeneration, managedFactory, receipt);

    internal void InvalidateNativeSource() => copyGeneration = lifetime.InvalidateSource();

    internal PortraitOwnedSceneTextureLifetime<T>.Borrow? TryBorrow(bool allowPreviousGeneration = false)
        => disposed ? null : lifetime.TryBorrow(allowPreviousGeneration);

    internal Task DrainPhysicalAsync() => lifetime.DrainPhysicalAsync();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (controller is not null) controller.ImageOwnerChanged -= InvalidateOwner;
        controller = null; descriptor = null; lifetime.Dispose();
    }
}
