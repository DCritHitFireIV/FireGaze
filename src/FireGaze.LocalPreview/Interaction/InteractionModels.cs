namespace FireGaze.LocalPreview.Interaction;

public enum PreviewKind { Mount, Minion, Accessory, Emote }

public readonly record struct MenuPreviewRequest(PreviewKind Kind, uint Id);

public readonly record struct ObjectPreviewSnapshot(ushort MountId, ushort MinionId, ushort AccessoryId);

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
    // Action row IDs, not ItemAction row IDs. Collection ID is ItemAction.Data[0].
    public static MenuPreviewRequest? Resolve(uint itemActionId, uint collectionId)
    {
        if (collectionId == 0 || collectionId > ushort.MaxValue) return null;
        return itemActionId switch
        {
            1322 => new(PreviewKind.Mount, collectionId),
            853 => new(PreviewKind.Minion, collectionId),
            20086 or 37312 => new(PreviewKind.Accessory, collectionId),
            _ => null,
        };
    }
}
