namespace FireGaze.LocalPreview.Runtime;

/// <summary>
/// Keeps a local trial alive while the client is between the collection id and
/// its child object. Native setup clears the id/object in separate frames on
/// slower clients; treating that transient gap as a user exit produces the
/// reported automatic dismount and duplicate-player flash.
/// </summary>
public static class TrialPreviewPresencePolicy
{
    public static TimeSpan InitialSpawnGrace => TimeSpan.FromSeconds(12);
    // Retained for old fixture/schema compatibility. Collection deadlines use elapsed time.
    public const int InitialSpawnGraceFrames = 180;

    public static bool IsTransientlyMissing(PreviewSlot slot, ushort expectedId, ushort currentId,
        bool childPresent, int remainingGraceFrames)
    {
        if (remainingGraceFrames <= 0) return false;
        return slot switch
        {
            PreviewSlot.Mount => currentId == expectedId && !childPresent,
            PreviewSlot.Minion => currentId == 0 && !childPresent,
            PreviewSlot.Accessory => currentId == expectedId && !childPresent,
            _ => false,
        };
    }
}
