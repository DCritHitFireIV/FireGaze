namespace FireGaze.LocalPreview.Runtime;

/// <summary>Tracks a real companion removed while the preview owns a mount setup.</summary>
public sealed class ImplicitMinionOwnership
{
    public ushort Id { get; private set; }
    private bool displaced;

    public void Begin(ushort baselineId, ushort currentId)
    {
        if (baselineId == 0 || currentId != 0 && currentId != baselineId) { Reset(); return; }
        if (Id != baselineId) displaced = false;
        Id = baselineId;
        if (currentId == 0) displaced = true;
    }

    public void Observe(ushort currentId, ushort pendingId)
    {
        if (Id == 0) return;
        if (currentId != 0 && currentId != Id || pendingId != 0 && pendingId != Id) { Reset(); return; }
        if (currentId == 0) displaced = true;
    }

    public bool ShouldRestore(bool manualExit, bool allowsPresentation, bool mountPreview, bool minionPreview,
        ushort currentId, ushort pendingId)
        => Id != 0 && displaced && manualExit && allowsPresentation && !mountPreview && !minionPreview
            && currentId == 0 && (pendingId == 0 || pendingId == Id);

    public void Reset() { Id = 0; displaced = false; }
}
