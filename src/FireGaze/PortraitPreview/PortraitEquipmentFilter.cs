namespace FireGaze.PortraitPreview;

internal sealed record PortraitBrowserItem(
    uint ID,
    string Name,
    uint Icon,
    byte DyeChannels,
    bool Available,
    string Reason,
    uint ItemLevel = 0,
    uint RequiredLevel = 0,
    OwnedSourceKind[]? Sources = null)
{
    public OwnedSourceKind[] Sources { get; init; } = Sources ?? [];
}

internal enum PortraitEquipmentSort
{
    Name,
    ItemLevelAscending,
    ItemLevelDescending,
    IDAscending,
    IDDescending,
    RequiredLevelAscending,
    RequiredLevelDescending,
}

internal sealed record PortraitEquipmentQuery(
    string Search = "",
    IReadOnlyCollection<OwnedSourceKind>? Sources = null,
    IReadOnlyCollection<byte>? DyeChannels = null,
    PortraitEquipmentSort Sort = PortraitEquipmentSort.Name);

/// <summary>
/// Empty source or dye selections leave that group unrestricted. Selected values use OR;
/// source, dye and search groups use AND. Duplicate IDs retain the first metadata and merge sources.
/// </summary>
internal static class PortraitEquipmentFilter
{
    public static IReadOnlyList<PortraitBrowserItem> Apply(IEnumerable<PortraitBrowserItem> items, PortraitEquipmentQuery? query = null)
    {
        query ??= new();
        var sources = query.Sources is { Count: > 0 } ? query.Sources.ToHashSet() : null;
        var dyeChannels = query.DyeChannels is { Count: > 0 } ? query.DyeChannels.ToHashSet() : null;
        var search = (query.Search ?? "").Trim();
        var filtered = items.Where(item => item.Sources.Length == 0 || item.Sources.Any(OwnedSourcePolicy.IsSupported))
            .GroupBy(item => item.ID)
            .Select(group => group.First() with { Sources = group.SelectMany(item => item.Sources).Where(OwnedSourcePolicy.IsSupported).Distinct().ToArray() })
            .Where(item => (sources is null || item.Sources.Any(sources.Contains))
                && (dyeChannels is null || dyeChannels.Contains(item.DyeChannels))
                && (search.Length == 0 || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));
        return query.Sort switch
        {
            PortraitEquipmentSort.ItemLevelAscending => filtered.OrderBy(item => item.ItemLevel)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.ID).ToArray(),
            PortraitEquipmentSort.ItemLevelDescending => filtered.OrderByDescending(item => item.ItemLevel)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.ID).ToArray(),
            PortraitEquipmentSort.RequiredLevelAscending => filtered.OrderBy(item => item.RequiredLevel)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.ID).ToArray(),
            PortraitEquipmentSort.RequiredLevelDescending => filtered.OrderByDescending(item => item.RequiredLevel)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.ID).ToArray(),
            PortraitEquipmentSort.IDAscending => filtered.OrderBy(item => item.ID).ToArray(),
            PortraitEquipmentSort.IDDescending => filtered.OrderByDescending(item => item.ID).ToArray(),
            _ => filtered.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.ID).ToArray(),
        };
    }
}
