namespace FireGaze.LocalPreview.Interaction;

public enum PreviewKind { Mount, Minion, Accessory, Emote }

public sealed record PreviewMenuOptions(bool Items = true, bool Mount = true, bool Minion = true,
    bool Accessory = true, bool Emote = true)
{
    public bool Allows(PreviewKind kind, bool fromItem) => (!fromItem || Items) && kind switch
    {
        PreviewKind.Mount => Mount,
        PreviewKind.Minion => Minion,
        PreviewKind.Accessory => Accessory,
        PreviewKind.Emote => Emote,
        _ => false,
    };
}

public readonly record struct MenuPreviewRequest(PreviewKind Kind, uint Id);

public readonly record struct ObjectPreviewSnapshot(ushort MountId, ushort MinionId, ushort AccessoryId);

/// <summary>Only a nested dispatch for the same text node inherits its original click.</summary>
public sealed class NativeMouseDispatchContext
{
    private readonly Stack<(nint Node, byte? Button)> previous = new();
    private nint node;
    public byte? Button { get; private set; }
    public void Enter(nint currentNode, byte? clickedButton)
    {
        previous.Push((node, Button));
        Button = clickedButton ?? (currentNode != 0 && node == currentNode ? Button : null);
        node = currentNode;
    }
    public void Exit()
    {
        var parent = previous.TryPop(out var value) ? value : default;
        node = parent.Item1;
        Button = parent.Item2;
    }
}

public sealed class NativeLinkContext
{
    private readonly Stack<ushort> previous = new();
    public ushort EmoteId { get; private set; }
    public void Enter(ushort emoteId)
    {
        previous.Push(EmoteId);
        EmoteId = emoteId;
    }
    public void Exit() => EmoteId = previous.TryPop(out var value) ? value : (ushort)0;
    public void Clear()
    {
        EmoteId = 0;
        previous.Clear();
    }
}

public static class PreviewMenuResolver
{
    public static IReadOnlyList<MenuPreviewRequest> Resolve(ObjectPreviewSnapshot snapshot, ushort emoteId)
    {
        var requests = new List<MenuPreviewRequest>(4);
        if (snapshot.MountId != 0) requests.Add(new(PreviewKind.Mount, snapshot.MountId));
        if (snapshot.MinionId != 0) requests.Add(new(PreviewKind.Minion, snapshot.MinionId));
        if (snapshot.AccessoryId != 0) requests.Add(new(PreviewKind.Accessory, snapshot.AccessoryId));
        if (emoteId != 0) requests.Add(new(PreviewKind.Emote, emoteId));
        return requests;
    }
}

public static class ItemCollectionResolver
{
    // Action row IDs, not ItemAction row IDs. For these three collection kinds,
    // ItemAction.Data[0] is the actual collection row ID.
    public static MenuPreviewRequest? Resolve(uint itemActionId, uint collectionId)
    {
        if (collectionId == 0 || collectionId > ushort.MaxValue) return null;
        return itemActionId switch
        {
            1322 => new(PreviewKind.Mount, collectionId),
            853 => new(PreviewKind.Minion, collectionId),
            20086 => new(PreviewKind.Accessory, collectionId),
            _ => null,
        };
    }

    public static IReadOnlyList<MenuPreviewRequest> ResolveAll(uint itemActionId, uint collectionId,
        IReadOnlyDictionary<uint, uint> emoteUnlockLinks)
    {
        if (itemActionId != 2633) return Resolve(itemActionId, collectionId) is { } request ? [request] : [];
        if (collectionId == 0) return [];
        // Generic unlock items store UnlockLink, not Emote.RowId. A textbook can
        // unlock multiple variants; other generic unlocks have no Emote match.
        return emoteUnlockLinks.Where(entry => entry.Key is > 0 and <= ushort.MaxValue && entry.Value == collectionId)
            .OrderBy(entry => entry.Key)
            .Select(entry => new MenuPreviewRequest(PreviewKind.Emote, entry.Key)).ToArray();
    }
}
