using FireGaze.LocalPreview.Core;

namespace FireGaze.LocalPreview;

internal sealed unsafe partial class MultiCompanionRuntime
{
    private readonly record struct InteractionPlayback(long LeaseID, long Generation, ushort Timeline);
    private InteractionPlayback? requestedInteractionDemo;
    private InteractionPlayback? appliedInteractionDemo;

    public bool HasInteractionDemoRequest => requestedInteractionDemo.HasValue;

    public CompanionCharacterGender ReadInteractionCharacterGender()
    {
        if (!CanRun(out var source)) return CompanionCharacterGender.Unknown;
        return (int)source->DrawData.CustomizeData.Sex switch
        {
            0 => CompanionCharacterGender.Male,
            1 => CompanionCharacterGender.Female,
            _ => CompanionCharacterGender.Unknown,
        };
    }

    /// <summary>Retains one exact owned lease. Native work waits for the next ready Framework frame.</summary>
    public bool SetInteractionDemo(CompanionDisplayLease lease, CompanionInteractionDemoKind kind, bool play)
    {
        if (!CanRun(out var source) || !BindingCurrent(source)) return false;
        var timeline = kind switch
        {
            CompanionInteractionDemoKind.GenderSpecial => (ushort)4,
            CompanionInteractionDemoKind.BattleAction => (ushort)198,
            _ => (ushort)0,
        };
        if (timeline == 0 || !session.Snapshot.Leases.Any(x => x.LeaseId == lease.LeaseId
            && x.Generation == lease.Generation && x.CompanionId == lease.CompanionId
            && x.State is CompanionDisplayState.Pending or CompanionDisplayState.Active or CompanionDisplayState.NativeHidden))
            return false;
        if (play) requestedInteractionDemo = new(lease.LeaseId, lease.Generation, timeline);
        else if (requestedInteractionDemo is { } current
            && current.LeaseID == lease.LeaseId && current.Generation == lease.Generation)
            requestedInteractionDemo = null;
        return true;
    }

    public void CancelInteractionDemo() => requestedInteractionDemo = null;

    private void UpdateInteractionDemo()
    {
        var requested = requestedInteractionDemo;
        if (appliedInteractionDemo is { } previous && previous != requested)
        {
            appliedInteractionDemo = null;
            if (actors.TryGetValue(previous.LeaseID, out var old) && old.Generation == previous.Generation)
                old.Actor.StopInteractionDemo();
            if (requestedInteractionDemo != requested) return;
        }
        if (requested is not { } next) return;
        var lease = session.Snapshot.Leases.FirstOrDefault(x => x.LeaseId == next.LeaseID
            && x.Generation == next.Generation);
        if (lease.LeaseId == 0 || lease.State is CompanionDisplayState.Failed or CompanionDisplayState.Retiring)
        { requestedInteractionDemo = null; return; }
        if (lease.State != CompanionDisplayState.Active || !actors.TryGetValue(next.LeaseID, out var entry)
            || entry.Generation != next.Generation) return;
        if (appliedInteractionDemo == next)
        {
            if (!entry.Actor.HasInteractionDemo(next.Timeline))
            {
                requestedInteractionDemo = null;
                appliedInteractionDemo = null;
                entry.Actor.StopInteractionDemo();
            }
            return;
        }
        if (entry.Actor.TryStartInteractionDemo(next.Timeline))
        {
            // Even cancellation inside native Play must leave an owner that
            // restores the exact override on the next safe Framework frame.
            appliedInteractionDemo = next;
        }
        else if (requestedInteractionDemo == next) requestedInteractionDemo = null;
    }
}
