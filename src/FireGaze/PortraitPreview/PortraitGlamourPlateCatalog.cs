using System.Text.Json;
using System.Text.Json.Serialization;

namespace FireGaze.PortraitPreview;

/// <summary>Framework-owned saved-template observations, isolated by stable character content identity.</summary>
internal sealed class PortraitGlamourPlateCatalog
{
    private static readonly TimeSpan CurrentFor = TimeSpan.FromMinutes(15);
    internal const int MaximumFileBytes = 4 * 1024 * 1024;
    private Dictionary<ulong, Snapshot> characters = [];
    private OwnedCharacterKey? active;

    public long ContentRevision { get; private set; }

    public void ActivateCharacter(OwnedCharacterKey character)
    {
        if (character.AccountID == 0 || character.ContentID == 0) throw new ArgumentException("Both character identities are required.", nameof(character));
        if (active == character) return;
        active = character;
        if (!characters.TryGetValue(character.ContentID, out var snapshot)) characters[character.ContentID] = snapshot = new();
        snapshot.Unavailable = true;
        snapshot.Reason = snapshot.ReadAt is null ? "尚未读取投影模板" : "已读取投影模板，尚未在本次登录刷新";
    }

    public void DeactivateCharacter() => active = null;

    public bool MarkUnavailable(OwnedCharacterKey character, string reason)
    {
        if (active != character) return false;
        var snapshot = characters[character.ContentID];
        snapshot.Unavailable = true;
        snapshot.Reason = string.IsNullOrWhiteSpace(reason) ? "投影模板当前不可读取" : reason;
        return true;
    }

    public bool ApplyRead(OwnedCharacterKey character, DateTimeOffset readAt, IReadOnlyList<PortraitGlamourPlate> plates)
    {
        if (active != character || !TryCopyComplete(plates, out var copied)) return false;
        var prior = characters[character.ContentID];
        if (prior.ReadAt > readAt || prior.ReadAt == readAt && !Equal(prior.Plates, copied)) return false;
        var changed = prior.ReadAt is null || !Equal(prior.Plates, copied);
        var revision = changed ? checked(ContentRevision + 1) : ContentRevision;
        characters[character.ContentID] = new() { ReadAt = readAt, Plates = copied, Reason = "已读取投影模板" };
        ContentRevision = revision;
        return true;
    }

    public PortraitGlamourPlateView BuildView(DateTimeOffset now)
    {
        if (active is not { } character) return new(null, [], OwnedReadStatus.Unread, null, "尚未进入角色");
        var snapshot = characters[character.ContentID];
        var status = snapshot.ReadAt is not { } readAt ? OwnedReadStatus.Unread
            : snapshot.Unavailable || now < readAt || now - readAt > CurrentFor ? OwnedReadStatus.PossiblyStale : OwnedReadStatus.Current;
        var reason = status == OwnedReadStatus.PossiblyStale && !snapshot.Unavailable ? "已读取投影模板可能过期" : snapshot.Reason;
        return new(character, Array.AsReadOnly(snapshot.Plates.Select(PortraitGlamourPlateMerge.Copy).ToArray()), status, snapshot.ReadAt, PortraitGlamourPlateGuidance.ForPlayer(reason));
    }

    public string ExportJSON() => JsonSerializer.Serialize(new Document
    {
        Version = 1,
        Characters = characters.OrderBy(pair => pair.Key).Select(pair => new OwnerDocument
        {
            ContentID = pair.Key, ReadAt = pair.Value.ReadAt,
            Plates = pair.Value.Plates.Select(PortraitGlamourPlateMerge.Copy).ToArray(),
        }).ToList(),
    });

    /// <summary>Invalid storage leaves every current observation intact.</summary>
    public bool ImportJSON(string json)
    {
        if (json is null || json.Length > MaximumFileBytes) return false;
        try
        {
            var document = JsonSerializer.Deserialize<Document>(json);
            if (document is not { Version: 1, Characters: not null } || document.Characters.Count > 64) return false;
            var replacement = new Dictionary<ulong, Snapshot>();
            foreach (var owner in document.Characters)
            {
                if (owner is null || owner.ContentID == 0 || owner.Plates is null) return false;
                PortraitGlamourPlate[] copied;
                if (owner.ReadAt is null) { if (owner.Plates.Length != 0) return false; copied = []; }
                else if (!TryCopyComplete(owner.Plates, out copied)) return false;
                if (!replacement.TryAdd(owner.ContentID, new()
                {
                    ReadAt = owner.ReadAt, Plates = copied, Unavailable = true,
                    Reason = owner.ReadAt is null ? "尚未读取投影模板" : "已读取投影模板，尚未在本次登录刷新",
                })) return false;
            }
            if (active is { } character && !replacement.ContainsKey(character.ContentID)) replacement.Add(character.ContentID, new());
            characters = replacement;
            return true;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException) { return false; }
    }

    private static bool TryCopyComplete(IReadOnlyList<PortraitGlamourPlate>? source, out PortraitGlamourPlate[] copied)
    {
        copied = [];
        if (source is null || source.Count != 20) return false;
        var result = new PortraitGlamourPlate[20];
        foreach (var plate in source)
        {
            if (!PortraitGlamourPlateMerge.IsValid(plate) || result[plate.Index] is not null) return false;
            result[plate.Index] = PortraitGlamourPlateMerge.Copy(plate);
        }
        copied = result;
        return true;
    }

    private static bool Equal(PortraitGlamourPlate[] first, PortraitGlamourPlate[] second)
        => first.Length == second.Length && first.Zip(second).All(pair => pair.First.Index == pair.Second.Index && pair.First.Pieces.SequenceEqual(pair.Second.Pieces));

    private sealed class Snapshot
    {
        public DateTimeOffset? ReadAt { get; init; }
        public PortraitGlamourPlate[] Plates { get; init; } = [];
        public bool Unavailable { get; set; }
        public string Reason { get; set; } = "尚未读取投影模板";
    }
    private sealed class Document
    {
        [JsonRequired] public int Version { get; set; }
        [JsonRequired] public List<OwnerDocument> Characters { get; set; } = [];
    }
    private sealed class OwnerDocument
    {
        [JsonRequired] public ulong ContentID { get; set; }
        [JsonRequired] public DateTimeOffset? ReadAt { get; set; }
        [JsonRequired] public PortraitGlamourPlate[] Plates { get; set; } = [];
    }
}
