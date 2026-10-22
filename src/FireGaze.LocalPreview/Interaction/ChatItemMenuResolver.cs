namespace FireGaze.LocalPreview.Interaction;

/// <summary>The current native menu owner, copied before any menu callback is retained.</summary>
public readonly record struct ChatItemMenuContext(bool IsDefaultMenu, string? AddonName, uint ParentAddonId,
    nint MenuAgent, nint ContextAgent, uint OwnerAddonId, nint ChatAgent, bool HasChatEventInterface,
    uint ContextItemId);

public static class ChatItemMenuResolver
{
    public static bool TryResolve(ChatItemMenuContext context, out uint itemId)
    {
        itemId = 0;
        if (!context.IsDefaultMenu || !IsChatAddon(context.AddonName) || context.ParentAddonId == 0
            || context.ContextAgent == 0 || context.ChatAgent == 0 || context.MenuAgent != context.ContextAgent
            || context.OwnerAddonId != context.ParentAddonId || !context.HasChatEventInterface
            || context.ContextItemId == 0 || context.ContextItemId >= 2000000) return false;
        // Match native item-link encoding. Event items use a different sheet and
        // must never become a collection item by applying a modulo operation.
        itemId = context.ContextItemId switch
        {
            >= 1000000 => context.ContextItemId - 1000000,
            >= 500000 => context.ContextItemId - 500000,
            _ => context.ContextItemId,
        };
        return itemId != 0;
    }

    public static bool IsChatAddon(string? name) => name is "ChatLog" or "ChatLogPanel_0" or "ChatLogPanel_1"
        or "ChatLogPanel_2" or "ChatLogPanel_3";
}
