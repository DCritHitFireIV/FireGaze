using FireGaze.LocalPreview.Core;

namespace FireGaze.LocalPreview;

internal sealed unsafe partial class MultiCompanionRuntime
{
    // Resolve opaque managed identities only when this command runs on Framework.
    // The supplied context prevents delayed commands from adopting a new scene/source.
    internal bool SetFollowDiagnosticsForLeaseSelection(CompanionExecutionContext expectedContext,
        IReadOnlyList<(long LeaseId, long Generation)> selected, out string reason)
    {
        reason = "Exact follow diagnostics require the original current context and 1..8 current lease identities.";
        if (!framework.IsInFrameworkUpdateThread || disposed || !allowed || !client.IsLoggedIn
            || ownerAddress == 0 || territory != client.TerritoryType
            || conditions[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
            || conditions[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51]
            || expectedContext != new CompanionExecutionContext(sceneEpoch, ownerID, ownerGeneration)
            || selected is null) return false;
        var count = selected.Count;
        if (count is < 1 or > 8) return false;
        var keys = new CompanionExecutionKey[count];
        var leases = session.Snapshot.Leases;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var identity = selected[i];
                if (identity.LeaseId <= 0 || identity.Generation <= 0 || !leases.Any(lease =>
                    lease.LeaseId == identity.LeaseId && lease.Generation == identity.Generation
                    && lease.State is CompanionDisplayState.Active or CompanionDisplayState.Pending or CompanionDisplayState.NativeHidden))
                    return false;
                keys[i] = new(expectedContext.SceneEpoch, expectedContext.SourceID, expectedContext.SourceGeneration,
                    identity.LeaseId, identity.Generation);
            }
        }
        catch (Exception) { reason = "Exact diagnostic identities changed while being copied."; return false; }
        return SetFollowDiagnosticsForLeases(keys, out reason);
    }
}
