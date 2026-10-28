using System.Text.Json;
using FireGaze.LocalPreview.Core;
using FireGaze.LocalPreview.Runtime;

namespace FireGaze.LocalPreview;

internal sealed unsafe partial class MultiCompanionRuntime
{
    private const int IdleFollowRequestsPerNativeFrame = 2;
    private readonly Guid idleFollowInstance = Guid.NewGuid();
    private ICompanionFollowSnapshotReader? idleFollowReader;
    private NativeCompanionIdleFollowGateway? idleFollowGateway;
    private NativeCompanionIdleFollowGate? idleFollowGate;
    private nint idleFollowVirtualTable;
    private uint? idleFollowCompanion;
    private long idleFollowEpoch = 1;
    private long idleFollowPublication;
    private uint? idleFollowBudgetFrame;
    private int idleFollowRequestsThisFrame;
    private int idleFollowCursor;
    private bool idleFollowAutomaticPaused;
    private bool idleFollowDevelopmentControl;
    private bool idleFollowColdQualificationAttempted;
    private bool idleFollowQualificationRejected;
    private int idleFollowColdQualificationCount;
    private string idleFollowReason = "awaiting-active-companion";
    private readonly Dictionary<long, NativeIdleFollowKey> idleFollowProofs = [];
    private readonly Queue<IdleFollowObservation> idleFollowSamples = [];

    private sealed record IdleFollowObservation(long LeaseID, long Generation, uint CompanionID,
        string Status, uint? Frame, bool NativeInvoked, bool BoundaryAttempted,
        byte? BeforeState, byte? AfterState, string Reason, NativeIdleFollowRawInputs? RawInputs);

    internal JsonElement ReadIdleFollowJSON() => JsonSerializer.SerializeToElement(new
    {
        Enabled = idleFollowGate?.Enabled == true,
        Control = idleFollowAutomaticPaused ? "DevelopmentPaused"
            : idleFollowDevelopmentControl ? "Development" : "Automatic",
        OrdinarySpeciesOnly = true,
        ColdQualificationCount = idleFollowColdQualificationCount,
        Epoch = idleFollowEpoch,
        Publication = idleFollowPublication,
        ProductionAdmission = false,
        MovementVerified = false,
        MaximumRequestsPerNativeFrame = IdleFollowRequestsPerNativeFrame,
        LastReason = idleFollowReason,
        Samples = idleFollowSamples.ToArray(),
    }, new JsonSerializerOptions { IncludeFields = true });

    /// <summary>Developer comparison override. Off also pauses the ordinary automatic consumer.</summary>
    internal bool SetDevelopmentIdleFollow(bool enabled, uint? companionID, out string reason)
    {
        reason = "framework-or-current-companion-unavailable";
        if (!framework.IsInFrameworkUpdateThread || disposed) return false;
        if (!enabled)
        {
            RevokeDevelopmentIdleFollow("development-disabled");
            idleFollowAutomaticPaused = true;
            reason = "";
            return true;
        }
        if (!trialPolicy.Enabled || !CanRun(out var source) || !BindingCurrent(source)
            || NativeMountActive(source) || companionID == 0
            || !session.Snapshot.Leases.Any(x => x.State == CompanionDisplayState.Active
                && (!companionID.HasValue || x.CompanionId == companionID)))
        {
            RevokeDevelopmentIdleFollow(reason);
            idleFollowAutomaticPaused = true;
            return false;
        }
        RevokeDevelopmentIdleFollow("development-rearm");
        if (!TryEnableIdleFollow(retryRejected: true, out reason))
        {
            idleFollowAutomaticPaused = true;
            return false;
        }
        idleFollowAutomaticPaused = false;
        idleFollowDevelopmentControl = true;
        idleFollowCompanion = companionID;
        idleFollowCursor = 0;
        idleFollowReason = "development-armed";
        return true;
    }

    private bool TryEnableIdleFollow(bool retryRejected, out string reason)
    {
        reason = idleFollowReason;
        if (idleFollowQualificationRejected && !retryRejected) return false;
        try
        {
            // Hash this process and its SDK once for this runtime. Normal frame
            // updates and lifecycle rearming reuse that image; the final gateway
            // still compares the loaded code before every native boundary.
            idleFollowReader ??= new NativeCompanionFollowReader(framework);
            if (idleFollowGate is null)
            {
                if (idleFollowColdQualificationAttempted) return false;
                idleFollowColdQualificationAttempted = true;
                idleFollowColdQualificationCount++;
                idleFollowGateway = new(new NativeCompanionIdleGateCodeImage(idleFollowReader));
                idleFollowVirtualTable = idleFollowGateway.CompanionVirtualTable;
                idleFollowGate = new(idleFollowGateway);
            }
            if (!idleFollowGate.TryEnableQualified())
            {
                idleFollowQualificationRejected = true;
                idleFollowReason = reason = idleFollowGateway?.Failure ?? "gateway-qualification-refused";
                return false;
            }
            idleFollowQualificationRejected = false;
            reason = "";
            return true;
        }
        catch (Exception failure)
        {
            idleFollowQualificationRejected = true;
            RevokeDevelopmentIdleFollow("idle-follow-qualification-exception");
            log.Debug(failure, "[LocalPreview] Idle-follow qualification failed");
            reason = idleFollowReason;
            return false;
        }
    }

    private void RevokeDevelopmentIdleFollow(string reason)
    {
        idleFollowGate?.Disable();
        // Revocation does not retire a physical child. Preserve its consumed
        // frame proof across off/on so rearming cannot issue a second gate call
        // in the same native frame. Only physical retirement forgets that proof.
        idleFollowSamples.Clear();
        idleFollowCompanion = null;
        idleFollowDevelopmentControl = false;
        idleFollowReason = reason;
        idleFollowEpoch++;
        idleFollowPublication++;
    }

    private void ForgetDevelopmentIdleFollow(CompanionDisplayLease lease)
    {
        if (!idleFollowProofs.TryGetValue(lease.LeaseId, out var key)
            || key.LeaseGeneration != lease.Generation) return;
        idleFollowGate?.ForgetLease(key);
        idleFollowProofs.Remove(lease.LeaseId);
    }

    private bool IdleFollowScopeCurrent(CompanionExecutionKey key, long epoch, uint expectedTerritory,
        nint expectedSource, NativeCompanionDisplayActor actor, bool allowDisabledObservation = false)
    {
        if ((!allowDisabledObservation && idleFollowGate?.Enabled != true) || idleFollowEpoch != epoch || !trialPolicy.Enabled
            || !CanRun(out var source) || NativeMountActive(source) || !BindingCurrent(source)
            || ownerAddress != expectedSource || territory != expectedTerritory || client.TerritoryType != expectedTerritory
            || key != new CompanionExecutionKey(sceneEpoch, ownerID, ownerGeneration, key.LeaseID, key.LeaseGeneration)
            || !actors.TryGetValue(key.LeaseID, out var entry) || entry.Generation != key.LeaseGeneration
            || !ReferenceEquals(entry.Actor, actor) || actor.IsDisposed || !actor.IsReady) return false;
        return session.Snapshot.Leases.Any(x => x.LeaseId == key.LeaseID && x.Generation == key.LeaseGeneration
            && x.State == CompanionDisplayState.Active && (!idleFollowCompanion.HasValue || x.CompanionId == idleFollowCompanion));
    }

    private void UpdateDevelopmentIdleFollow()
    {
        try { UpdateDevelopmentIdleFollowCore(); }
        catch (Exception failure)
        {
            idleFollowQualificationRejected = true;
            RevokeDevelopmentIdleFollow("idle-follow-update-exception");
            log.Debug(failure, "[LocalPreview] Idle-follow update stopped");
        }
    }

    private void UpdateDevelopmentIdleFollowCore()
    {
        if (idleFollowAutomaticPaused) return;
        if (idleFollowGate?.Enabled != true)
        {
            if (!trialPolicy.Enabled || !CanRun(out var source) || !BindingCurrent(source)
                || NativeMountActive(source)
                || !session.Snapshot.Leases.Any(x => x.State == CompanionDisplayState.Active)) return;
            if (!TryEnableIdleFollow(retryRejected: false, out _)) return;
            idleFollowReason = "automatic-armed";
        }
        if (idleFollowGate is not { Enabled: true } gate || idleFollowReader is not { } reader) return;
        var epoch = idleFollowEpoch;
        if (!reader.IsFrameworkThread || !reader.TryReadNativeFrame(out var nativeFrame))
        {
            RevokeDevelopmentIdleFollow("native-frame-unavailable");
            return;
        }
        if (idleFollowBudgetFrame != nativeFrame)
        {
            idleFollowBudgetFrame = nativeFrame;
            idleFollowRequestsThisFrame = 0;
        }
        var active = session.Snapshot.Leases.Where(x => x.State == CompanionDisplayState.Active
            && (!idleFollowCompanion.HasValue || x.CompanionId == idleFollowCompanion)).ToArray();
        if (active.Length == 0) { RevokeDevelopmentIdleFollow("no-active-companion"); return; }
        var remaining = Math.Min(active.Length, IdleFollowRequestsPerNativeFrame - idleFollowRequestsThisFrame);
        for (var index = 0; index < remaining && gate.Enabled && epoch == idleFollowEpoch; index++)
        {
            var selectedIndex = idleFollowCursor % active.Length;
            idleFollowCursor = (selectedIndex + 1) % active.Length;
            var lease = active[selectedIndex];
            var key = new CompanionExecutionKey(sceneEpoch, ownerID, ownerGeneration, lease.LeaseId, lease.Generation);
            if (!actors.TryGetValue(lease.LeaseId, out var entry) || entry.Generation != lease.Generation) continue;
            var actor = entry.Actor;
            var expectedTerritory = territory;
            var expectedSource = ownerAddress;
            bool ScopeCurrent() => IdleFollowScopeCurrent(key, epoch, expectedTerritory, expectedSource, actor)
                && reader.TryReadNativeFrame(out var currentFrame) && currentFrame == nativeFrame;
            if (!ScopeCurrent()) continue;
            idleFollowRequestsThisFrame++;
            if (!actor.TryReadFollowBinding(reader, key, expectedTerritory, epoch, out var binding, out _)) continue;
            var proof = new NativeIdleFollowKey(idleFollowInstance, key.SceneEpoch, key.SourceID, key.SourceGeneration,
                expectedTerritory, key.LeaseID, key.LeaseGeneration, binding.BindingRevision,
                binding.Child, binding.ChildGeneration, binding.Host, binding.HostGeneration);
            var result = actor.TryRequestIdleFollow(gate, reader, key, idleFollowInstance, expectedTerritory,
                epoch, idleFollowVirtualTable, ScopeCurrent, ordinarySpeciesOnly: true);
            var boundaryUncertain = result.Status is NativeIdleFollowStatus.Exception or NativeIdleFollowStatus.BindingChangedAfterCall
                || result.Reason == "gateway-identity-or-code";
            if (boundaryUncertain && epoch == idleFollowEpoch)
            {
                idleFollowQualificationRejected = true;
                gate.Disable();
            }
            // A gate exception revokes its own permission. That must stop the
            // next automatic frame without discarding a still-current failure
            // receipt. A recalled/replaced epoch still cannot publish anything.
            if (!IdleFollowScopeCurrent(key, epoch, expectedTerritory, expectedSource, actor, boundaryUncertain)
                || !reader.TryReadNativeFrame(out var observedFrame) || observedFrame != nativeFrame) return;
            if (result.BoundaryAttempted) idleFollowProofs[lease.LeaseId] = proof;
            idleFollowReason = result.Reason;
            idleFollowSamples.Enqueue(new(lease.LeaseId, lease.Generation, lease.CompanionId, result.Status.ToString(),
                result.Frame, result.NativeInvoked, result.BoundaryAttempted, result.BeforeState, result.AfterState,
                result.Reason, result.RawInputs));
            while (idleFollowSamples.Count > 64) idleFollowSamples.Dequeue();
            idleFollowPublication++;
            if (boundaryUncertain) return;
        }
    }
}
