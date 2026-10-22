namespace FireGaze.PortraitPreview;

internal sealed record PortraitEquipmentSourceChoice(
    OwnedSourceKind Kind,
    bool CanSelect,
    bool HasUnread,
    bool HasCached,
    int OwnedItemCount,
    int ReadSourceCount = 0,
    int? TotalSourceCount = null,
    int CurrentSourceCount = 0,
    int CachedSourceCount = 0,
    bool RosterMayBeStale = false,
    string StatusText = "");

/// <summary>Summarizes only the active character's supplied source views. Reading an empty container is still a successful read.</summary>
internal static class PortraitEquipmentFilterSources
{
    public static IReadOnlyList<PortraitEquipmentSourceChoice> Build(IEnumerable<OwnedSourceView> sources, OwnedCharacterKey? character = null, OwnedRetainerRoster? roster = null)
    {
        var byKind = sources.ToLookup(source => source.Source.Kind);
        return OwnedSourcePolicy.SupportedKinds.Select(kind =>
        {
            var observations = byKind[kind].GroupBy(source => source.Source)
                .Select(group => group.OrderByDescending(source => source.ReadAt).First()).ToArray();
            if (kind == OwnedSourceKind.RetainerInventory) return RetainerChoice(observations, character, roster);
            var read = observations.Where(source => source.ReadAt is not null).ToArray();
            return new PortraitEquipmentSourceChoice(kind,
                read.Length > 0,
                observations.Length == 0 || read.Length != observations.Length,
                read.Any(source => source.Status != OwnedReadStatus.Current),
                (int)Math.Min(int.MaxValue, read.Sum(source => (long)Math.Max(0, source.OwnedItemCount))));
        }).ToArray();
    }

    private static PortraitEquipmentSourceChoice RetainerChoice(OwnedSourceView[] observations, OwnedCharacterKey? character, OwnedRetainerRoster? roster)
    {
        var known = character is { } owner && roster is { Status: not OwnedReadStatus.Unread } && roster.Character == owner
            && roster.RetainerIDs.All(id => id != 0) && roster.RetainerIDs.Distinct().Count() == roster.RetainerIDs.Count;
        var rosterIDs = known ? roster!.RetainerIDs.ToHashSet() : null;
        var read = observations.Where(source => source.ReadAt is not null && source.Source.RetainerID != 0
            && (rosterIDs is null || rosterIDs.Contains(source.Source.RetainerID))).ToArray();
        var current = known ? read.Count(source => source.Status == OwnedReadStatus.Current) : 0;
        var cached = read.Length - current;
        var rosterStale = known && roster!.Status == OwnedReadStatus.PossiblyStale;
        var total = known ? (int?)rosterIDs!.Count : null;
        var text = total switch
        {
            null => "未读取雇员名单" + (read.Length > 0 ? " · 有已保存记录" : ""),
            0 => "未雇佣雇员",
            _ => $"已读取 {read.Length}/{total}" + (cached > 0 ? $" · 已保存 {cached}" : ""),
        };
        if (rosterStale) text += " · 名单待刷新";
        return new(OwnedSourceKind.RetainerInventory, read.Length > 0,
            total is null || read.Length < total, cached > 0,
            (int)Math.Min(int.MaxValue, read.Sum(source => (long)Math.Max(0, source.OwnedItemCount))),
            read.Length, total, current, cached, rosterStale, text);
    }
}
