namespace FireGaze.LocalPreview.Interaction;

public readonly record struct MountCatalogNameSource(MountCatalogIconSource Mount, string? Name);

/// <summary>Uses client names and unique exact-appearance names without changing a mount's identity.</summary>
public sealed class MountCatalogNames
{
    private readonly Dictionary<MountCatalogAppearance, string> uniqueNames;

    public MountCatalogNames(IEnumerable<MountCatalogNameSource> sources)
    {
        uniqueNames = sources.Select(source => (source, Name: PlayerFacingName(source.Name)))
            .Where(item => HasModel(item.source.Mount) && item.Name is not null)
            .Select(item => (item.source, Name: item.Name!))
            .GroupBy(item => MountCatalogAppearance.For(item.source.Mount))
            .Select(group => (Appearance: group.Key,
                Names: group.Select(source => source.Name).Distinct(StringComparer.Ordinal).Take(2).ToArray()))
            .Where(group => group.Names.Length == 1)
            .ToDictionary(group => group.Appearance, group => group.Names[0]);
    }

    public PreviewCatalogEntry? CreateEntry(MountCatalogNameSource source, uint icon)
    {
        if (!HasModel(source.Mount)) return null;
        var rawName = source.Name?.Trim();
        var name = PlayerFacingName(rawName)
            ?? uniqueNames.GetValueOrDefault(MountCatalogAppearance.For(source.Mount));
        // Some client rows expose an internal model code instead of a localized
        // player name. Keep that code only as a secondary, searchable hint so the
        // card is visibly unnamed without leaking a raw numeric id to players.
        if (string.IsNullOrEmpty(name))
            name = IsInternalCodeName(rawName ?? string.Empty)
                ? $"未命名坐骑 · {rawName}"
                : "未命名坐骑";
        return new(PreviewKind.Mount, source.Mount.Id, name, icon);
    }

    private static string? PlayerFacingName(string? value)
    {
        var name = value?.Trim();
        return string.IsNullOrEmpty(name) || IsInternalCodeName(name) ? null : name;
    }

    private static bool IsInternalCodeName(string value)
    {
        var prefix = value.StartsWith("CAM-", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("CHL ", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("CHL-", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("CPD-", StringComparison.OrdinalIgnoreCase);
        return prefix && value.All(character => character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or ' ' or '-');
    }

    private static bool HasModel(MountCatalogIconSource source)
        => source.Id is > 0 and <= ushort.MaxValue && source.ModelChara > 0;
}
