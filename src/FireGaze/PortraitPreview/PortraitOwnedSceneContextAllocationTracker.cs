namespace FireGaze.PortraitPreview;

/// <summary>
/// Native constructor/destructor evidence only. No memory read, hook installation,
/// address adoption or synthesized reference operation occurs here. The caller
/// must include both audited native entries in one admitted Hook batch.
/// </summary>
internal sealed class PortraitOwnedSceneContextAllocationTracker(IPortraitOwnedSceneSlotPhaseSource phases,
    Func<bool> admitted) : IPortraitOwnedSceneContextAllocationSource
{
    private readonly object gate = new();
    private readonly Dictionary<nint, Allocation> allocations = [];
    private long serial;
    private readonly record struct Binding(nint View, nint Character, uint CharacterGeneration,
        PortraitIdentity Source, long SessionEpoch, long ManagerGeneration);
    private readonly record struct Allocation(long Epoch, Binding? Owner);

    public void ObserveConstructed(nint allocated, nint returned)
    {
        lock (gate)
        {
            if (allocated == 0) return;
            // Even a failed/foreign constructor result cannot resurrect an older
            // allocation previously recorded at this address.
            allocations.Remove(allocated);
            if (returned != allocated || !Admitted()) return;
            try { allocations[allocated] = new(checked(++serial), null); }
            catch (OverflowException) { allocations.Clear(); }
        }
    }
    public void ObserveDestroying(nint context)
    {
        // Invalidate before Original can release this storage or recursively
        // allocate another context at the same address. Generic unrelated
        // destructor receivers require no memory read and do not enter the map.
        lock (gate) allocations.Remove(context);
    }
    public void InvalidateAll() { lock (gate) allocations.Clear(); }

    public bool TryGetAllocation(nint context, nint view, nint character, uint characterGeneration,
        PortraitOwnedSceneSlotPhase phase, out long allocationEpoch)
    {
        lock (gate)
        {
            allocationEpoch = 0;
            if (context == 0 || view == 0 || character == 0 || characterGeneration == 0
                || !Admitted() || !allocations.TryGetValue(context, out var allocation)
                || !Qualified(phase) || !Current(phase)) return false;
            var binding = new Binding(view, character, characterGeneration, phase.Source,
                phase.SessionEpoch, phase.ManagerGeneration);
            if (allocation.Owner is { } pinned && pinned != binding)
            { allocations.Remove(context); return false; }
            if (!Current(phase) || !Admitted()) return false;
            allocations[context] = allocation with { Owner = binding };
            allocationEpoch = allocation.Epoch; return true;
        }
    }
    private bool Admitted()
    {
        try { if (admitted()) return true; }
        catch (Exception) { }
        allocations.Clear(); return false;
    }
    private bool Current(PortraitOwnedSceneSlotPhase expected)
    {
        try { return phases.TryCapture(out var current) && current == expected; }
        catch (Exception) { allocations.Clear(); return false; }
    }
    private static bool Qualified(PortraitOwnedSceneSlotPhase phase)
        => phase.Source.IsValid && phase.Source.Generation != 0 && phase.Roots.IsValid
            && phase.SessionEpoch > 0 && phase.ManagerGeneration > 0 && phase.PhaseToken > 0
            && phase.InNativePhase && phase.BuildValidated
            && phase.ThreadID == Environment.CurrentManagedThreadId;
}
