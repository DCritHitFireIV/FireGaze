using FireGaze.LocalPreview.Core;
using FireGaze.LocalPreview.Runtime;
using System.Diagnostics;

namespace FireGaze.LocalPreview;

internal sealed unsafe partial class MultiCompanionRuntime
{
    // Observation only. No requests are queued/taken and no native ABI is invoked here.
    private const int MaximumFollowDiagnosticBatch = 8;
    private CompanionExecutionKey[]? followDiagnosticBatchKeys;

    internal bool SetFollowDiagnosticsForLeases(IReadOnlyList<CompanionExecutionKey> keys, out string reason)
    {
        reason = "Exact diagnostics require 1..8 distinct current lease keys on the Framework thread.";
        if (!framework.IsInFrameworkUpdateThread || disposed || keys is null) return false;
        var selectionCount = keys.Count;
        if (selectionCount is < 1 or > MaximumFollowDiagnosticBatch || !NativeCompanionFollowReader.LayoutValid()) return false;
        var copy = new CompanionExecutionKey[selectionCount];
        try { for (var i = 0; i < copy.Length; i++) copy[i] = keys[i]; }
        catch (Exception) { reason = "Exact diagnostic selection changed while being copied."; return false; }
        var context = new CompanionExecutionContext(sceneEpoch, ownerID, ownerGeneration);
        var leases = session.Snapshot.Leases;
        if (copy.Distinct().Count() != copy.Length || copy.Any(key => key.Context != context
            || key.SceneEpoch <= 0 || key.SourceID == 0 || key.SourceGeneration == 0 || key.LeaseID <= 0 || key.LeaseGeneration <= 0
            || !leases.Any(lease => lease.LeaseId == key.LeaseID && lease.Generation == key.LeaseGeneration
                && lease.State is CompanionDisplayState.Active or CompanionDisplayState.Pending or CompanionDisplayState.NativeHidden))) return false;
        copy = copy.OrderBy(key => key.LeaseID).ThenBy(key => key.LeaseGeneration).ToArray();
        if (FollowDiagnostics.Enabled && followDiagnosticBatchKeys is { } prior && prior.SequenceEqual(copy))
        { reason = ""; return true; }
        followDiagnosticReader ??= new NativeCompanionFollowReader(framework);
        followDiagnosticCompanion = null;
        followDiagnosticBatchKeys = copy;
        FollowDiagnostics = new(true, ++followDiagnosticEpoch, ++followDiagnosticPublication,
            Array.Empty<CompanionFollowDiagnosticSample>()) { SelectionKind = "exact-leases" };
        reason = "";
        return true;
    }

    private bool FollowBatchSelectsLease(long id, long generation)
        => followDiagnosticBatchKeys?.Any(key => key.LeaseID == id && key.LeaseGeneration == generation) == true;

    private bool FollowBatchSelectsCompanion(uint companionID)
        => followDiagnosticBatchKeys is not null && session.Snapshot.Leases.Any(lease => lease.CompanionId == companionID
            && FollowBatchSelectsLease(lease.LeaseId, lease.Generation));

    private bool FollowBatchContextCurrent(CompanionExecutionContext context, CompanionExecutionKey[] keys,
        uint selectedTerritory, nint selectedSource)
        => !disposed && allowed && client.IsLoggedIn && selectedSource != 0 && ownerAddress == selectedSource
            && selectedTerritory == territory && client.TerritoryType == selectedTerritory
            && !conditions[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
            && !conditions[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51]
            && context == new CompanionExecutionContext(sceneEpoch, ownerID, ownerGeneration)
            && ReferenceEquals(keys, followDiagnosticBatchKeys);

    private CompanionFollowHostCorrelation ReadHostCorrelation(CompanionExecutionKey key, TimeSpan observedAt,
        IReadOnlyList<CompanionFollowExecutionSnapshot> executions)
    {
        if (!followRequests.TryGetValue(key.LeaseID, out var request) || request.Key != key)
            return new(null, null, "unknown", "no-current-host-request");
        var execution = executions.FirstOrDefault(x => x.Request.Key == key);
        if (execution is null || execution.Request != request || !execution.RequestDispatched)
            return new(request, null, "unknown", "host-request-not-dispatched-or-sequence-mismatch");
        var receipt = execution.Receipt;
        if (receipt is null) return new(request, null, "unknown", "host-receipt-unavailable");
        if (receipt.Value.Key != key || receipt.Value.Sequence != request.Sequence || !receipt.Value.BindingCurrent
            || receipt.Value.ObservedAt > observedAt)
            return new(request, null, "invalid", "host-receipt-identity-sequence-or-time");
        // Association does not require Applied: a real refusal is useful diagnostic evidence.
        return new(request, receipt, "associated", null);
    }

    private void PublishFollowBatchDiagnostics(IReadOnlyList<CompanionBehaviorIntent> intents, TimeSpan plannedAt)
    {
        if (!FollowDiagnostics.Enabled || followDiagnosticReader is not { } reader || followDiagnosticBatchKeys is not { } keys) return;
        var epoch = followDiagnosticEpoch;
        var selectedTerritory = territory;
        var selectedSource = ownerAddress;
        var context = new CompanionExecutionContext(sceneEpoch, ownerID, ownerGeneration);
        var selectedLeases = session.Snapshot.Leases;
        bool ScopeCurrent() => FollowDiagnostics.Enabled && epoch == followDiagnosticEpoch
            && FollowBatchContextCurrent(context, keys, selectedTerritory, selectedSource);
        bool LeaseCurrent(CompanionDisplayLease lease) => session.Snapshot.Leases.Any(current =>
            current.LeaseId == lease.LeaseId && current.Generation == lease.Generation
            && current.CompanionId == lease.CompanionId && current.State == lease.State
            && current.State is CompanionDisplayState.Active or CompanionDisplayState.Pending or CompanionDisplayState.NativeHidden);
        if (!ScopeCurrent()) { InvalidateFollowDiagnostics(); return; }
        var leases = keys.Select(key => selectedLeases.FirstOrDefault(x => x.LeaseId == key.LeaseID
            && x.Generation == key.LeaseGeneration)).ToArray();
        if (leases.Any(lease => lease.LeaseId == 0 || !LeaseCurrent(lease))) { InvalidateFollowDiagnostics(); return; }
        var count = keys.Length;
        var plans = new CompanionFollowPlanDraft?[count];
        var children = new CompanionNativeChildState?[count];
        var bindings = new CompanionFollowBinding?[count];
        var capturedActors = new NativeCompanionDisplayActor?[count];
        var results = Enumerable.Repeat(CompanionFollowDiagnosticResult.Unavailable, count).ToArray();
        var correlations = new CompanionFollowHostCorrelation[count];
        var observedAt = Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());
        var executions = followExecution?.Snapshot ?? Array.Empty<CompanionFollowExecutionSnapshot>();
        uint? beforeFrame = null, afterFrame = null;
        string? batchFailure = null;
        for (var i = 0; i < count; i++)
        {
            var key = keys[i];
            var intent = intents.FirstOrDefault(x => x.LeaseId == key.LeaseID && x.Generation == key.LeaseGeneration);
            if (intent.LeaseId == key.LeaseID) plans[i] = new(key, ++followDiagnosticPlan, intent, plannedAt);
            correlations[i] = ReadHostCorrelation(key, observedAt, executions);
        }
        try
        {
            if (!reader.IsFrameworkThread) batchFailure = "reader-thread-unavailable";
            else if (!reader.TryReadNativeFrame(out var first)) batchFailure = "before-native-frame-unavailable";
            else
            {
                beforeFrame = first;
                for (var i = 0; i < count && ScopeCurrent(); i++)
                {
                    var key = keys[i];
                    if (!LeaseCurrent(leases[i])) results[i] = CompanionFollowDiagnosticResult.BindingChanged;
                    else if (plans[i] is null) results[i] = CompanionFollowDiagnosticResult.NoPlan;
                    else if (!actors.TryGetValue(key.LeaseID, out var entry) || entry.Generation != key.LeaseGeneration)
                        results[i] = CompanionFollowDiagnosticResult.BindingChanged;
                    else if (!entry.Actor.IsReady) results[i] = CompanionFollowDiagnosticResult.PendingChild;
                    else if (!entry.Actor.TryReadFollowBinding(reader, key, selectedTerritory, epoch, out var binding, out var child)
                        || binding.Source != selectedSource || binding.Territory != selectedTerritory)
                        results[i] = CompanionFollowDiagnosticResult.BindingChanged;
                    else
                    {
                        capturedActors[i] = entry.Actor;
                        bindings[i] = binding;
                        children[i] = child;
                        results[i] = CompanionFollowDiagnosticResult.CurrentPair;
                    }
                }
                // A later child's read can invalidate an earlier physical pair.
                for (var i = 0; i < count && ScopeCurrent(); i++)
                {
                    if (bindings[i] is not { } binding || capturedActors[i] is not { } actor) continue;
                    if (!LeaseCurrent(leases[i]) || !actors.TryGetValue(keys[i].LeaseID, out var current)
                        || !ReferenceEquals(actor, current.Actor) || current.Generation != keys[i].LeaseGeneration
                        || !actor.TryReadFollowState(reader, binding, out var final) || final != children[i])
                    { children[i] = null; results[i] = CompanionFollowDiagnosticResult.BindingChanged; }
                }
                if (!reader.TryReadNativeFrame(out var last)) batchFailure = "after-native-frame-unavailable";
                else { afterFrame = last; if (last != first) batchFailure = "native-frame-changed"; }
            }
        }
        catch (Exception) { batchFailure = "reader-exception"; }
        if (!FollowDiagnostics.Enabled || epoch != followDiagnosticEpoch) return;
        if (!ScopeCurrent() || leases.Any(lease => !LeaseCurrent(lease))) { InvalidateFollowDiagnostics(); return; }
        // All reader callbacks, including the after-frame read, have completed.
        // The Framework-owned execution metadata must still match at publication.
        var finalExecutions = followExecution?.Snapshot ?? Array.Empty<CompanionFollowExecutionSnapshot>();
        for (var i = 0; i < count; i++)
        {
            var finalCorrelation = ReadHostCorrelation(keys[i], observedAt, finalExecutions);
            if (finalCorrelation != correlations[i])
                correlations[i] = new(correlations[i].Request, null, "invalid", "host-correlation-changed-during-capture");
        }
        if (batchFailure is not null)
        {
            var failure = batchFailure == "native-frame-changed" ? CompanionFollowDiagnosticResult.FrameChanged
                : batchFailure.Contains("frame-unavailable") ? CompanionFollowDiagnosticResult.FrameUnavailable
                : CompanionFollowDiagnosticResult.Unavailable;
            for (var i = 0; i < count; i++) { children[i] = null; results[i] = failure; }
        }
        var capture = new CompanionFollowCaptureBoundary(selectedTerritory, frameworkFrame, beforeFrame, afterFrame, Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()));
        var samples = new CompanionFollowDiagnosticSample[count];
        for (var i = 0; i < count; i++)
        {
            if (children[i] is null)
                correlations[i] = new(correlations[i].Request, null, "unknown", "child-capture-invalid:" + results[i]);
            else if (correlations[i].Receipt is { } receipt && receipt.ActualHostPosition != children[i]!.Value.PhysicalHostPosition)
                correlations[i] = new(correlations[i].Request, null, "invalid", "host-transform-diverged-since-receipt");
            samples[i] = new(keys[i], ++followDiagnosticSample, beforeFrame, plans[i], children[i], results[i])
                { Capture = capture, HostCorrelation = correlations[i] };
        }
        var complete = batchFailure is null && samples.All(sample => sample.Result == CompanionFollowDiagnosticResult.CurrentPair
            && sample.HostCorrelation?.Status == "associated");
        FollowDiagnostics = new(true, epoch, ++followDiagnosticPublication, Array.AsReadOnly(samples))
        {
            SelectionKind = "exact-leases", BatchBeforeNativeFrame = beforeFrame, BatchAfterNativeFrame = afterFrame,
            BatchComplete = complete, BatchFailureReason = complete ? null : batchFailure ?? "one-or-more-observations-unknown-or-invalid",
        };
    }
}
