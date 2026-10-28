namespace FireGaze.PortraitPreview;

internal static class PortraitSourceOwnerPublication
{
    internal static void Refresh(Func<OwnedCharacterKey?> readCurrent, Action<OwnedCharacterKey?> publish)
    {
        ArgumentNullException.ThrowIfNull(readCurrent);
        ArgumentNullException.ThrowIfNull(publish);
        publish(readCurrent());
    }
}
