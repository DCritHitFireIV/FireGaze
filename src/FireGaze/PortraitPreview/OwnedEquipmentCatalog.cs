using System.Text.Json;
using System.Text.Json.Serialization;

namespace FireGaze.PortraitPreview;

/// <summary>Framework-thread owned cache. A failed/unloaded read never means an empty source.</summary>
internal sealed class OwnedEquipmentCatalog
{
    private const int CacheVersion = 2;
    private static readonly TimeSpan CurrentFor = TimeSpan.FromMinutes(15);
    private readonly Func<uint, uint?> resolveOwnedEquipmentID;
    // AccountID can change between sessions for the same character. It is still required
    // in active/callback guards, but is not a durable cache identity.
    private Dictionary<ulong, Dictionary<OwnedSourceKey, SourceSnapshot>> characters = [];
    private OwnedCharacterKey? active;

    public OwnedEquipmentCatalog(Func<uint, uint?> resolveOwnedEquipmentID)
    {
        this.resolveOwnedEquipmentID = resolveOwnedEquipmentID ?? throw new ArgumentNullException(nameof(resolveOwnedEquipmentID));
    }

    public void ActivateCharacter(OwnedCharacterKey key)
    {
        ValidateCharacter(key);
        if (active == key) return;
        active = key;
        if (!characters.TryGetValue(key.ContentID, out var sources)) characters[key.ContentID] = sources = [];
        foreach (var snapshot in sources.Values)
            if (snapshot.ReadAt is not null) { snapshot.Unavailable = true; snapshot.Reason = "已读取记录，重新登录后尚未刷新"; }
    }

    public void DeactivateCharacter() => active = null;

    public void RegisterSource(OwnedSourceKey source)
    {
        ValidateSource(source);
        var sources = ActiveSources();
        if (!sources.ContainsKey(source)) sources[source] = new();
    }

    /// <summary>Only call for a complete, successfully loaded source snapshot; empty is then authoritative.</summary>
    public bool ApplyRead(OwnedCharacterKey character, OwnedSourceKey source, DateTimeOffset readAt, IEnumerable<OwnedItemRecord> records)
    {
        if (active != character) return false;
        ValidateCharacter(character);
        ValidateSource(source);
        var sources = ActiveSources();
        if (sources.TryGetValue(source, out var prior) && prior.ReadAt > readAt) return false;
        var equipment = Normalize(records);
        if (prior?.ReadAt == readAt && !prior.Items.SequenceEqual(equipment)) return false;
        sources[source] = new() { ReadAt = readAt, Items = equipment, Reason = "已读取记录" };
        return true;
    }

    public bool MarkUnavailable(OwnedCharacterKey character, OwnedSourceKey source, string reason)
    {
        if (active != character) return false;
        ValidateSource(source);
        RegisterSource(source);
        var snapshot = ActiveSources()[source];
        snapshot.Unavailable = true;
        snapshot.Reason = string.IsNullOrWhiteSpace(reason) ? "来源尚未读取或当前不可用" : reason;
        return true;
    }

    public OwnedEquipmentView BuildView(DateTimeOffset now)
    {
        if (active is not { } key) return new(null, [], []);
        var entries = new Dictionary<uint, List<OwnedItemVariant>>();
        var sourceViews = new List<OwnedSourceView>();
        foreach (var (source, snapshot) in ActiveSources().OrderBy(s => s.Key.Kind).ThenBy(s => s.Key.RetainerID))
        {
            var status = Status(snapshot, now);
            var reason = snapshot.Reason;
            if (snapshot.ReadAt is not null && status == OwnedReadStatus.PossiblyStale && !snapshot.Unavailable)
                reason = "已读取记录可能过期";
            sourceViews.Add(new(source, status, snapshot.ReadAt, snapshot.Items.Count, reason));
            foreach (var item in snapshot.Items)
            {
                if (!entries.TryGetValue(item.BaseItemID, out var variants)) entries[item.BaseItemID] = variants = [];
                var raw = item.Record;
                variants.Add(new(source, raw.ItemID, raw.ContainerID, raw.Slot, raw.Quantity, raw.Stain1, raw.Stain2, raw.GlamourID, raw.HighQuality, status));
            }
        }
        return new(key,
            entries.OrderBy(e => e.Key).Select(e => new OwnedEquipmentEntry(e.Key, e.Value.AsReadOnly())).ToList().AsReadOnly(),
            sourceViews.AsReadOnly());
    }

    public string ExportJSON()
        => Serialize(characters);

    private static string Serialize(Dictionary<ulong, Dictionary<OwnedSourceKey, SourceSnapshot>> records)
    {
        var document = new CacheDocument { Version = CacheVersion };
        foreach (var (contentID, sources) in records.OrderBy(c => c.Key))
        {
            var owner = new CharacterDocument { ContentID = contentID };
            foreach (var (source, snapshot) in sources.OrderBy(s => s.Key.Kind).ThenBy(s => s.Key.RetainerID))
                owner.Sources.Add(new() { Source = source, ReadAt = snapshot.ReadAt, Items = snapshot.Items.Select(i => i.Record).ToList() });
            document.Characters.Add(owner);
        }
        return JsonSerializer.Serialize(document);
    }

    /// <summary>Parse and validate into a replacement map first. Bad cache input cannot erase live observations.</summary>
    public bool ImportJSON(string jsonText)
    {
        if (!TryReadJSON(jsonText, out var replacement, out _)) return false;
        characters = replacement;
        if (active is { } key && !characters.ContainsKey(key.ContentID)) characters[key.ContentID] = [];
        return true;
    }

    /// <summary>Validate without changing live observations, so disk migration can commit first.</summary>
    internal bool PrepareImportJSON(string jsonText, out string currentJSON, out bool legacy)
    {
        currentJSON = string.Empty;
        if (!TryReadJSON(jsonText, out var replacement, out legacy)) return false;
        currentJSON = Serialize(replacement);
        return true;
    }

    private bool TryReadJSON(string jsonText, out Dictionary<ulong, Dictionary<OwnedSourceKey, SourceSnapshot>> replacement, out bool legacy)
    {
        replacement = [];
        legacy = false;
        if (jsonText is null || jsonText.Length > 16_000_000) return false;
        try
        {
            using var envelope = JsonDocument.Parse(jsonText);
            if (envelope.RootElement.ValueKind != JsonValueKind.Object
                || !envelope.RootElement.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema)
                || !envelope.RootElement.TryGetProperty("Characters", out var ownerArray) || ownerArray.ValueKind != JsonValueKind.Array) return false;
            if (schema is not (1 or CacheVersion)) return false;
            legacy = schema == 1;
            var owners = legacy
                ? JsonSerializer.Deserialize<LegacyCacheDocument>(jsonText)?.Characters?.Select(owner => owner is null ? null : new ImportOwner(owner.Character.ContentID, owner.Character, owner.Sources)).ToList()
                : JsonSerializer.Deserialize<CacheDocument>(jsonText)?.Characters?.Select(owner => owner is null ? null : new ImportOwner(owner.ContentID, null, owner.Sources)).ToList();
            if (owners is null || owners.Count > 64) return false;
            var legacyOwners = new HashSet<OwnedCharacterKey>();
            var conflicts = new HashSet<(ulong ContentID, OwnedSourceKey Source)>();
            foreach (var owner in owners)
            {
                if (owner is null || owner.Sources is null || owner.Sources.Count > 512) return false;
                if (owner.ContentID == 0) return false;
                if (owner.LegacyCharacter is { } character)
                {
                    ValidateCharacter(character);
                    if (!legacyOwners.Add(character)) return false;
                }
                var sources = new Dictionary<OwnedSourceKey, SourceSnapshot>();
                foreach (var source in owner.Sources)
                {
                    if (source is null || source.Items is null || (source.ReadAt is null && source.Items.Count != 0)) return false;
                    ValidateSource(source.Source);
                    if (!sources.TryAdd(source.Source, new()
                    {
                        ReadAt = source.ReadAt,
                        Items = Normalize(source.Items),
                        Unavailable = true,
                        Reason = source.ReadAt is null ? "尚未读取" : "已读取记录，尚未在本次登录刷新",
                    })) return false;
                }
                if (!replacement.TryGetValue(owner.ContentID, out var existing)) replacement.Add(owner.ContentID, sources);
                else
                {
                    if (!legacy) return false;
                    foreach (var (source, candidate) in sources)
                    {
                        if (!existing.TryGetValue(source, out var prior) || candidate.ReadAt is not null && (prior.ReadAt is null || candidate.ReadAt > prior.ReadAt))
                        {
                            existing[source] = candidate;
                            conflicts.Remove((owner.ContentID, source));
                        }
                        else if (candidate.ReadAt == prior.ReadAt && !candidate.Items.SequenceEqual(prior.Items))
                            conflicts.Add((owner.ContentID, source));
                    }
                    if (existing.Count > 512) return false;
                }
            }
            return conflicts.Count == 0;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private List<NormalizedItem> Normalize(IEnumerable<OwnedItemRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var equipment = new List<NormalizedItem>();
        var slots = new HashSet<(uint Container, int Slot)>();
        var count = 0;
        foreach (var item in records)
        {
            if (++count > 20_000 || item is null || item.Slot < 0 || item.Slot > 100_000 || !slots.Add((item.ContainerID, item.Slot)))
                throw new ArgumentException("Invalid or duplicate physical inventory location.", nameof(records));
            if (item.ItemID == 0 || item.Quantity == 0) continue;
            var itemID = resolveOwnedEquipmentID(item.ItemID);
            if (itemID is not > 0) continue;
            equipment.Add(new(itemID.Value, item));
        }
        return equipment.OrderBy(i => i.Record.ContainerID).ThenBy(i => i.Record.Slot).ToList();
    }

    private Dictionary<OwnedSourceKey, SourceSnapshot> ActiveSources()
    {
        if (active is not { } key) throw new InvalidOperationException("No character is active.");
        return characters[key.ContentID];
    }

    private static OwnedReadStatus Status(SourceSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.ReadAt is not { } readAt) return OwnedReadStatus.Unread;
        return snapshot.Unavailable || now < readAt || now - readAt > CurrentFor ? OwnedReadStatus.PossiblyStale : OwnedReadStatus.Current;
    }

    private static void ValidateCharacter(OwnedCharacterKey key)
    {
        if (key.AccountID == 0 || key.ContentID == 0) throw new ArgumentException("Both account and content identities are required.", nameof(key));
    }

    private static void ValidateSource(OwnedSourceKey key)
    {
        var retainer = key.Kind is OwnedSourceKind.RetainerInventory or OwnedSourceKind.RetainerEquipped;
        if (!Enum.IsDefined(key.Kind) || retainer != (key.RetainerID != 0))
            throw new ArgumentException("Invalid inventory source or retainer identity.", nameof(key));
    }

    private sealed record NormalizedItem(uint BaseItemID, OwnedItemRecord Record);
    private sealed class SourceSnapshot
    {
        public DateTimeOffset? ReadAt { get; set; }
        public List<NormalizedItem> Items { get; set; } = [];
        public bool Unavailable { get; set; }
        public string Reason { get; set; } = "尚未读取";
    }
    private sealed class CacheDocument
    {
        public int Version { get; set; }
        [JsonRequired]
        public List<CharacterDocument> Characters { get; set; } = [];
    }
    private sealed class CharacterDocument
    {
        [JsonRequired]
        public ulong ContentID { get; set; }
        [JsonRequired]
        public List<SourceDocument> Sources { get; set; } = [];
    }
    private sealed class LegacyCacheDocument
    {
        [JsonRequired]
        public List<LegacyCharacterDocument> Characters { get; set; } = [];
    }
    private sealed class LegacyCharacterDocument
    {
        [JsonRequired]
        public OwnedCharacterKey Character { get; set; }
        [JsonRequired]
        public List<SourceDocument> Sources { get; set; } = [];
    }
    private sealed record ImportOwner(ulong ContentID, OwnedCharacterKey? LegacyCharacter, List<SourceDocument>? Sources);
    private sealed class SourceDocument
    {
        [JsonRequired]
        public OwnedSourceKey Source { get; set; }
        [JsonRequired]
        public DateTimeOffset? ReadAt { get; set; }
        [JsonRequired]
        public List<OwnedItemRecord> Items { get; set; } = [];
    }
}
