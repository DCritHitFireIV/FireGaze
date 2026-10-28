using FireGaze.LocalPreview.Core;
using FireGaze.LocalPreview.Runtime;
using System.Text.Json;

namespace FireGaze.LocalPreview;

internal sealed unsafe partial class MultiCompanionRuntime
{
    private ICompanionFollowSnapshotReader? followDiagnosticReader;
    private uint? followDiagnosticCompanion;
    private long followDiagnosticEpoch = 1;
    private long followDiagnosticPublication;
    private long followDiagnosticPlan;
    private long followDiagnosticSample;
    internal CompanionFollowDiagnosticSnapshot FollowDiagnostics { get; private set; }
        = new(false, 1, 0, Array.Empty<CompanionFollowDiagnosticSample>());
    private static readonly JsonSerializerOptions FollowDiagnosticJSONOptions = new() { IncludeFields = true };
    internal JsonElement ReadFollowDiagnosticsJSON()
        => JsonSerializer.SerializeToElement(FollowDiagnostics, FollowDiagnosticJSONOptions);

    internal bool SetFollowDiagnostics(bool enabled, uint? companionID, out string reason)
    {
        reason = "Follow diagnostics require the Framework thread and one current local companion.";
        if (!framework.IsInFrameworkUpdateThread || disposed) return false;
        if (!enabled) { InvalidateFollowDiagnostics(); reason = ""; return true; }
        if (companionID is null or 0 || !NativeCompanionFollowReader.LayoutValid()
            || !session.Snapshot.Leases.Any(x => x.CompanionId == companionID
                && x.State is CompanionDisplayState.Active or CompanionDisplayState.Pending or CompanionDisplayState.NativeHidden)) return false;
        if (FollowDiagnostics.Enabled && followDiagnosticBatchKeys is null && followDiagnosticCompanion == companionID) { reason = ""; return true; }
        followDiagnosticReader ??= new NativeCompanionFollowReader(framework);
        followDiagnosticBatchKeys = null;
        followDiagnosticCompanion = companionID;
        FollowDiagnostics = new(true, ++followDiagnosticEpoch, ++followDiagnosticPublication,
            Array.Empty<CompanionFollowDiagnosticSample>());
        reason = "";
        return true;
    }

    private void InvalidateFollowDiagnostics()
    {
        if (!FollowDiagnostics.Enabled) return;
        followDiagnosticBatchKeys = null;
        followDiagnosticCompanion = null;
        FollowDiagnostics = new(false, ++followDiagnosticEpoch, ++followDiagnosticPublication,
            Array.Empty<CompanionFollowDiagnosticSample>());
    }

    private void PublishFollowDiagnostics(IReadOnlyList<CompanionBehaviorIntent> intents, TimeSpan plannedAt)
    {
        if (!FollowDiagnostics.Enabled || followDiagnosticReader is not { } reader) return;
        if (followDiagnosticBatchKeys is not null) { PublishFollowBatchDiagnostics(intents, plannedAt); return; }
        var lease = session.Snapshot.Leases.FirstOrDefault(x => x.CompanionId == followDiagnosticCompanion);
        if (lease.LeaseId == 0) { InvalidateFollowDiagnostics(); return; }
        var selected = followDiagnosticCompanion;
        var selectedTerritory = territory;
        var selectedSource = ownerAddress;
        var context = new CompanionExecutionContext(sceneEpoch, ownerID, ownerGeneration);
        if (!FollowDiagnosticContextCurrent(context, selected, selectedTerritory, selectedSource, lease))
        { InvalidateFollowDiagnostics(); return; }
        var key = new CompanionExecutionKey(context.SceneEpoch, context.SourceID, context.SourceGeneration, lease.LeaseId, lease.Generation);
        var intent = intents.FirstOrDefault(x => x.LeaseId == lease.LeaseId && x.Generation == lease.Generation);
        CompanionFollowPlanDraft? plan = intent.LeaseId == lease.LeaseId
            ? new(key, ++followDiagnosticPlan, intent, plannedAt) : null;
        CompanionNativeChildState? child = null;
        uint? frame = null;
        var result = CompanionFollowDiagnosticResult.Unavailable;
        var epoch = followDiagnosticEpoch;
        try
        {
            if (!reader.IsFrameworkThread) result = CompanionFollowDiagnosticResult.Unavailable;
            else if (!reader.TryReadNativeFrame(out var before)) result = CompanionFollowDiagnosticResult.FrameUnavailable;
            else
            {
                frame = before;
                if (lease.State == CompanionDisplayState.Retiring) result = CompanionFollowDiagnosticResult.Retiring;
                else if (plan is null) result = CompanionFollowDiagnosticResult.NoPlan;
                else if (!actors.TryGetValue(lease.LeaseId, out var entry) || entry.Generation != lease.Generation)
                    result = CompanionFollowDiagnosticResult.BindingChanged;
                else if (!entry.Actor.IsReady) result = CompanionFollowDiagnosticResult.PendingChild;
                else if (!entry.Actor.TryReadFollowBinding(reader, key, selectedTerritory, epoch, out var binding, out var actual)
                    || binding.Source != selectedSource || binding.Territory != selectedTerritory)
                    result = CompanionFollowDiagnosticResult.BindingChanged;
                else { child = actual; result = CompanionFollowDiagnosticResult.CurrentPair; }
                if (!reader.TryReadNativeFrame(out var after)) { child = null; frame = null; result = CompanionFollowDiagnosticResult.FrameUnavailable; }
                else if (before != after) { child = null; result = CompanionFollowDiagnosticResult.FrameChanged; }
            }
        }
        catch (Exception) { child = null; result = CompanionFollowDiagnosticResult.Unavailable; }
        if (!FollowDiagnostics.Enabled || epoch != followDiagnosticEpoch) return;
        if (!FollowDiagnosticContextCurrent(context, selected, selectedTerritory, selectedSource, lease))
        { InvalidateFollowDiagnostics(); return; }
        var sample = new CompanionFollowDiagnosticSample(key, ++followDiagnosticSample, frame, plan, child, result);
        FollowDiagnostics = new(true, epoch, ++followDiagnosticPublication,
            Array.AsReadOnly(new[] { sample }));
    }

    private bool FollowDiagnosticContextCurrent(CompanionExecutionContext context, uint? selected,
        uint selectedTerritory, nint selectedSource, CompanionDisplayLease lease)
    {
        if (disposed || !allowed || !client.IsLoggedIn || client.TerritoryType != selectedTerritory
            || territory != selectedTerritory || selected != followDiagnosticCompanion
            || selectedSource == 0 || ownerAddress != selectedSource
            || conditions[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
            || conditions[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51]
            || context != new CompanionExecutionContext(sceneEpoch, ownerID, ownerGeneration)) return false;
        var current = session.Snapshot.Leases.FirstOrDefault(x => x.CompanionId == selected);
        return current.CompanionId == lease.CompanionId && current.LeaseId == lease.LeaseId
            && current.Generation == lease.Generation && current.State == lease.State
            && current.State is CompanionDisplayState.Active or CompanionDisplayState.Pending
                or CompanionDisplayState.NativeHidden or CompanionDisplayState.Retiring;
    }
}
