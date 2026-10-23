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
    // Explicit development owners can issue one bounded start capability.
    // Normal brokers inherit only the production gate; this never grants a lease.
    bool StartAdmission => ProductionAdmission;
    bool TryRequest(PortraitIdentity source, uint slot, Action<IPortraitOwnedSceneLease> onAdvance,
        Func<IPortraitOwnedSceneLease, bool> onRetire);
    // True certifies that no source callback ever entered and no native owner exists.
    // Older brokers cannot make this proof and retain the normal retirement path.
    bool TryCancelUnstartedRequest(string reason) => false;
    void Cancel(string reason);
}

internal interface IPortraitOwnedSceneSessionBroker : IPortraitOwnedSceneControllerBroker, IDisposable
{
    string? LastError => null;
    void Update();
    Task DrainPhysicalAsync();
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
    private bool hasAdvanced;
    private PortraitOwnedSceneObserverCameraSnapshot? pendingObserver;
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
            || snapshot.Version != PortraitOwnedCharaViewSnapshot.CurrentVersion || !snapshot.IsDetachedValid
            || !snapshot.Provenance.IsValid || snapshot.Provenance.Digest != snapshot.ContentDigest
            || snapshot.Source != snapshot.Provenance.Source || string.IsNullOrWhiteSpace(snapshot.ContentDigest)
            || !snapshot.Source.IsValid
            || snapshot.Source.Generation == 0 || requestedSlot >= 8
            || !broker.IsFrameworkThread || !broker.StartAdmission) return false;
        IPortraitOwnedSceneRuntime candidate;
        try { candidate = createRuntime() ?? throw new InvalidOperationException("Owned scene runtime factory returned null."); }
        catch (Exception error) { LastError = error.Message; return false; }
        var token = checked(++generation);
        source = snapshot; slot = requestedSlot; runtime = candidate; cancellationSent = false;
        runtimeMayOwn = requestCallbackEntered = hasAdvanced = false;
        pendingObserver = null; boundLeaseEpoch = 0; LastError = null;
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

    // Input is detached managed state. Only the next qualified native callback applies it.
    internal bool TrySetObserverCamera(PortraitOwnedSceneObserverCameraSnapshot camera)
    {
        if (disposed || disposeRequested || !broker.IsFrameworkThread || !camera.IsNativeSupported
            || source is null || runtime is null || State is not (PortraitOwnedSceneControllerState.Requested
                or PortraitOwnedSceneControllerState.Loading or PortraitOwnedSceneControllerState.Ready)) return false;
        pendingObserver = camera;
        if (State != PortraitOwnedSceneControllerState.Requested) State = PortraitOwnedSceneControllerState.Loading;
        return true;
    }

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
            // Initial preparation must finish before a native observer can be applied.
            // A queued camera invalidates the previous image until its subsequent Render.
            if (hasAdvanced && runtime.IsReady && pendingObserver is { } observer)
            {
                if (!runtime.TryApplyObserverCamera(lease, observer)) { Fail("Owned observer camera was not applied."); return; }
                pendingObserver = null;
            }
            if (!runtime.Advance(lease, lease.NativeFrame)) { Fail("Owned native scene advance did not complete."); return; }
            hasAdvanced = true;
            State = runtime.IsReady && pendingObserver is null
                ? PortraitOwnedSceneControllerState.Ready : PortraitOwnedSceneControllerState.Loading;
        }
        catch (Exception error) { Fail("Owned native scene callback failed: " + error.Message); }
    }

    private bool Retire(long token, IPortraitOwnedSceneLease lease)
    {
        if (token != generation || disposed || runtime is null || source is null || lease is null
            || lease.Source != source.Source || lease.Slot != slot || !lease.IsCurrent && !lease.NativeReleaseConfirmed
            || boundLeaseEpoch != 0 && lease.Epoch != boundLeaseEpoch) return false;
        State = PortraitOwnedSceneControllerState.Retiring;
        SetImageOwner(null);
        try
        {
            var retired = lease.NativeReleaseConfirmed || !runtimeMayOwn || runtime.TryRetire(lease);
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
    internal void RevokeNativeSource(string reason)
    {
        if (disposed || State == PortraitOwnedSceneControllerState.Off) return;
        // Native ownership is already uncertain. Revoke managed images immediately;
        // retain the runtime owner and let the broker preserve physical quarantine.
        Fail(reason);
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
        try
        {
            if (TryCompleteUnstartedCancellation(reason)) return;
            broker.Cancel(reason);
        }
        catch (Exception error) { LastError ??= error.Message; }
    }

    private bool TryCompleteUnstartedCancellation(string reason)
    {
        if (requestCallbackEntered || runtimeMayOwn || !broker.TryCancelUnstartedRequest(reason)
            || requestCallbackEntered || runtimeMayOwn) return false;
        runtime = null; source = null; slot = 0; boundLeaseEpoch = 0;
        SetImageOwner(null); State = PortraitOwnedSceneControllerState.Off;
        cancellationSent = false; LastError = null;
        if (disposeRequested) disposed = true;
        return true;
    }
    internal void Update()
    {
        if (disposed || State is not (PortraitOwnedSceneControllerState.Retiring or PortraitOwnedSceneControllerState.Faulted)
            || requestCallbackEntered || runtimeMayOwn) return;
        // A foreign Original may have blocked the initial no-owner cancellation proof.
        // Retry that detached proof on Framework without waiting for another Render.
        try { TryCompleteUnstartedCancellation("Unused owned request is closing."); }
        catch (Exception error) { LastError ??= error.Message; }
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
    bool TryApplyObserverCamera(IPortraitOwnedSceneLease lease, PortraitOwnedSceneObserverCameraSnapshot camera) => false;
    bool UseReadyView(IPortraitOwnedSceneLease lease, Action<nint> readView);
    bool TryRetire(IPortraitOwnedSceneLease lease);
}

internal sealed class PortraitOwnedSceneRuntimeAdapter(PortraitOwnedCharaView view) : IPortraitOwnedSceneRuntime
{
    public bool IsReady => view.IsReady;
    public bool TryCreate(PortraitOwnedCharaViewSnapshot source, IPortraitOwnedSceneLease lease) => view.TryCreate(source, lease);
    public bool Advance(IPortraitOwnedSceneLease lease, uint nativeFrame) => view.Advance(lease, nativeFrame);
    public bool TryApplyObserverCamera(IPortraitOwnedSceneLease lease, PortraitOwnedSceneObserverCameraSnapshot camera)
        => view.TryApplyObserverCamera(lease, camera);
    public bool UseReadyView(IPortraitOwnedSceneLease lease, Action<nint> readView) => view.UseReadyView(lease, readView);
    public bool TryRetire(IPortraitOwnedSceneLease lease) => view.TryRetire(lease);
}

/// <summary>Detached observer camera; it never writes the portrait's capture camera or native view.</summary>
internal readonly record struct PortraitOwnedSceneObserverCameraSnapshot(Vector3 Position, Vector3 Target,
    float Yaw, float Pitch, float Distance)
{
    internal static bool TryFromSource(Vector3 position, Vector3 target,
        out PortraitOwnedSceneObserverCameraSnapshot camera)
    {
        camera = default;
        var offset = position-target;
        var distance = offset.Length();
        if (!IsFinite(position) || !IsFinite(target) || !float.IsFinite(distance) || distance <= 0) return false;
        var candidate = new PortraitOwnedSceneObserverCameraSnapshot(position,target,MathF.Atan2(offset.X,offset.Z),
            MathF.Asin(Math.Clamp(-offset.Y/distance,-1,1)),distance);
        if (!candidate.IsNativeSupported) return false;
        camera=candidate;return true;
    }
    internal bool IsValid => IsFinite(Position) && IsFinite(Target) && float.IsFinite(Yaw) && float.IsFinite(Pitch)
        && float.IsFinite(Distance) && Distance > 0 && Distance <= 100 && Pitch >= -MathF.PI / 2 && Pitch <= MathF.PI / 2;
    internal bool IsNativeSupported
    {
        get
        {
            if (!IsValid || Distance is < .5f or > 2f || Target.X is < -1.6f or > 1.6f
                || Target.Y is < -1f or > .5f || Target.Z is < -.5f or > .5f
                || Pitch < -89f * MathF.PI / 180 || Pitch > 30f * MathF.PI / 180) return false;
            var offset = new Vector3(MathF.Sin(Yaw) * MathF.Cos(Pitch), -MathF.Sin(Pitch), MathF.Cos(Yaw) * MathF.Cos(Pitch)) * Distance;
            return Vector3.DistanceSquared(Position, Target + offset) <= .000001f;
        }
    }
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
