namespace FireGaze.LocalPreview.Runtime;

public readonly record struct TrialMarketHotbarIdentity(nint Addon, ushort AddonId, nint Root, long Revision)
{
    public bool Valid => Addon != 0 && AddonId != 0 && Root != 0 && Revision > 0;
}

public readonly record struct TrialMarketHotbarState(uint UiFlags, byte HideCount, bool RootVisible,
    short X, short Y, float Scale, float RootX, float RootY, float RootScaleX, float RootScaleY);

public sealed class TrialMarketHotbarLease
{
    private const uint ActionBars = 16;
    private TrialMarketHotbarIdentity owner;
    private TrialMarketHotbarState original;
    private TrialMarketHotbarState expected;
    public bool Held { get; private set; }
    public bool NeedsReacquire(TrialMarketHotbarIdentity identity, TrialMarketHotbarState current)
        => Held && owner == identity && current.UiFlags != expected.UiFlags;
    public bool TryApply(TrialMarketHotbarIdentity identity, TrialMarketHotbarState before,
        TrialMarketHotbarState anchor, bool eligible, out TrialMarketHotbarState result)
    {
        result = before;
        if (Held && owner != identity) Clear();
        if (!eligible || !identity.Valid || !ValidGeometry(anchor)) return false;
        if (!Held)
        {
            // The reviewed CN special bar has one hide class. Mixed-class addons are outside
            // this local exemption and remain owned by the game.
            if (before.UiFlags != ActionBars) return false;
            owner = identity; original = before; Held = true;
        }
        else if (before.UiFlags != expected.UiFlags) return false;
        result = new(0, 0, true, anchor.X, anchor.Y, anchor.Scale,
            anchor.RootX, anchor.RootY, anchor.RootScaleX, anchor.RootScaleY);
        expected = result;
        return true;
    }
    public bool TryRelease(TrialMarketHotbarIdentity identity, TrialMarketHotbarState current,
        bool actionBarsHidden, out TrialMarketHotbarState result)
    {
        result = current;
        if (!Held) return false;
        if (identity != owner) { Clear(); return false; }
        var hideCount = actionBarsHidden ? (byte)Math.Max(1, (int)original.HideCount) : (byte)0;
        result = new(
            current.UiFlags == expected.UiFlags ? original.UiFlags : current.UiFlags,
            current.HideCount == expected.HideCount ? hideCount : current.HideCount,
            current.RootVisible == expected.RootVisible ? original.RootVisible : current.RootVisible,
            current.X == expected.X ? original.X : current.X,
            current.Y == expected.Y ? original.Y : current.Y,
            current.Scale == expected.Scale ? original.Scale : current.Scale,
            current.RootX == expected.RootX ? original.RootX : current.RootX,
            current.RootY == expected.RootY ? original.RootY : current.RootY,
            current.RootScaleX == expected.RootScaleX ? original.RootScaleX : current.RootScaleX,
            current.RootScaleY == expected.RootScaleY ? original.RootScaleY : current.RootScaleY);
        Clear();
        return true;
    }
    private static bool ValidGeometry(TrialMarketHotbarState value)
        => float.IsFinite(value.Scale) && value.Scale > 0 && value.Scale <= 10
            && float.IsFinite(value.RootX) && float.IsFinite(value.RootY)
            && float.IsFinite(value.RootScaleX) && value.RootScaleX > 0 && value.RootScaleX <= 10
            && float.IsFinite(value.RootScaleY) && value.RootScaleY > 0 && value.RootScaleY <= 10;
    public void Clear() { Held = false; owner = default; original = default; expected = default; }
}
