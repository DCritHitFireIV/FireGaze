namespace FireGaze.PortraitPreview;

internal enum PortraitSlot
{
    MainHand, OffHand, Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, RightRing, LeftRing, Facewear,
}

internal readonly record struct PortraitIdentity(nint Editor, nint State, nint Model, uint Generation, ulong Character)
{
    public bool IsValid => Editor != 0 && State != 0 && Model != 0 && Character != 0;
}

internal readonly record struct PortraitPiece(uint ItemID, byte Stain0, byte Stain1);

internal sealed record PortraitLook(PortraitPiece[] Pieces, bool HeadVisible, bool WeaponVisible, bool Visor)
{
    public PortraitLook Copy() => new((PortraitPiece[])Pieces.Clone(), HeadVisible, WeaponVisible, Visor);
}

internal interface IPortraitPreviewTarget
{
    bool IsCurrent(PortraitIdentity identity);
    // Legacy synchronous targets use IsCurrent solely for ownership. Native targets must override
    // this with positive lifecycle proof when readiness can temporarily prevent a current lookup.
    bool IsEditorOwnerDestroyed(PortraitIdentity identity) => !IsCurrent(identity);
    PortraitLook? Capture(PortraitIdentity identity);
    bool Apply(PortraitIdentity identity, PortraitLook look);
    bool RestoreOriginal(PortraitIdentity identity, PortraitLook look) => Apply(identity, look);
}

/// <summary>Owns one editor wardrobe without taking ownership of camera, lighting, or pose.</summary>
internal sealed class PortraitPreviewSession(IPortraitPreviewTarget target)
{
    private PortraitIdentity identity;
    private PortraitLook? original;
    public bool IsActive => original is not null;
    public long Revision { get; private set; }
    public PortraitLook? Current { get; private set; }
    /// <summary>Entry wardrobe captured when the preview lease opened.</summary>
    public PortraitLook? Original => original?.Copy();

    public bool Open(PortraitIdentity requested)
    {
        if (IsActive && requested == identity && target.IsCurrent(identity)) return true;
        if (!Close()) return false;
        if (!requested.IsValid || !target.IsCurrent(requested)) return false;
        var captured = target.Capture(requested);
        if (captured is null || captured.Pieces.Length != 13 || !target.IsCurrent(requested)) return false;
        identity = requested;
        original = captured.Copy();
        Current = captured.Copy();
        Revision++;
        return true;
    }

    public bool BlockSave(PortraitIdentity requested)
    {
        // Save handling must not itself retire the editor's preview lease during a draw refresh.
        // Framework Update validates lifetime; the native save guard separately verifies state owner.
        return IsActive && identity == requested;
    }

    public void Validate()
    {
        if (IsActive && target.IsEditorOwnerDestroyed(identity)) Forget();
    }

    public bool ObserveLook(PortraitLook observed)
    {
        Validate();
        if (!IsActive || !target.IsCurrent(identity) || observed.Pieces.Length != 13 || observed.Pieces[12].ItemID > ushort.MaxValue) return false;
        // The native adapter has already applied and validated this historical snapshot.
        Current = observed.Copy();
        return true;
    }

    public bool Replace(PortraitSlot slot, PortraitPiece piece)
    {
        if ((uint)slot >= 13) return false;
        Validate();
        if (Current is null) return false;
        var next = Current.Copy();
        next.Pieces[(int)slot] = piece;
        return Apply(next);
    }

    public bool SetVisibility(bool headVisible, bool weaponVisible, bool visor)
    {
        Validate();
        return Current is not null && Apply(Current.Copy() with
        {
            HeadVisible = headVisible, WeaponVisible = weaponVisible, Visor = visor,
        });
    }

    private bool Apply(PortraitLook next)
    {
        Validate();
        if (!IsActive || !target.IsCurrent(identity)) return false;
        var applied = target.Apply(identity, next);
        Validate();
        if (!IsActive || !target.IsCurrent(identity) || !applied) return false;
        Current = next;
        return true;
    }

    public bool Close()
    {
        var restore = original;
        var previous = identity;
        if (restore is null) return true;
        if (target.IsEditorOwnerDestroyed(previous)) { Forget(); return true; }
        if (!target.IsCurrent(previous)) return false;
        bool restored;
        try { restored = target.RestoreOriginal(previous, restore); }
        catch { return false; }
        // A replaced native allocation owns its own appearance. Never roll the old look into it.
        if (target.IsEditorOwnerDestroyed(previous)) { Forget(); return true; }
        if (!restored || !target.IsCurrent(previous)) return false;
        Forget();
        return true;
    }

    private void Forget()
    {
        original = null;
        Current = null;
        identity = default;
    }
}
