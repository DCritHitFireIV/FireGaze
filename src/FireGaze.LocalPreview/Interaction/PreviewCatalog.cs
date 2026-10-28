namespace FireGaze.LocalPreview.Interaction;

public enum MountCatalogView { Regular, Special }

public sealed record PreviewCatalogEntry(PreviewKind Kind, uint Id, string Name, uint Icon = 0)
{
    public MountCatalogView MountView { get; init; } = MountCatalogView.Regular;
    public uint MountRepresentativeId { get; init; } = Id;
    public bool HasAdmittedSpecialAction { get; init; }
}

public sealed class PreviewCatalog(IEnumerable<PreviewCatalogEntry> entries)
{
    private readonly PreviewCatalogEntry[] entries = entries
        .Where(e => Enum.IsDefined(e.Kind) && e.Id is > 0 and <= ushort.MaxValue && !string.IsNullOrWhiteSpace(e.Name))
        .Select(e => e with { Name = e.Name.Trim() }).OrderBy(e => e.Name, StringComparer.CurrentCulture).ToArray();

    public IReadOnlyList<PreviewCatalogEntry> Search(PreviewKind kind, string query)
    {
        query = query.Trim();
        return entries.Where(e => e.Kind == kind && (query.Length == 0
            || e.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || e.Id.ToString() == query)).ToArray();
    }

    public IReadOnlyList<PreviewCatalogEntry> SearchMounts(MountCatalogView view, string query)
    {
        if (!Enum.IsDefined(view)) return [];
        return Search(PreviewKind.Mount, query).Where(entry => entry.MountView == view
            && entry.MountRepresentativeId == entry.Id).ToArray();
    }
}
