using System.Numerics;

namespace FireGaze.PortraitPreview;

/// <summary>
/// A deliberately small production wiring boundary for the future owned scene. The broker is
/// the only component allowed to issue a native-phase lease. This controller never creates a
/// view from Framework/ImGui code and never treats a census row as a reservation.
/// </summary>
internal interface IPortraitOwnedSceneControllerBroker
{
    bool IsFrameworkThread { get; }
    bool ProductionAdmission { get; }
    bool TryRequest(PortraitIdentity source, uint slot, Action<IPortraitOwnedSceneLease> onAdvance,
        Func<IPortraitOwnedSceneLease, bool> onRetire);
    void Cancel(string reason);
}

internal enum PortraitOwnedSceneControllerState { Off, Requested, Loading, Ready, Retiring, Faulted }

/// <summary>One controller generation bound to the broker's exact source-phase lease.</summary>
internal sealed record PortraitOwnedSceneImageOwner(Guid Session, long Revision, PortraitIdentity Source, uint Slot, long LeaseEpoch)
{
    internal bool IsValid => Session != Guid.Empty && Revision > 0 && Source.IsValid && Source.Generation > 0
        && Slot < 8 && LeaseEpoch > 0;
}

internal sealed class PortraitOwnedSceneController : IDisposable
{
    private readonly IPortraitOwnedSceneControllerBroker broker;
    private readonly Func<IPortraitOwnedSceneRuntime> createRuntime;
    private IPortraitOwnedSceneRuntime? runtime;
    private PortraitOwnedCharaViewSnapshot? source;
    private uint slot;
    private long generation;
    private long boundLeaseEpoch;
    private PortraitOwnedSceneImageOwner? imageOwner;
    private bool cancellationSent;
    private bool runtimeMayOwn, requestCallbackEntered;
    private bool disposed;
    private bool disposeRequested;

    internal PortraitOwnedSceneController(IPortraitOwnedSceneControllerBroker broker,
        Func<IPortraitOwnedSceneRuntime> createRuntime)
    {
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.createRuntime = createRuntime ?? throw new ArgumentNullException(nameof(createRuntime));
    }

    internal PortraitOwnedSceneControllerState State { get; private set; } = PortraitOwnedSceneControllerState.Off;
    internal PortraitOwnedCharaViewSnapshot? Source => source;
    internal uint Slot => slot;
    internal string? LastError { get; private set; }
    internal PortraitOwnedSceneImageOwner? ImageOwner => Volatile.Read(ref imageOwner);
    internal event Action? ImageOwnerChanged;

    internal bool IsImageOwnerCurrent(PortraitOwnedSceneImageOwner owner)
        => owner is { IsValid: true } && ImageOwner == owner
            && State is PortraitOwnedSceneControllerState.Loading or PortraitOwnedSceneControllerState.Ready;

    internal bool TryStart(PortraitOwnedCharaViewSnapshot snapshot, uint requestedSlot)
    {
        if (disposed || disposeRequested || State != PortraitOwnedSceneControllerState.Off || snapshot is null
            || !snapshot.Source.IsValid || snapshot.Source.Generation == 0 || requestedSlot >= 8
            || !broker.IsFrameworkThread || !broker.ProductionAdmission) return false;
        IPortraitOwnedSceneRuntime candidate;
        try { candidate = createRuntime() ?? throw new InvalidOperationException("Owned scene runtime factory returned null."); }
        catch (Exception error) { LastError = error.Message; return false; }
        var token = checked(++generation);
        source = snapshot; slot = requestedSlot; runtime = candidate; cancellationSent = false;
        runtimeMayOwn = requestCallbackEntered = false; boundLeaseEpoch = 0; LastError = null;
        State = PortraitOwnedSceneControllerState.Requested;
        try
        {
            if (broker.TryRequest(snapshot.Source, requestedSlot,
                lease => Advance(token, lease), lease => Retire(token, lease))) return true;
        }
        catch (Exception error) { LastError = "Owned scene request failed: " + error.Message; }
        // A broker may synchronously enter a source callback before its request returns.
        // Even a false/throwing request cannot discard the runtime owner after that point.
        if (runtime is not null && (requestCallbackEntered || runtimeMayOwn))
        {
            LastError ??= "Owned scene broker rejected a request after entering its callback.";
            if (State != PortraitOwnedSceneControllerState.Faulted) State = PortraitOwnedSceneControllerState.Retiring;
            SetImageOwner(null);
            CancelOnce(LastError);
            return false;
        }
        runtime = null; source = null; slot = 0; State = PortraitOwnedSceneControllerState.Off;
        return false;
    }

    /// <summary>Stops admission and waits for the broker's real native retirement callback.</summary>
    internal void Stop(string reason = "Owned scene stopped.")
    {
        if (disposed || disposeRequested || State is PortraitOwnedSceneControllerState.Off or PortraitOwnedSceneControllerState.Faulted
            or PortraitOwnedSceneControllerState.Retiring) return;
        State = PortraitOwnedSceneControllerState.Retiring;
        SetImageOwner(null);
        CancelOnce(reason);
    }

    internal bool TryUseReady(IPortraitOwnedSceneLease lease, Action<nint> readView)
        => ImageOwner is { } owner && TryUseReady(owner, lease, readView);

    internal bool TryUseReady(PortraitOwnedSceneImageOwner owner, IPortraitOwnedSceneLease lease, Action<nint> readView)
    {
        if (disposed || disposeRequested || State != PortraitOwnedSceneControllerState.Ready || runtime is null
            || source is null || lease is null || lease.Source != source.Source || lease.Slot != slot
            || !IsImageOwnerCurrent(owner) || lease.Epoch != owner.LeaseEpoch || !lease.IsCurrent || readView is null) return false;
        return runtime.UseReadyView(lease, readView);
    }

    private void Advance(long token, IPortraitOwnedSceneLease lease)
    {
        if (token == generation) requestCallbackEntered = true;
        // A stale callback is ignored. It must not fault or alter a newer source/slot owner.
        if (token != generation || disposed || disposeRequested || State is PortraitOwnedSceneControllerState.Off
            or PortraitOwnedSceneControllerState.Retiring or PortraitOwnedSceneControllerState.Faulted
            || runtime is null || source is null || lease is null || lease.Source != source.Source || lease.Slot != slot
            || boundLeaseEpoch != 0 && lease.Epoch != boundLeaseEpoch
            ) return;
        if (!lease.IsCurrent || lease.Epoch <= 0) { Fail("Owned native scene callback lost its current lease."); return; }
        try
        {
            if (State == PortraitOwnedSceneControllerState.Requested)
            {
                boundLeaseEpoch = lease.Epoch;
                // TryCreate may allocate before returning false or throwing. Retain this
                // owner until its real native retirement callback confirms all cleanup.
                runtimeMayOwn = true;
                if (!runtime.TryCreate(source, lease)) { Fail("Owned native scene creation did not complete."); return; }
                State = PortraitOwnedSceneControllerState.Loading;
                SetImageOwner(new(Guid.NewGuid(), token, source.Source, slot, boundLeaseEpoch));
                return;
            }
            if (!runtime.Advance(lease, lease.NativeFrame)) { Fail("Owned native scene advance did not complete."); return; }
            if (runtime.IsReady) State = PortraitOwnedSceneControllerState.Ready;
        }
        catch (Exception error) { Fail("Owned native scene callback failed: " + error.Message); }
    }

    private bool Retire(long token, IPortraitOwnedSceneLease lease)
    {
        if (token != generation || disposed || runtime is null || source is null || lease is null
            || lease.Source != source.Source || lease.Slot != slot || !lease.IsCurrent
            || boundLeaseEpoch != 0 && lease.Epoch != boundLeaseEpoch) return false;
        State = PortraitOwnedSceneControllerState.Retiring;
        SetImageOwner(null);
        try
        {
            var retired = !runtimeMayOwn || runtime.TryRetire(lease);
            if (!retired) { Fail("Owned native scene retirement was not confirmed."); return false; }
            runtimeMayOwn = requestCallbackEntered = false;
            boundLeaseEpoch = 0;
            runtime = null; source = null; slot = 0; State = PortraitOwnedSceneControllerState.Off;
            if (disposeRequested) disposed = true;
            cancellationSent = false; LastError = null; return true;
        }
        catch (Exception error) { Fail("Owned native scene retirement failed: " + error.Message); return false; }
    }

    private void Fail(string reason)
    {
        LastError = reason; State = PortraitOwnedSceneControllerState.Faulted; SetImageOwner(null); CancelOnce(reason);
    }

    private void SetImageOwner(PortraitOwnedSceneImageOwner? value)
    {
        if (ImageOwner == value) return;
        Volatile.Write(ref imageOwner, value);
        // A managed image consumer must not interrupt native resource retirement.
        if (ImageOwnerChanged is not { } changed) return;
        foreach (Action subscriber in changed.GetInvocationList())
            try { subscriber(); } catch (Exception error) { LastError ??= error.Message; }
    }

    private void CancelOnce(string reason)
    {
        if (cancellationSent) return;
        cancellationSent = true;
        try { broker.Cancel(reason); } catch (Exception error) { LastError ??= error.Message; }
    }

    public void Dispose()
    {
        if (disposed || disposeRequested) return;
        disposeRequested = true;
        if (State == PortraitOwnedSceneControllerState.Off) { disposed = true; return; }
        if (State is not PortraitOwnedSceneControllerState.Off)
        {
            State = PortraitOwnedSceneControllerState.Retiring;
            SetImageOwner(null);
            CancelOnce("Owned scene controller disposed.");
        }
    }
}

/// <summary>Runtime seam used by the controller; the real implementation is PortraitOwnedCharaView.</summary>
internal interface IPortraitOwnedSceneRuntime
{
    bool IsReady { get; }
    bool TryCreate(PortraitOwnedCharaViewSnapshot source, IPortraitOwnedSceneLease lease);
    bool Advance(IPortraitOwnedSceneLease lease, uint nativeFrame);
    bool UseReadyView(IPortraitOwnedSceneLease lease, Action<nint> readView);
    bool TryRetire(IPortraitOwnedSceneLease lease);
}

internal sealed class PortraitOwnedSceneRuntimeAdapter(PortraitOwnedCharaView view) : IPortraitOwnedSceneRuntime
{
    public bool IsReady => view.IsReady;
    public bool TryCreate(PortraitOwnedCharaViewSnapshot source, IPortraitOwnedSceneLease lease) => view.TryCreate(source, lease);
    public bool Advance(IPortraitOwnedSceneLease lease, uint nativeFrame) => view.Advance(lease, nativeFrame);
    public bool UseReadyView(IPortraitOwnedSceneLease lease, Action<nint> readView) => view.UseReadyView(lease, readView);
    public bool TryRetire(IPortraitOwnedSceneLease lease) => view.TryRetire(lease);
}

/// <summary>Detached observer camera; it never writes the portrait's capture camera or native view.</summary>
internal readonly record struct PortraitOwnedSceneObserverCameraSnapshot(Vector3 Position, Vector3 Target,
    float Yaw, float Pitch, float Distance)
{
    internal bool IsValid => IsFinite(Position) && IsFinite(Target) && float.IsFinite(Yaw) && float.IsFinite(Pitch)
        && float.IsFinite(Distance) && Distance > 0 && Distance <= 100 && Pitch >= -MathF.PI / 2 && Pitch <= MathF.PI / 2;
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

internal sealed class PortraitOwnedSceneObserverCamera
{
    internal PortraitOwnedSceneObserverCameraSnapshot Snapshot { get; private set; }
    internal bool TrySet(Vector3 position, Vector3 target, float yaw, float pitch, float distance)
    {
        var candidate = new PortraitOwnedSceneObserverCameraSnapshot(position, target, yaw, pitch, distance);
        if (!candidate.IsValid || Vector3.DistanceSquared(position, target) <= .000001f) return false;
        Snapshot = candidate; return true;
    }
}
