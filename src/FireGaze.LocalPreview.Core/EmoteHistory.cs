namespace FireGaze.LocalPreview.Core;

/// <param name="InstanceId">Host supplied actor generation or instance identity, never display name.</param>
public readonly record struct ActorIdentity(ulong ObjectId, ulong InstanceId);
public readonly record struct EmoteReference(uint TerritoryId, ActorIdentity Actor, uint EmoteId);

public sealed class EmoteHistory
{
    private readonly Dictionary<ActorIdentity, EmoteReference> latest = [];
    private uint territory;

    /// <summary>Call for every territory boundary, including a new instance with the same row id.</summary>
    public void ChangeTerritory(uint territoryId)
    {
        territory = territoryId;
        latest.Clear();
    }

    public EmoteReference Record(ActorIdentity actor, uint emoteId)
    {
        var reference = BindChat(actor, emoteId);
        latest[actor] = reference;
        return reference;
    }

    public bool TryGet(ActorIdentity actor, out EmoteReference reference) => latest.TryGetValue(actor, out reference);

    /// <summary>Bind the emote id observed for this message, never a later lookup of the actor's last emote.</summary>
    public EmoteReference BindChat(ActorIdentity actor, uint emoteId)
    {
        if (emoteId == 0) throw new ArgumentOutOfRangeException(nameof(emoteId));
        return new(territory, actor, emoteId);
    }

    public void Clear() => latest.Clear();
}
