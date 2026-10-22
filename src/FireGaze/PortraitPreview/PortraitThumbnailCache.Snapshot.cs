namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitThumbnailCache
{
    // Playback thumbnails represent one real frame of the same logical setup, not an
    // exact historic animation instant. Frozen portraits retain their precise progress.
    public static string KeyForSnapshot(ulong contentID, PortraitEditorSnapshot snapshot)
    {
        var value = snapshot.Composition.Paused ? snapshot : snapshot with
            { Composition = snapshot.Composition with { AnimationProgress = 0 } };
        return KeyForSnapshot(contentID.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + value.ToJSON());
    }
}
