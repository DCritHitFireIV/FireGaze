namespace FireGaze.PortraitPreview;

internal enum PortraitOwnedSceneSlotCensusKind { IncompleteOrChanged, Source, ProvedIdle, OccupiedUnknown }

/// <summary>Detached table values only. Unknown pointers are copied, never adopted as an identified native owner.</summary>
internal readonly record struct PortraitOwnedSceneSlotCensusRow(uint Slot, PortraitOwnedSceneSlotCensusKind Kind,
    nint Character, byte ObjectKind, byte ObjectIndex, ushort MemoryIndex, nint RegisteredCamera,
    int ActiveCount, bool RenderActive, nint RetainedOffscreenCamera, nint RetainedViewCamera);

/// <summary>
/// Two complete stable samples under one issued phase. Complete describes this
/// observation only, not a live consumer registry, idle reservation or GPU lease.
/// It cannot outlive/reissue its source phase or grant production admission.
/// </summary>
internal sealed record PortraitOwnedSceneSlotCensus(PortraitOwnedSceneSlotPhase Phase, bool Complete,
    IReadOnlyList<PortraitOwnedSceneSlotCensusRow> Rows)
{
    public bool ProductionAdmission => false;
    internal static PortraitOwnedSceneSlotCensus Incomplete(PortraitOwnedSceneSlotPhase phase)
        => new(phase, false, Array.AsReadOnly(Enumerable.Range(0, 8)
            .Select(slot => new PortraitOwnedSceneSlotCensusRow((uint)slot, PortraitOwnedSceneSlotCensusKind.IncompleteOrChanged,
                0, 0, 0, 0, 0, 0, false, 0, 0)).ToArray()));
}
