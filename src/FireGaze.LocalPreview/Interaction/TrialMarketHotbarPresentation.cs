using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FireGaze.LocalPreview.Runtime;

namespace FireGaze.LocalPreview.Interaction;

public sealed unsafe class TrialMarketHotbarPresentation
{
    private readonly TrialMarketHotbarLease lease = new();
    public bool Active => lease.Held;

    /// <summary>Framework thread only; caller resolves these exact native addons each frame.</summary>
    public bool Apply(AtkUnitBase* special, AtkUnitBase* primary, UiFlags hiddenUiFlags,
        bool eligibleTrial, bool marketOpen, long revision)
    {
        if (!eligibleTrial || !marketOpen)
        {
            Release(special, hiddenUiFlags, revision);
            return false;
        }
        if (special == null || special->RootNode == null || primary == null || primary->RootNode == null
            || primary == special || primary->RootNode == special->RootNode) return false;
        var identity = Identity(special, revision);
        var before = Read(special);
        var anchor = Read(primary);
        if (!lease.TryApply(identity, before, anchor, true, out var state))
        {
            // A UI hide refresh can rewrite the special bar's flags while this
            // lease is held. Restore every field still owned by the lease, then
            // take a new lease from the restored game state. Mixed flags remain
            // rejected after the safe restore and are never overwritten.
            if (!lease.NeedsReacquire(identity, before)
                || !lease.TryRelease(identity, before, (hiddenUiFlags & UiFlags.ActionBars) != 0, out var restored))
                return false;
            Write(special, restored);
            before = Read(special);
            if (!lease.TryApply(identity, before, anchor, true, out state)) return false;
        }
        Write(special, state);
        // AtkUnitBase.IsVisible changes only its Show bit; the root's visibility is independent.
        special->IsVisible = true;
        return true;
    }

    public bool Release(AtkUnitBase* special, UiFlags hiddenUiFlags, long revision)
    {
        if (special == null || special->RootNode == null) { lease.Clear(); return false; }
        if (!lease.TryRelease(Identity(special, revision), Read(special),
            (hiddenUiFlags & UiFlags.ActionBars) != 0, out var state)) return false;
        Write(special, state);
        return true;
    }

    private static TrialMarketHotbarIdentity Identity(AtkUnitBase* addon, long revision)
        => new((nint)addon, addon->Id, (nint)addon->RootNode, revision);

    private static TrialMarketHotbarState Read(AtkUnitBase* addon)
        => new((uint)addon->UiFlags, addon->UiFlagsHideCount, IsVisible(addon->RootNode),
            addon->X, addon->Y, addon->Scale, addon->RootNode->X, addon->RootNode->Y,
            addon->RootNode->ScaleX, addon->RootNode->ScaleY);

    private static void Write(AtkUnitBase* addon, TrialMarketHotbarState state)
    {
        // This changes only the leased special bar. No global ToggleUi call, regular hotbar,
        // configuration position, action slot or save file is changed. Native ToggleUi increments
        // a matching addon's hide reference count and decrements with a zero clamp. While exempt,
        // this addon does not receive those increments; release reconciles the current global flag.
        addon->UiFlags = (UiFlags)state.UiFlags;
        addon->UiFlagsHideCount = state.HideCount;
        addon->X = state.X; addon->Y = state.Y; addon->Scale = state.Scale;
        var root = addon->RootNode;
        root->X = state.RootX; root->Y = state.RootY;
        root->ScaleX = state.RootScaleX; root->ScaleY = state.RootScaleY;
        SetVisible(root, state.RootVisible);
    }

    // The generated IsVisible/ToggleVisibility wrappers resolve a client function pointer.
    // Reading and writing the native Visible bit directly keeps the lease deterministic during
    // addon reconstruction and in the offline ABI harness, while matching the native wrapper's
    // state contract. No other node flags are changed.
    private static bool IsVisible(AtkResNode* node)
        => node != null && (node->NodeFlags & NodeFlags.Visible) != 0;

    private static void SetVisible(AtkResNode* node, bool visible)
    {
        if (node == null) return;
        node->NodeFlags = visible ? node->NodeFlags | NodeFlags.Visible : node->NodeFlags & ~NodeFlags.Visible;
    }
}
