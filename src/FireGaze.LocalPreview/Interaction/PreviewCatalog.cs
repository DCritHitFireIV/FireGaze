namespace FireGaze.LocalPreview.Interaction;

public sealed record PreviewCatalogEntry(PreviewKind Kind, uint Id, string Name);

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
}
