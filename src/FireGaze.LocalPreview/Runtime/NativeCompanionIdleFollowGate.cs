using System.Numerics;

namespace FireGaze.LocalPreview.Runtime;

internal readonly record struct NativeIdleFollowKey(Guid Instance, long SceneEpoch, ulong SourceId,
    uint SourceGeneration, uint Territory, long LeaseId, long LeaseGeneration, long BindingRevision,
    nint Child, uint ChildGeneration, nint Host, uint HostGeneration);

internal readonly record struct NativeIdleFollowSample(NativeIdleFollowKey Key, uint Frame,
    byte Spawn, byte State, byte Special, bool Ready, bool RawIdentityCurrent, bool SourceAndSceneCurrent,
    nint Peer1, nint Peer2, ulong RenderFlags, uint BaseId, bool ChildAndHostPositionDirect,
    bool NullDrawPropagation, float Threshold, Vector3 ChildPosition, Vector3 HostPosition, nint GenuineCompanion = 0,
    byte? SpeciesBehavior = null, byte? SpeciesSpecial = null);

// Detached copies from the same capture as Sample; diagnostic data never grants admission.
internal sealed record NativeIdleFollowRawInputs(byte? SpeciesBehavior, byte? SpeciesSpecial,
    byte SpawnState, byte BehaviorState, byte BehaviorSpecial, bool Owner588PointerPresent,
    int? Owner588FirstInt, float Threshold, Vector3 ChildPosition, Vector3 HostPosition)
{
    public bool Predicate180BC90 => Owner588PointerPresent && Owner588FirstInt == 2;
    public float DistanceSquared => Vector3.DistanceSquared(ChildPosition, HostPosition);
}

internal interface INativeIdleFollowEnvironment
{
    bool IsFrameworkThread { get; }
    string CaptureFailure => "snapshot-unavailable";
    NativeIdleFollowRawInputs? RawInputs => null;
    bool TryCapture(in NativeIdleFollowKey key, out NativeIdleFollowSample sample);
}

internal interface INativeIdleFollowGateway
{
    bool IsFrameworkThread { get; }
    bool Verify();
    bool TryInvoke(nint child, byte nativeGate);
}

internal enum NativeIdleFollowStatus
{
    Disabled, Refused, AlreadyFollow, DuplicateFrame, EnteredFollow, NativeDidNotTransition,
    BindingChangedAfterCall, Exception
}

internal readonly record struct NativeIdleFollowResult(NativeIdleFollowStatus Status, uint? Frame,
    bool NativeInvoked, byte? BeforeState, byte? AfterState, string Reason = "", bool BoundaryAttempted = false,
    NativeIdleFollowRawInputs? RawInputs = null)
{
    public bool MovementVerified => false;
}

/// <summary>Constructed disabled. Never executes native UpdateFollow or sets child coordinates.</summary>
internal sealed class NativeCompanionIdleFollowGate(INativeIdleFollowGateway gateway)
{
    private readonly object authorityLock = new();
    private readonly Dictionary<nint, (NativeIdleFollowKey Key, uint Frame)> attempted = [];
    private int enabled;
    private long authorityVersion;
    private bool executing;
    private (Guid Instance, long Epoch)? scene;
    public bool ProductionAdmission => false;
    public bool Enabled => Volatile.Read(ref enabled) != 0;
    internal int AttemptedCount => gateway.IsFrameworkThread && !executing ? attempted.Count : 0;
    public bool TryEnableDevelopment() => TryEnableQualified();
    public bool TryEnableQualified()
    {
        try
        {
            lock (authorityLock)
            {
                var version = authorityVersion;
                if (!gateway.IsFrameworkThread || executing || !gateway.Verify())
                {
                    RevokeNoLock();
                    return false;
                }
                if (version != authorityVersion)
                {
                    RevokeNoLock();
                    return false;
                }
                Volatile.Write(ref enabled, 1);
                return true;
            }
        }
        catch (Exception) { Disable(); return false; }
    }
    // Any thread may revoke admission; dictionary mutations remain Framework-only.
    /// <summary>
    /// Revokes authority without touching the Framework-owned attempt ledger.  The
    /// authority lock brackets the final check and native boundary below, so an
    /// asynchronous shutdown cannot interleave between that check and the call.
    /// </summary>
    public void Disable()
    {
        lock (authorityLock)
        {
            RevokeNoLock();
        }
    }
    private void RevokeNoLock()
    {
        authorityVersion++;
        Volatile.Write(ref enabled, 0);
    }
    public bool ForgetLease(in NativeIdleFollowKey key)
        => gateway.IsFrameworkThread && !executing && attempted.TryGetValue(key.Child, out var old)
            && old.Key == key && attempted.Remove(key.Child);
    public NativeIdleFollowResult Request(INativeIdleFollowEnvironment environment, in NativeIdleFollowKey key)
    {
        NativeIdleFollowResult Refuse(NativeIdleFollowStatus status, uint? frame = null, byte? before = null, string reason = "")
            => new(status, frame, false, before, null, reason);
        if (!Enabled) return Refuse(NativeIdleFollowStatus.Disabled);
        if (executing || environment is null || !environment.IsFrameworkThread || !gateway.IsFrameworkThread || !KeyValid(key))
            return Refuse(NativeIdleFollowStatus.Refused);
        var invoked = false; var boundaryAttempted = false;
        NativeIdleFollowSample before = default;
        try
        {
            executing = true;
            if (!environment.TryCapture(key, out before))
                return Refuse(NativeIdleFollowStatus.Refused, reason: environment.CaptureFailure);
            if (SafetyFailure(before, key) is { Length: > 0 } failure)
                return Refuse(NativeIdleFollowStatus.Refused, before.Frame, before.State, failure);
            if (before.State == 1) return Refuse(NativeIdleFollowStatus.AlreadyFollow, before.Frame, before.State);
            if (before.State is not (0 or 5)) return Refuse(NativeIdleFollowStatus.Refused, before.Frame, before.State);
            var requestedScene = (key.Instance, key.SceneEpoch);
            if (scene != requestedScene) { attempted.Clear(); scene = requestedScene; }
            if (attempted.TryGetValue(key.Child, out var old) && old.Key.Instance == key.Instance
                && old.Key.SceneEpoch == key.SceneEpoch && old.Key.ChildGeneration == key.ChildGeneration && old.Frame == before.Frame)
                return Refuse(NativeIdleFollowStatus.DuplicateFrame, before.Frame, before.State);
            // A current actor may rebind a replacement child within the same lease.
            // Its fresh physical proof retires only that lease's old proof entries.
            var currentKey = key;
            foreach (var oldChild in attempted.Where(x => x.Value.Key.Instance == currentKey.Instance
                && x.Value.Key.SceneEpoch == currentKey.SceneEpoch && x.Value.Key.LeaseId == currentKey.LeaseId
                && x.Value.Key.LeaseGeneration == currentKey.LeaseGeneration && x.Key != currentKey.Child).Select(x => x.Key).ToArray())
                attempted.Remove(oldChild);
            if (!attempted.ContainsKey(key.Child) && attempted.Count >= 256)
                return Refuse(NativeIdleFollowStatus.Refused, before.Frame, before.State, "request-capacity-awaits-retirement");
            if (!gateway.Verify()) return Refuse(NativeIdleFollowStatus.Refused, reason: "gateway-identity-or-code");
            // Re-read after gateway verification. Captures must independently bracket
            // native frame/scene/lease/manager/generation; a detached DTO is not authority.
            if (!environment.TryCapture(key, out var immediatelyBefore) || immediatelyBefore != before
                || SafetyFailure(immediatelyBefore, key).Length != 0 || !Enabled)
                return Refuse(NativeIdleFollowStatus.Refused, before.Frame, before.State, "before-native:" + environment.CaptureFailure);
            var rawInputs = environment.RawInputs;
            attempted[key.Child] = (key, before.Frame);
            // Disable may be requested by a lifecycle callback on another thread.
            // Keep the final authority check and the unmanaged boundary in one
            // short critical section; no managed map is mutated here.
            lock (authorityLock)
            {
                if (!Enabled)
                    return Refuse(NativeIdleFollowStatus.Refused, before.Frame, before.State, "authority-revoked");
                boundaryAttempted = true;
                invoked = gateway.TryInvoke(key.Child, 0);
            }
            if (!invoked) return new(NativeIdleFollowStatus.Refused, before.Frame, false,
                before.State, null, "gateway-final-admission", true);
            if (!environment.TryCapture(key, out var after) || !SameLiveIdentity(after, key)
                || after.Frame != before.Frame || after.Spawn != 3 || after.Special != 0
                || SafetyFailure(after, key).Length != 0 || after.GenuineCompanion != before.GenuineCompanion
                || (before.State == 5 && (after.SpeciesBehavior != before.SpeciesBehavior
                    || after.SpeciesSpecial != before.SpeciesSpecial))
                || after.ChildPosition != before.ChildPosition || after.HostPosition != before.HostPosition)
                return new(NativeIdleFollowStatus.BindingChangedAfterCall, before.Frame, true, before.State, null, "after-native-binding", true);
            if (after.State == 1)
                return new(NativeIdleFollowStatus.EnteredFollow, before.Frame, true, before.State, after.State,
                    "", true, rawInputs);

            // A native idle gate is deliberately not a forced transition.  Keep the
            // captured raw inputs attached to the result so a no-op can be explained
            // without guessing at ownership or writing the behavior state. The CN
            // client compares distance before considering its owner predicate.
            var reason = NoTransitionReason(rawInputs);
            return new(NativeIdleFollowStatus.NativeDidNotTransition, before.Frame, true,
                before.State, after.State, reason, true, rawInputs);
        }
        catch (Exception)
        {
            Disable();
            return new(NativeIdleFollowStatus.Exception, boundaryAttempted ? before.Frame : null,
                invoked, boundaryAttempted ? before.State : null, null,
                boundaryAttempted ? "boundary-exception-outcome-unknown" : "before-boundary-exception", boundaryAttempted);
        }
        finally { executing = false; }
    }

    private static bool KeyValid(in NativeIdleFollowKey key) => key.Instance != Guid.Empty
        && key.SceneEpoch > 0 && key.SourceId != 0 && key.SourceGeneration > 0 && key.Territory > 0
        && key.LeaseId > 0 && key.LeaseGeneration > 0 && key.BindingRevision > 0
        && key.Child > 0 && key.Host > 0 && key.Child != key.Host
        && key.ChildGeneration > 0 && key.HostGeneration > 0;

    private static bool SameLiveIdentity(in NativeIdleFollowSample sample, in NativeIdleFollowKey key)
        => sample.Key == key && sample.Ready && sample.RawIdentityCurrent && sample.SourceAndSceneCurrent;

    private static string SafetyFailure(in NativeIdleFollowSample s, in NativeIdleFollowKey key)
    {
        if (!SameLiveIdentity(s, key)) return "binding-source-scene";
        if (s.Spawn != 3) return "spawn-not-three";
        if (s.Special != 0) return "special-not-zero";
        if (s.State == 5 && (s.SpeciesBehavior is not (1 or 2) || s.SpeciesSpecial != 0))
            return "state-five-species-unqualified";
        if (s.Peer1 != 0 || s.Peer2 != 0) return "peer-not-null";
        if (s.RenderFlags != 0) return "child-render-flags";
        if (s.BaseId is 0 or 0xA4) return "base-id-unqualified";
        if (!s.ChildAndHostPositionDirect) return "position-getter-unqualified";
        if (!s.NullDrawPropagation) return "draw-propagation-unqualified";
        if (!float.IsFinite(s.Threshold) || s.Threshold < 0) return "threshold-nonfinite-or-negative";
        if (!Finite(s.ChildPosition) || !Finite(s.HostPosition)
            || !float.IsFinite(Vector3.DistanceSquared(s.ChildPosition, s.HostPosition))) return "position-nonfinite-or-overflow";
        return "";
    }

    private static string NoTransitionReason(NativeIdleFollowRawInputs? raw)
    {
        if (raw is null) return "native-state-unchanged";
        var distanceSquared = raw.DistanceSquared;
        var threshold = raw.Threshold;
        // Match the audited sqrtss + comiss path. Squaring a rounded float
        // threshold changes the answer at some native boundaries (for example sqrt(2)).
        if (float.IsFinite(distanceSquared) && float.IsFinite(threshold) && threshold >= 0
            && MathF.Sqrt(distanceSquared) <= threshold)
            return "native-distance-threshold";
        if (raw.Predicate180BC90) return "native-owner-substate";
        return "native-state-unchanged";
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
