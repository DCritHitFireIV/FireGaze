using System;

namespace FireGaze.LocalPreview.Runtime;

public readonly record struct TrialNativeHotbarIdentity(nint Module,nint Addon,ushort AddonId,
    nint Source,uint SourceGeneration,ushort Mount,ushort Accessory,long Revision)
{
    public bool Valid => Module!=0 && Addon!=0 && AddonId!=0 && Source!=0 && Revision>0;
}

public readonly record struct TrialNativeHotbarCommand(byte Type,uint Id);

/// <summary>Pure ownership bookkeeping. Snapshots contain command values only; never native
/// text, component, actor or draw pointers. Callers re-resolve all native identities before use.</summary>
public sealed class TrialNativeHotbarLease
{
    public const int SlotCount=16;
    public const int PetSlotOffset=0x105A0;
    public const int SlotStride=0xE8;
    private TrialNativeHotbarIdentity identity;
    private readonly TrialNativeHotbarCommand[] original=new TrialNativeHotbarCommand[SlotCount];
    private readonly TrialNativeHotbarCommand[] expected=new TrialNativeHotbarCommand[SlotCount];
    public bool Held {get;private set;}
    public uint OriginalMode {get;private set;}
    public bool OriginalVisible {get;private set;}
    public long SuspendedRevision {get;private set;}
    public bool CanAcquire(long revision)=>revision>0 && revision!=SuspendedRevision;
    public void Suspend(long revision){if(revision>0)SuspendedRevision=revision;}
    public bool Acquire(TrialNativeHotbarIdentity owner,TrialNativeHotbarCommand[] before,
        TrialNativeHotbarCommand[] replacement,uint mode,bool visible)
    {
        if(Held || !owner.Valid || !CanAcquire(owner.Revision) || before.Length!=SlotCount || replacement.Length!=SlotCount)return false;
        identity=owner;Array.Copy(before,original,SlotCount);Array.Copy(replacement,expected,SlotCount);
        OriginalMode=mode;OriginalVisible=visible;Held=true;return true;
    }
    public bool CanRestore(TrialNativeHotbarIdentity current)=>Held && current==identity;
    public bool CanRestoreCommands(TrialNativeHotbarIdentity current)=>Held && current.Module!=0 && current.Source!=0
        && current.Module==identity.Module && current.Source==identity.Source && current.SourceGeneration==identity.SourceGeneration;
    public bool CanRestoreModule(TrialNativeHotbarIdentity current)=>CanRestoreCommands(current)
        && current.Mount==identity.Mount && current.Accessory==identity.Accessory && current.Revision==identity.Revision;
    public bool CanRestorePresentation(TrialNativeHotbarIdentity current)=>CanRestoreCommands(current) && current.Valid
        && current.Addon==identity.Addon && current.AddonId==identity.AddonId;
    public TrialNativeHotbarCommand Original(int slot)=>original[slot];
    public TrialNativeHotbarCommand Expected(int slot)=>expected[slot];
    public bool IsExpected(int slot,TrialNativeHotbarCommand current)
        =>Held && slot is >=0 and <SlotCount && expected[slot]==current;
    public bool CanInvokeNativeSlot(TrialNativeHotbarIdentity current,int slot,TrialNativeHotbarCommand command)
        =>CanRestore(current) && slot is >=0 and <=TrialSpecialHotbarModel.ExitSlot && IsExpected(slot,command);
    public bool TryRoute(TrialNativeHotbarIdentity current,nint slotAddress,TrialNativeHotbarCommand command,out int slot)
    {
        slot=-1;
        if(!CanRestore(current))return false;
        var offset=slotAddress-current.Module-PetSlotOffset;
        if(offset<0 || offset>=SlotCount*SlotStride || offset%SlotStride!=0)return false;
        var index=(int)(offset/SlotStride);
        if(!IsExpected(index,command))return false;
        slot=index;return true;
    }
    public void Clear(){Held=false;identity=default;Array.Clear(original);Array.Clear(expected);}
}
