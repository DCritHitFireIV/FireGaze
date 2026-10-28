using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FireGaze.Internal;
using FireGaze.Translate;

namespace FireGaze.UIText;

internal sealed class UITextRewritePlan
{
    public string ID { get; set; } = Guid.NewGuid().ToString("N");
    public bool Upgrade { get; set; }
    public bool IncludeGrey { get; set; }
    public string PackHash { get; set; } = string.Empty;
    public string DLLSignature { get; set; } = string.Empty;
    public string ChannelSignature { get; set; } = string.Empty;
    public string SourceSignature { get; set; } = string.Empty;
    public List<string> TargetIDs { get; set; } = [];
}

internal sealed class UITextRewriteSnapshot
{
    public string BeforeJSON { get; set; } = string.Empty;
    public string AfterHash { get; set; } = string.Empty;
    public string DLLSignature { get; set; } = string.Empty;
    public List<string> Completed { get; set; } = [];
    public bool Restored { get; set; }
    public string? SourceSignature { get; set; }
}

internal static class UITextRetranslation
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static void ValidatePlan(UITextRewritePlan plan, UITextRewriteSnapshot? snapshot, string json,
        string dll, string source, string channel)
    {
        if (snapshot?.Restored == true || Hash(json) != (snapshot?.AfterHash ?? plan.PackHash) || dll != (snapshot?.DLLSignature ?? plan.DLLSignature)
            || source != (snapshot?.SourceSignature ?? plan.SourceSignature) || channel != plan.ChannelSignature)
            throw new IOException("译文、插件、原始备份或通道在确认后发生变化，请重新确认；现有内容没有被覆盖。");
    }

    public static List<UITextTarget> SelectTargets(UITextPack pack, bool upgrade, bool includeGrey)
    {
        bool Eligible(string original, bool translated, string? source, string? providerText = null) => !UITextText.LooksUntranslatable(providerText ?? original)
            && !TranslationIndex.IsChinese(original) && !string.Equals(source, "user", StringComparison.OrdinalIgnoreCase)
            && (!upgrade || !translated || source?.Trim().ToLowerInvariant() is "library" or "ai:google"
                or "ai:mymemory" or "ai:caiyun" or "ai:public-caiyun" or "ai:deepl");
        var result = new List<UITextTarget>();
        foreach (var e in pack.Entries)
            if (!pack.IsSkipped(e.Original) && (e.Role == "UI" || includeGrey && e.Role == "Ambiguous")
                && Eligible(e.Original, e.HasTranslation, e.Source, e.ProviderText))
                result.Add(new(e.Original, e.Context, e, null));
        foreach (var e in pack.Resources)
            if (!pack.IsResourceSkipped(e.Container, e.Key) && Eligible(e.Original, e.HasTranslation, e.Source))
                result.Add(new(e.Original, "资源：" + e.Container + " · " + e.Key, null, e));
        foreach (var e in pack.Attributes)
            if (!pack.IsAttributeSkipped(e.Original) && Eligible(e.Original, e.HasTranslation, e.Source))
                result.Add(new(e.Original, e.Context, null, null, e));
        return result;
    }

    public static string TargetID(UITextTarget t) => JsonSerializer.Serialize(t.Resource is { } r
        ? new[] { "resource", r.Container, r.Key, r.Original }
        : new[] { t.Attribute is null ? "entry" : "attribute", t.Original });

    public static List<UITextTarget> RemainingTargets(UITextPack pack, bool upgrade, bool grey, string[] approved, string[] completed)
    {
        var allowed = approved.ToHashSet(StringComparer.Ordinal);
        allowed.ExceptWith(completed);
        return SelectTargets(pack, upgrade, grey).Where(t => allowed.Contains(TargetID(t))).ToList();
    }

    public static (int Applied, int PlaceholderRejected, int Unchanged) Accept(UITextPack pack,
        IReadOnlyList<UITextTarget> targets, IEnumerable<KeyValuePair<string, string>> values, string channel)
    {
        var receipt = AcceptWithReceipt(pack, targets, values, channel);
        return (receipt.Applied, receipt.PlaceholderRejected, receipt.Unchanged);
    }

    public static (int Applied, int PlaceholderRejected, int Unchanged, string[] CompletedTargetIDs) AcceptWithReceipt(UITextPack pack,
        IReadOnlyList<UITextTarget> targets, IEnumerable<KeyValuePair<string, string>> values, string channel)
    {
        // Shared text is sent once, but only the selected identities receive its result.
        var eligible = targets.Where(t => t.Entry is { IsUserSource: false } && !pack.IsSkipped(t.Original)
            || t.Resource is { IsUserSource: false } r && !pack.IsResourceSkipped(r.Container, r.Key)
            || t.Attribute is { IsUserSource: false } && !pack.IsAttributeSkipped(t.Original)).ToArray();
        var selected = new UITextPack
        {
            Entries = eligible.Where(t => t.Entry is not null).Select(t => t.Entry!).ToList(),
            Resources = eligible.Where(t => t.Resource is not null).Select(t => t.Resource!).ToList(),
            Attributes = eligible.Where(t => t.Attribute is not null).Select(t => t.Attribute!).ToList(),
        };
        var delivered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in values) delivered[pair.Key] = pair.Value;
        var safeValues = new List<KeyValuePair<string, string>>();
        var acceptedValues = new Dictionary<string, string>(StringComparer.Ordinal);
        var providerTexts = eligible.GroupBy(target => target.Original, StringComparer.Ordinal)
            .Where(group => group.Select(target => target.TranslationText).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().TranslationText, StringComparer.Ordinal);
        var rejected = 0;
        foreach (var pair in delivered)
        {
            var clean = UITextText.CleanTranslated(pair.Value);
            var providerText = providerTexts.GetValueOrDefault(pair.Key, UITextText.ForTranslation(pair.Key));
            if (!string.IsNullOrWhiteSpace(clean) && clean != providerText
                && UITextText.CheckPlaceholders(providerText, clean) is not null)
                rejected += selected.Entries.Count(e => e.Original == pair.Key)
                    + selected.Resources.Count(e => e.Original == pair.Key) + selected.Attributes.Count(e => e.Original == pair.Key);
            else
            {
                safeValues.Add(pair);
                if (!string.IsNullOrWhiteSpace(clean) && clean != providerText)
                    acceptedValues[pair.Key] = clean;
            }
        }
        var result = UITextFlow.AcceptTranslations(selected, safeValues, channel, overwriteExisting: true,
            overwriteUser: false, providerTexts: providerTexts);
        // Receiving a value is not completion. Rejected, blank or unchanged
        // replies must remain available to the next real resume operation.
        var completed = eligible.GroupBy(TargetID, StringComparer.Ordinal).Where(group => group.All(target =>
        {
            if (!acceptedValues.TryGetValue(target.Original, out var accepted)) return false;
            return target.Entry is { } entry ? entry.Translated == accepted && entry.Source == "ai:" + channel
                : target.Resource is { } resource ? resource.Translated == accepted && resource.Source == "ai:" + channel
                : target.Attribute is { } attribute && attribute.Translated == accepted && attribute.Source == "ai:" + channel;
        })).Select(group => group.Key).ToArray();
        return (result.Applied, result.PlaceholderRejected + rejected, result.Unchanged, completed);
    }

    private static string SnapshotPath(string directory, string name, string id)
    {
        if (Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Guid.TryParse(id, out _))
            throw new IOException("重翻备份名称无效。");
        return Path.Combine(directory, name + "-" + id + ".json");
    }

    public static void SaveSnapshot(string directory, string name, string id, string currentJSON, string dllSignature, string[] completed)
        => SaveSnapshotCore(directory, name, id, currentJSON, dllSignature, null, completed);

    public static void SealSnapshot(string directory, string name, string id, string currentJSON, string dllSignature, string sourceSignature, string[] completed)
        => SaveSnapshotCore(directory, name, id, currentJSON, dllSignature, sourceSignature, completed);

    private static void SaveSnapshotCore(string directory, string name, string id, string currentJSON, string dllSignature, string? sourceSignature, string[] completed)
    {
        Directory.CreateDirectory(directory);
        var path = SnapshotPath(directory, name, id);
        var snapshot = File.Exists(path) ? JsonSerializer.Deserialize<UITextRewriteSnapshot>(File.ReadAllText(path))!
            : new UITextRewriteSnapshot { BeforeJSON = currentJSON };
        snapshot.AfterHash = Hash(currentJSON);
        snapshot.DLLSignature = dllSignature;
        if (sourceSignature is not null) snapshot.SourceSignature = sourceSignature;
        snapshot.Completed = completed.ToList();
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(snapshot));
    }

    public static UITextRewriteSnapshot ReadSnapshot(string directory, string name, string id, string currentJSON, string dllSignature)
    {
        var snapshot = JsonSerializer.Deserialize<UITextRewriteSnapshot>(File.ReadAllText(SnapshotPath(directory, name, id)))
            ?? throw new IOException("重翻备份无法读取。");
        if (snapshot.Restored || snapshot.AfterHash != Hash(currentJSON) || snapshot.DLLSignature != dllSignature)
            throw new IOException("译文或插件在确认后发生变化，请重新确认；未覆盖现有内容。");
        return snapshot;
    }

    public static void MarkRestored(string directory, string name, string id)
    {
        var path = SnapshotPath(directory, name, id);
        var snapshot = JsonSerializer.Deserialize<UITextRewriteSnapshot>(File.ReadAllText(path))
            ?? throw new IOException("重翻备份无法读取。");
        snapshot.Restored = true;
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(snapshot));
    }

    public static bool HasSnapshot(string directory, string name, string id) => File.Exists(SnapshotPath(directory, name, id));
    public static string? LatestSnapshot(string directory, string name) => Directory.Exists(directory)
        ? Directory.GetFiles(directory, name + "-*.json").OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(p => Path.GetFileNameWithoutExtension(p)[(name.Length + 1)..]).FirstOrDefault() : null;
}
