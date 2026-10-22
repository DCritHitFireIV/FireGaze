namespace FireGaze.PortraitPreview;

internal enum PortraitOwnedSceneContextRole { Source, Owned, Independent }
internal readonly record struct PortraitOwnedSceneContextEvidence(ContextState State,
    PortraitOwnedSceneContextRole Role, nint View, nint Character, uint CharacterGeneration,
    long RequestEpoch, long ManagerGeneration, long PhaseToken, long AllocationEpoch);

internal interface IPortraitOwnedSceneContextEnvironment
{
    // False is unknown evidence. Unknown receivers never grant a Release permit.
    bool TryReadContext(nint context, nint ownedView, bool ownCall,
        out PortraitOwnedSceneContextEvidence evidence);
    bool IsContextEvidenceCurrent(nint context, PortraitOwnedSceneContextEvidence evidence);
}

// This is a required external native-lifetime boundary, not an actor/address
// heuristic. The default provider remains inert until the complete audited native
// Hook batch is admitted and enabled. Constructor/destructor observations must
// prove allocation generation before an owned self-call exemption may use it.
internal interface IPortraitOwnedSceneContextAllocationSource
{
    bool TryGetAllocation(nint context, nint view, nint character, uint characterGeneration,
        PortraitOwnedSceneSlotPhase phase, out long allocationEpoch);
}

internal interface IPortraitOwnedSceneContextLifetimeEnvironment
{
    void BeginContextLifetimeBatch();
    void EndContextLifetimeBatch();
    void ObserveContextConstructed(nint allocated, nint returned);
    void ObserveContextDestroying(nint context);
}
