namespace FireGaze.LocalPreview.Interaction;

/// <summary>Request a menu only for the current immutable native right-click event.</summary>
public static class NativeEmoteMenuPolicy
{
    public static bool ShouldOpen(ushort emoteId, bool rightClick, int menusBefore, int menusAfter, uint ownerAddon)
        => emoteId != 0 && rightClick && menusBefore == menusAfter && ownerAddon != 0;
}
