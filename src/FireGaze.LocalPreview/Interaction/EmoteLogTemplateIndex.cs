namespace FireGaze.LocalPreview.Interaction;

/// <summary>Matches exact, unformatted native LogMessage templates. Shared templates are never guessed.</summary>
public sealed class EmoteLogTemplateIndex
{
    private readonly Dictionary<string, HashSet<ushort>> mappings = new(StringComparer.Ordinal);
    public int UniqueCount => mappings.Count(pair => pair.Value.Count == 1);
    public int AmbiguousCount => mappings.Count(pair => pair.Value.Count > 1);
    public IReadOnlyList<ushort[]> ConflictEmoteGroups => mappings.Values.Where(ids => ids.Count > 1)
        .Select(ids => ids.Order().ToArray()).ToArray();

    public void Add(ushort emoteId, ReadOnlySpan<byte> template)
    {
        if (emoteId == 0 || template.IsEmpty) return;
        var key = Convert.ToBase64String(template);
        if (!mappings.TryGetValue(key, out var ids)) mappings[key] = ids = [];
        ids.Add(emoteId);
    }

    public void ExcludeNonEmoteTemplate(ReadOnlySpan<byte> template)
    {
        if (template.IsEmpty) return;
        if (mappings.TryGetValue(Convert.ToBase64String(template), out var ids)) ids.Add(0);
    }

    public bool TryResolve(ReadOnlySpan<byte> template, out ushort emoteId)
    {
        emoteId = 0;
        if (template.IsEmpty || !mappings.TryGetValue(Convert.ToBase64String(template), out var ids)
            || ids.Count != 1) return false;
        emoteId = ids.First();
        return true;
    }
}
