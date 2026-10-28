namespace FireGaze.LocalPreview.Interaction;

public readonly record struct MountCatalogIconSource(uint Id, uint Icon, uint ModelChara,
    int EquipHead = 0, int EquipBody = 0, int EquipLeg = 0, int EquipFoot = 0, uint Customize = 0);

internal readonly record struct MountCatalogAppearance(uint ModelChara, int Head, int Body, int Leg, int Foot, uint Customize)
{
    public static MountCatalogAppearance For(MountCatalogIconSource source)
        => new(source.ModelChara, source.EquipHead, source.EquipBody, source.EquipLeg, source.EquipFoot, source.Customize);
}

/// <summary>Resolves official icons for mounts without a collection icon.</summary>
public sealed class MountCatalogIcons
{
    private readonly Dictionary<MountCatalogAppearance, uint> uniqueIcons;

    public MountCatalogIcons(IEnumerable<MountCatalogIconSource> sources)
    {
        uniqueIcons = sources.Where(source => source.Icon > 0 && source.ModelChara > 0)
            .GroupBy(MountCatalogAppearance.For)
            .Select(group => (Appearance: group.Key, Icons: group.Select(source => source.Icon).Distinct().Take(2).ToArray()))
            .Where(group => group.Icons.Length == 1)
            .ToDictionary(group => group.Appearance, group => group.Icons[0]);
    }

    public uint Resolve(MountCatalogIconSource source)
    {
        if (source.Icon > 0) return source.Icon;
        if (source.ModelChara > 0 && uniqueIcons.TryGetValue(MountCatalogAppearance.For(source), out var icon)) return icon;
        // The mount-guide category image does not depict this mount. Preserve
        // the missing-image state for the catalog's identifiable neutral card.
        return 0;
    }

}
