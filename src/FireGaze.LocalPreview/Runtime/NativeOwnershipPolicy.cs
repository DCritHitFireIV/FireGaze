namespace FireGaze.LocalPreview.Runtime;

/// <summary>Native state wins even when the engine reuses an existing model object.</summary>
public static class NativeOwnershipPolicy
{
    public static bool RestoreSibling(bool winner, ushort currentId, ushort previewId, ushort baselineId, bool sameObject)
        => !winner && sameObject && (currentId == previewId || currentId == baselineId);

    public static bool RemovePendingChild(ushort currentId, ushort baselineId, uint requestedId, bool sameObject)
        => sameObject && currentId == baselineId && currentId != requestedId;
    public static bool RemovePendingMount(ushort currentId, ushort baselineId, uint requestedId, bool sameObject)
        => sameObject && currentId == baselineId && currentId != requestedId;
}
