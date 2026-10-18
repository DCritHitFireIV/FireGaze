namespace FireGaze.PortraitPreview;

internal sealed record PortraitEquipmentSourceChoice(
    OwnedSourceKind Kind,
    bool CanSelect,
    bool HasUnread,
    bool HasCached,
    int OwnedItemCount);

/// <summary>Summarizes only the active character's supplied source views. Reading an empty container is still a successful read.</summary>
internal static class PortraitEquipmentFilterSources
{
    public static IReadOnlyList<PortraitEquipmentSourceChoice> Build(IEnumerable<OwnedSourceView> sources)
    {
        var byKind = sources.ToLookup(source => source.Source.Kind);
        return Enum.GetValues<OwnedSourceKind>().Select(kind =>
        {
            var observations = byKind[kind].ToArray();
            var read = observations.Where(source => source.ReadAt is not null).ToArray();
            return new PortraitEquipmentSourceChoice(kind,
                read.Length > 0,
                observations.Length == 0 || read.Length != observations.Length,
                read.Any(source => source.Status != OwnedReadStatus.Current),
                (int)Math.Min(int.MaxValue, read.Sum(source => (long)Math.Max(0, source.OwnedItemCount))));
        }).ToArray();
    }
}
