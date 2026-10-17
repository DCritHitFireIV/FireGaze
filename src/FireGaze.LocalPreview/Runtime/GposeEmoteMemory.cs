using System;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>
/// Keeps the local character's last executed emote independently of the live controller's
/// idle resets. Sitting clears old actions and requests an idle default after standing.
/// All calls belong to the framework thread. Nothing here alters
/// gameplay state or a user-file emote history.
/// </summary>
public sealed class GposeEmoteMemory
{
    private ulong characterId;
    private ushort emoteId;
    private byte pose;
    private long nextEntry;
    private long entry;
    private ushort entryEmote;
    private byte entryPose;
    private bool postureReset;
    private bool entryNeedsIdle;
    private bool entered;
    private bool applied;

    public ushort EmoteId => emoteId;
    public byte Pose => pose;
    public bool HasPendingReplay => entered && !applied && (entryEmote != 0 || entryNeedsIdle);

    /// <summary>Call only for actual local acceptance or an actually started preview timeline.</summary>
    public bool Record(ulong owner, ushort id, byte requestedPose = 0, bool accepted = true, bool resetPosture = false)
    {
        if (owner == 0 || id == 0 || !accepted) return false;
        if(resetPosture){ResetPosture(owner);return true;}
        ChangeCharacter(owner);
        emoteId = id;
        pose = requestedPose;
        postureReset=false;
        return true;
    }

    public void ResetPosture(ulong owner)
    {
        if(owner==0)return;
        ChangeCharacter(owner);emoteId=0;pose=0;postureReset=true;EndEntry();
    }

    public void ChangeCharacter(ulong owner)
    {
        if (characterId == owner) return;
        characterId = owner;
        emoteId = 0;
        pose = 0;
        postureReset=false;
        EndEntry();
    }

    /// <summary>Capture before destroying the local preview actor for native GPose allocation.</summary>
    public long BeginEntry(ulong owner)
    {
        ChangeCharacter(owner);
        EndEntry();
        if (owner == 0 || nextEntry == long.MaxValue) return 0;
        entry = ++nextEntry;
        entryEmote = emoteId;
        entryPose = pose;
        entryNeedsIdle=postureReset;
        return entry;
    }

    /// <summary>The current trial or native sit belongs to this entry only, never persistent history.</summary>
    public long BeginPostureEntry(ulong owner,ushort id,byte requestedPose)
    {
        long token=BeginEntry(owner);
        if(token!=0){entryEmote=id;entryPose=requestedPose;entryNeedsIdle=false;}
        return token;
    }

    /// <summary>An unidentified native posture cannot authorize an invented local replay.</summary>
    public long BeginUnidentifiedPostureEntry(ulong owner)
    {
        long token=BeginEntry(owner);entryEmote=0;entryNeedsIdle=false;return token;
    }

    public void CompleteEntry(long token, bool success)
    {
        if (token == 0 || token != entry) return;
        entered = success;
        if (!success) EndEntry();
    }

    public void EndEntry()
    {
        entry = 0;
        entryEmote = 0;
        entryPose = 0;
        entryNeedsIdle=false;
        entered = false;
        applied = false;
    }

    /// <summary>
    /// Retries only while the native GPose clone is not ready. A successful replay is
    /// consumed once per entry, so later posing-tool edits and pause controls win.
    /// </summary>
    public bool TryReplay(ulong owner, Func<ushort, byte, bool> replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        if (owner == 0 || owner != characterId || !HasPendingReplay) return false;
        long expected = entry;
        bool result = replay(entryEmote, entryPose);
        if (result && entry == expected) applied = true;
        return result;
    }
}
