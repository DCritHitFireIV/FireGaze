namespace FireGaze.LocalPreview.Interaction;

/// <summary>A copy of the current native market menu, never a remembered hover.</summary>
public readonly record struct MarketItemMenuContext(bool IsDefaultMenu, string? AddonName, uint ParentAddonId,
    nint MenuAgent, nint ContextAgent, uint OwnerAddonId, nint ItemSearchAgent,
    bool HasListingEventInterface, bool HasResultEventInterface, uint ItemSearchAddonId,
    nint UpdateChecker, long UpdateCheckerParam, uint ResultItemId, uint ResultSelectedIndex,
    uint ResultHoveredItemId, uint ResultHoveredContainer, short ResultHoveredSlot);

public static class MarketItemMenuResolver
{
    // Audited against the current native ItemSearch menu constructors. These are
    // pointer identities only: we do not dereference either private subobject.
    // A changed native layout fails the identity check and offers no menu item.
    public const int ListingUpdateCheckerOffset = 0x40;
    public const int ResultEventInterfaceOffset = 0x50;

    public static bool TryResolve(MarketItemMenuContext context, out uint itemId)
    {
        itemId = 0;
        if (!context.IsDefaultMenu || !IsMarketAddon(context.AddonName) || context.ParentAddonId == 0
            || context.ContextAgent == 0 || context.ItemSearchAgent == 0
            || context.MenuAgent != context.ContextAgent || context.OwnerAddonId != context.ParentAddonId)
            return false;

        if (context.AddonName == "ItemSearch")
        {
            if (!context.HasListingEventInterface || context.ItemSearchAddonId != context.ParentAddonId
                || context.UpdateChecker != context.ItemSearchAgent + ListingUpdateCheckerOffset
                || context.UpdateCheckerParam <= 0 || context.UpdateCheckerParam >= 2000000)
                return false;
            // Native ItemSearch binds this value while constructing this menu.
            // ContextItemIndex cannot safely index ListingPageItemIds: the game
            // may sort or filter those rows through another index mapping.
            return TryNormalize((uint)context.UpdateCheckerParam, out itemId);
        }

        if (!context.HasResultEventInterface || context.ResultHoveredContainer != 9999
            || context.ResultSelectedIndex > short.MaxValue
            || context.ResultHoveredSlot != (short)context.ResultSelectedIndex
            || !TryNormalize(context.ResultHoveredItemId, out var selectedItemId)
            || selectedItemId != context.ResultItemId)
            return false;
        itemId = selectedItemId;
        return true;
    }

    public static bool IsMarketAddon(string? name) => name is "ItemSearch" or "ItemSearchResult";

    private static bool TryNormalize(uint rawItemId, out uint itemId)
    {
        itemId = 0;
        if (rawItemId == 0 || rawItemId >= 2000000) return false;
        itemId = rawItemId switch
        {
            >= 1000000 => rawItemId - 1000000,
            >= 500000 => rawItemId - 500000,
            _ => rawItemId,
        };
        return itemId != 0;
    }
}
