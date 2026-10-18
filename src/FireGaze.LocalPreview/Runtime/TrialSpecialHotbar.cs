using System;
using System.Collections.Generic;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>A cosmetic special action, resolved from the current client's six native special slots.</summary>
public enum TrialSpecialActionKind { NativeAction, FlightPose }
public sealed record TrialSpecialAction(int Slot, uint Id, uint Icon, string Name, ushort Timeline, bool UsesMountActor,
    TrialSpecialActionKind Kind=TrialSpecialActionKind.NativeAction);
public sealed record TrialSpecialBar(bool IsMount, ushort Id, long Revision, string Name, IReadOnlyList<TrialSpecialAction?> Actions,
    bool FlightPose=false);

/// <summary>Reads content only. It never writes hotbar files or the genuine player's state.</summary>
public sealed class TrialSpecialHotbarCatalog(ExcelModule excel)
{
    private readonly Dictionary<(bool Mount, ushort Id), TrialSpecialBar> cache = new();

    public TrialSpecialBar? GetBar(bool isMount, ushort id, long revision)
    {
        if (id == 0 || id > short.MaxValue) return null;
        if (cache.TryGetValue((isMount, id), out var cached)) return cached with { Revision = revision };
        var slots = new TrialSpecialAction?[6];
        string name;
        if (isMount)
        {
            if (excel.GetSheet<Mount>().GetRowOrDefault(id) is not { } mount) return null;
            name = mount.Singular.ExtractText();
            if (mount.MountAction.RowId != 0 && mount.MountAction.IsValid)
            {
                var actions = mount.MountAction.Value.Action;
                for (var i = 0; i < slots.Length; i++) slots[i] = Resolve(i, actions[i], true);
            }
            // The borrowed slot uses Jump's original icon only. Its leased dispatcher
            // invokes a local visual toggle and never executes GeneralAction Jump.
            var flightSlot=Array.FindLastIndex(slots,action=>action is not null)+1;
            if(flightSlot<slots.Length && excel.GetSheet<GeneralAction>().GetRowOrDefault(2) is {} jump)
                slots[flightSlot]=new(flightSlot,2,(uint)jump.Icon,"飞行姿态",0,true,TrialSpecialActionKind.FlightPose);
        }
        else
        {
            if (excel.GetSheet<Ornament>().GetRowOrDefault(id) is not { } ornament) return null;
            name = ornament.Singular.ExtractText();
            if (ornament.Action.RowId != 0 && ornament.Action.IsValid)
            {
                var actions = ornament.Action.Value.Actions;
                for (var i = 0; i < slots.Length; i++) slots[i] = Resolve(i, actions[i], false);
            }
        }
        var result = new TrialSpecialBar(isMount, id, revision, name, Array.AsReadOnly(slots));
        cache[(isMount,id)] = result;
        return result;
    }

    private static TrialSpecialAction? Resolve(int slot, RowRef<Lumina.Excel.Sheets.Action> reference, bool mount)
    {
        if (reference.RowId == 0 || !reference.IsValid) return null;
        var action = reference.Value;
        var timeline = action.AnimationEnd.RowId;
        if (timeline == 0 || timeline > ushort.MaxValue || !action.AnimationEnd.IsValid) return null;
        // Both categories are native cosmetic action timelines. The timeline's model prefix
        // must agree with the clone target; unrelated gameplay actions never become previews.
        var key = action.AnimationEnd.Value.Key.ExtractText();
        if (!key.StartsWith(mount ? "mount_sp/" : "ornament_sp/", StringComparison.Ordinal)) return null;
        return new(slot, reference.RowId, action.Icon, action.Name.ExtractText(), (ushort)timeline, mount);
    }
}

/// <summary>Framework-thread state and routing shared by clicks and the native special keybinds.</summary>
public sealed class TrialSpecialHotbarModel(TrialSpecialHotbarCatalog catalog)
{
    public const int ExitSlot = 11;
    private long frame;
    private readonly long[] activatedFrames = new long[ExitSlot + 1];
    private readonly bool[] activated = new bool[ExitSlot + 1];
    public TrialSpecialBar? Current { get; private set; }

    public void SetPreview(bool mount, ushort id, long revision, bool flightPose=false)
    {
        bool same=Current is {} old && old.IsMount==mount && old.Id==id && old.Revision==revision;
        if(same && Current!.FlightPose==flightPose)return;
        Current = catalog.GetBar(mount, id, revision);
        if(Current is {} current && mount && flightPose)
        {
            var actions=new TrialSpecialAction?[current.Actions.Count];
            for(var i=0;i<actions.Length;i++)actions[i]=current.Actions[i] is {Kind:TrialSpecialActionKind.FlightPose} action
                ?action with{Name="地面姿态"}:current.Actions[i];
            Current=current with{Actions=Array.AsReadOnly(actions),FlightPose=true};
        }
        // Updating the button label after a toggle must not let a second route
        // replay the same physical input in this frame.
        if(!same)Array.Clear(activated);
    }
    public void Clear() { Current = null; Array.Clear(activated); }
    public void BeginFrame(long token) => frame = token;

    /// <summary>Map the genuine special bar's original slot to the replacement bar's slot.
    /// A missing replacement slot is still consumed so the genuine old item cannot execute.</summary>
    public bool TryMapNativeAction(uint actionId, ushort sourceMountId, ushort sourceAccessoryId, out int slot)
    {
        slot = -1;
        if (Current is not { } current || actionId == 0) return false;
        var source = sourceMountId != 0 ? catalog.GetBar(true, sourceMountId, 0)
            : sourceAccessoryId != 0 ? catalog.GetBar(false, sourceAccessoryId, 0) : null;
        if (source is not null)
            for (var i = 0; i < source.Actions.Count; i++)
                if (source.Actions[i] is {Kind:TrialSpecialActionKind.NativeAction} action && action.Id==actionId) { slot = i; return true; }
        for (var i = 0; i < current.Actions.Count; i++)
            if (current.Actions[i] is {Kind:TrialSpecialActionKind.NativeAction} action && action.Id==actionId) { slot = i; return true; }
        return false;
    }

    /// <returns>True when the input belongs to the current bar, including an empty slot.</returns>
    public bool ActivateSlot(int slot, long expectedRevision, Func<uint, bool> playAction, System.Action exit,Func<bool>? toggleFlightPose=null)
    {
        if (Current is not { } current || current.Revision != expectedRevision || slot is < 0 or > ExitSlot) return false;
        if (activated[slot] && activatedFrames[slot] == frame) return true;
        activated[slot] = true;
        activatedFrames[slot] = frame;
        if (slot == ExitSlot) exit();
        else if (slot < current.Actions.Count && current.Actions[slot] is {Kind:TrialSpecialActionKind.FlightPose}) toggleFlightPose?.Invoke();
        else if (slot < current.Actions.Count && current.Actions[slot] is { } action) playAction(action.Id);
        return true;
    }
}

public static class TrialSpecialKeyboardRouting
{
    public static bool CanHandle(bool hasPreview, bool gameUiHidden, bool gameTextInput, bool pluginTextInput)
        => hasPreview && !gameUiHidden && !gameTextInput && !pluginTextInput;
}

public enum TrialSpecialBarVisibilityChange { None, Hide, Restore }

/// <summary>Owns one transient native addon hide. An absent/rebuilt addon or changed genuine
/// source ends the lease; no stale address or previous collection state is ever restored.</summary>
public sealed class TrialSpecialNativeBarLease
{
    private nint address;
    private ushort addonId;
    private nint owner;
    private uint generation;
    private ushort mount;
    private ushort accessory;
    private bool held;

    public TrialSpecialBarVisibilityChange Observe(bool trialActive, nint currentAddress, ushort currentAddonId,
        bool visible, nint currentOwner, uint currentGeneration, ushort currentMount, ushort currentAccessory)
    {
        bool present = currentAddress != 0 && currentAddonId != 0 && currentOwner != 0;
        bool same = held && present && currentAddress == address && currentAddonId == addonId
            && currentOwner == owner && currentGeneration == generation
            && currentMount == mount && currentAccessory == accessory;
        if (!trialActive)
        {
            held = false;
            return same && !visible ? TrialSpecialBarVisibilityChange.Restore : TrialSpecialBarVisibilityChange.None;
        }
        if (same) return visible ? TrialSpecialBarVisibilityChange.Hide : TrialSpecialBarVisibilityChange.None;
        held = false;
        if (!present || !visible || (currentMount == 0 && currentAccessory == 0)) return TrialSpecialBarVisibilityChange.None;
        address = currentAddress; addonId = currentAddonId; owner = currentOwner;
        generation = currentGeneration; mount = currentMount; accessory = currentAccessory; held = true;
        return TrialSpecialBarVisibilityChange.Hide;
    }
}
