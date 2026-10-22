namespace FireGaze.PortraitPreview;

internal delegate bool PortraitOwnedSceneInstallationValidation(out string reason);

/// <summary>
/// Real typed slot-reader environment. No installation, native memory reads or
/// implicit slot selection occurs on construction. The normal validator still
/// denies production admission; tests replace only that external native gate.
/// </summary>
internal sealed class PortraitOwnedSceneHookEnvironment(
    IPortraitOwnedSceneSlotNativeApi native, PortraitOwnedSceneSlotNativePhaseSource phases,
    Func<bool> framework, nint moduleBase, PortraitOwnedSceneInstallationValidation? validate = null,
    IPortraitOwnedSceneContextAllocationSource? contextAllocations = null)
    : IPortraitOwnedSceneHookEnvironment, IPortraitOwnedSceneHookPhaseEnvironment, IPortraitOwnedSceneContextEnvironment, IPortraitOwnedSceneContextLifetimeEnvironment, IPortraitOwnedSceneActiveMutationEnvironment
{
    private readonly PortraitOwnedSceneSlotReader reader = new(native, phases);
    private PortraitIdentity source;
    private uint requestedSlot;
    private long request, lastRequest, managerGeneration, sessionEpoch, ownerAllocation;
    private bool lifetimeBatch;
    private PortraitOwnedSceneSlotRoots activeRoots;
    private PortraitOwnedSceneContextAllocationTracker? lifetimeTracker;
    private IPortraitOwnedSceneContextAllocationSource ContextAllocations
        => contextAllocations ?? (lifetimeTracker ??= new(phases, () => lifetimeBatch && IsFrameworkThread));
    private PortraitOwnedSceneSlotOwner? owner;

    public bool IsFrameworkThread { get { try { return framework(); } catch (Exception) { return false; } } }
    public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(moduleBase);
    public bool TryValidateInstallation(out string reason)
    {
        if (!IsFrameworkThread || moduleBase <= 0) { reason = "No qualified native Framework/module context."; return false; }
        try { return (validate ?? PortraitOwnedSceneHooks.TryValidateCurrentProcess)(out reason); }
        catch (Exception error) { reason = error.Message; return false; }
    }

    public bool QueueRequest(PortraitIdentity candidate, uint slot, long requestEpoch)
    {
        if (!IsFrameworkThread || request != 0 || !candidate.IsValid || candidate.Generation == 0
            || slot >= 8 || requestEpoch <= lastRequest) return false;
        source = candidate; requestedSlot = slot; request = lastRequest = requestEpoch;
        owner = null; managerGeneration = sessionEpoch = ownerAllocation = 0;
        return true;
    }

    public IDisposable? EnterSourcePhase(PortraitIdentity candidate, uint frame, long requestEpoch)
        => Matches(candidate, requestEpoch) ? phases.EnterSource(candidate, frame, requestEpoch) : null;
    public IDisposable? EnterRetirementPhase(PortraitIdentity candidate, uint frame, long requestEpoch)
        => Matches(candidate, requestEpoch) ? phases.EnterRetirement(candidate, frame, requestEpoch) : null;
    private bool Matches(PortraitIdentity candidate, long requestEpoch)
        => IsFrameworkThread && requestEpoch > 0 && request == requestEpoch && source == candidate;
    private bool TryPhase(out PortraitOwnedSceneSlotPhase phase)
    {
        if (!phases.TryCapture(out phase) || request <= 0 || phase.Source != source) return false;
        if (managerGeneration == 0)
        { managerGeneration = phase.ManagerGeneration; sessionEpoch = phase.SessionEpoch; }
        return phase.ManagerGeneration == managerGeneration && phase.SessionEpoch == sessionEpoch;
    }

    public bool IsSourceCurrent(PortraitIdentity candidate)
        => candidate == source && TryPhase(out var phase) && reader.TryReadSourceSlot(phase, out var slot)
            && slot != requestedSlot;
    public bool IsSlotIdle(uint slot)
        => slot == requestedSlot && TryPhase(out var phase) && reader.TryReadIdle(slot, phase, out _);
    public bool TryReadViewSlot(nint view, out uint slot)
    {
        slot = uint.MaxValue;
        return TryPhase(out var phase) && (view == source.Model
            ? reader.TryReadSourceSlot(phase, out slot) : reader.TryReadViewSlot(view, phase, out slot));
    }

    public bool IsSlotOwnedBy(uint slot, nint view)
    {
        if (slot != requestedSlot || !TryPhase(out var phase)
            || !reader.TryReadOwnerCandidate(slot, view, phase, out var candidate)) return false;
        long allocation = 0;
        if (candidate.Stage == PortraitOwnedSceneSlotStage.Prepared
            && (!ContextAllocations.TryGetAllocation(candidate.Context, candidate.View, candidate.Character,
                candidate.CharacterGeneration, phase, out allocation) || allocation <= 0)) return false;
        if (owner is { } pinned)
        {
            if (pinned.View != candidate.View || pinned.Character != candidate.Character
                || pinned.CharacterGeneration != candidate.CharacterGeneration) return false;
            if (pinned.Stage == PortraitOwnedSceneSlotStage.Prepared)
                return pinned == candidate && ownerAllocation == allocation;
            if (candidate.Stage == PortraitOwnedSceneSlotStage.Initialized) return pinned == candidate;
            if (candidate.Stage != PortraitOwnedSceneSlotStage.Prepared) return false;
        }
        owner = candidate; ownerAllocation = allocation; return true;
    }

    public bool TryReadContext(nint context, nint ownedView, bool ownCall,
        out PortraitOwnedSceneContextEvidence evidence)
    {
        evidence = default;
        try
        {
            if (context == 0 || !TryPhase(out var phase)) return false;
            if (reader.TryReadSourceContext(context, phase, out var state))
            {
                evidence = new(state, PortraitOwnedSceneContextRole.Source, source.Model,
                    (nint)source.Character, source.Generation, request, phase.ManagerGeneration, phase.PhaseToken, 0);
                return true;
            }
            if (owner is not { } pinned || pinned.View != ownedView
                || !reader.TryReadOwnedContext(context, requestedSlot, pinned, ownCall, phase, out state)) return false;
            long allocation = 0;
            if (ownCall
                && (!ContextAllocations.TryGetAllocation(context, pinned.View, pinned.Character,
                    pinned.CharacterGeneration, phase, out allocation) || allocation <= 0)) allocation = 0;
            evidence = new(state, PortraitOwnedSceneContextRole.Owned, pinned.View, pinned.Character,
                pinned.CharacterGeneration, request, phase.ManagerGeneration, phase.PhaseToken, allocation);
            return true;
        }
        catch (Exception) { evidence = default; return false; }
    }
    public bool IsContextEvidenceCurrent(nint context, PortraitOwnedSceneContextEvidence evidence)
        => TryReadContext(context, evidence.View, evidence.Role == PortraitOwnedSceneContextRole.Owned
            && evidence.AllocationEpoch > 0, out var current) && current == evidence;

    public bool TryQualifyActiveSource(PortraitIdentity candidate, uint frame, long requestEpoch, uint targetSlot, out nint renderer)
    {
        renderer = 0;
        if (!Matches(candidate, requestEpoch) || targetSlot != requestedSlot || !TryPhase(out var phase)
            || phase.NativeFrame != frame || !reader.TryReadCensus(phase, out var census) || !census.Complete
            || census.Rows.Count != 8 || !phases.TryCapture(out var after) || after != phase
            || !reader.TryReadSourceSlot(phase, out var sourceSlot) || sourceSlot == targetSlot) return false;
        if (activeRoots.IsValid && activeRoots != phase.Roots) return false;
        activeRoots = phase.Roots; renderer = phase.Roots.Renderer; return renderer != 0;
    }
    public IDisposable? BeginOwnedCounterMutation() => activeRoots.IsValid ? phases.BeginOwnedCounterMutation() : null;
    public IDisposable? ResumeOwnedCounterPhase(PortraitIdentity candidate, uint frame, long requestEpoch, nint renderer)
    {
        if (!Matches(candidate, requestEpoch) || !activeRoots.IsValid || renderer != activeRoots.Renderer) return null;
        var scope = phases.EnterSource(candidate, frame, requestEpoch);
        if (scope is not null && TryPhase(out var phase) && phase.Roots == activeRoots) return scope;
        scope?.Dispose(); return null;
    }

    public void EndRequest(long requestEpoch)
    {
        if (request != requestEpoch) return;
        phases.EndRequest(requestEpoch); source = default; request = 0; owner = null;
        managerGeneration = sessionEpoch = ownerAllocation = 0; activeRoots = default;
    }

    public void BeginContextLifetimeBatch() { lifetimeBatch = true; _ = ContextAllocations; }
    public void EndContextLifetimeBatch() { lifetimeBatch = false; lifetimeTracker?.InvalidateAll(); }
    public void ObserveContextConstructed(nint allocated, nint returned)
    { if (lifetimeBatch && ContextAllocations is PortraitOwnedSceneContextAllocationTracker tracker) tracker.ObserveConstructed(allocated, returned); }
    public void ObserveContextDestroying(nint context) => lifetimeTracker?.ObserveDestroying(context);
}
