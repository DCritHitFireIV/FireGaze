namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitNativeLightAngles(short Vertical, short Horizontal)
{
    public bool IsValid => PortraitLightDirection.TryNative(Vertical, Horizontal, out _);
    public static PortraitNativeLightAngles From(PortraitComposition composition)
        => new(composition.DirectionalVerticalAngle, composition.DirectionalHorizontalAngle);
}

internal sealed record PortraitDirectionalLightView(float Vertical, float Horizontal);

/// <summary>Owns two exact native fields for one gesture. All state belongs to the Framework thread.</summary>
internal sealed class PortraitLightDrag
{
    private long lastToken;
    private long token;
    private PortraitIdentity identity;
    private PortraitHistoryKey key;
    private PortraitNativeLightAngles lastOwned;

    public bool IsActive => token != 0;
    internal long CurrentToken => token;
    internal long LastToken => lastToken;
    public bool CanBegin(long requested) => requested>0 && requested>lastToken;
    public bool Owns(long requested) => requested > 0 && token == requested;

    public bool Begin(long requested, PortraitIdentity owner, PortraitHistoryKey source, PortraitNativeLightAngles current)
    {
        if (!CanBegin(requested)) return false;
        lastToken = requested;
        Cancel();
        if (!owner.IsValid || !current.IsValid) return false;
        token=requested; identity=owner; key=source; lastOwned=current;
        return true;
    }

    public bool Observe(PortraitIdentity owner, PortraitHistoryKey source, PortraitNativeLightAngles current)
    {
        if (!IsActive) return false;
        if (owner==identity && source==key && current==lastOwned && current.IsValid) return true;
        Cancel();
        return false;
    }

    public bool Prepare(long requested, PortraitIdentity owner, PortraitHistoryKey source,
        PortraitNativeLightAngles current, float vertical, float horizontal, out PortraitNativeLightAngles desired)
    {
        desired=default;
        if (!Owns(requested) || !Observe(owner,source,current)
            || !PortraitLightDirection.TryQuantize(vertical,horizontal,out var nativeVertical,out var nativeHorizontal)) return false;
        desired=new(nativeVertical,nativeHorizontal);
        return true;
    }

    public void Accepted(long requested, PortraitNativeLightAngles actual)
    {
        if (!Owns(requested)) return;
        if (!actual.IsValid) {Cancel();return;}
        lastOwned=actual;
    }

    public void Cancel() => token=0;
}
