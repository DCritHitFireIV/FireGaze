namespace FireGaze.PortraitPreview;

internal static class PortraitLibraryPresentation
{
    internal readonly record struct TagCheckboxView(string VisibleText, string ID);

    /// <summary>
    /// Separates the user-facing tag text from ImGui's widget ID. The ID is derived with a
    /// deterministic ordinal hash, so reordering the tag list does not move a checkbox's state.
    /// </summary>
    public static TagCheckboxView TagCheckboxPresentation(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in tag)
            {
                hash ^= character;
                hash *= 16777619;
            }
            return new(tag, $"###PortraitFavoriteTag_{hash:X8}");
        }
    }

    public static IReadOnlyList<PortraitFavoriteEntry> Filter(IEnumerable<PortraitFavoriteEntry> entries,
        string search, IReadOnlyCollection<string> tags)
    {
        var needle = search.Trim();
        return entries.Where(entry => (tags.Count == 0 || entry.Tags.Any(tag => tags.Contains(tag)))
            && (needle.Length == 0 || entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.SourceName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.Tags.Any(tag => tag.Contains(needle, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(entry => entry.UpdatedAt).ThenBy(entry => entry.ID).ToArray();
    }

    // Facewear comes from unlocks rather than inventory containers, and has no dye channels.
    public static PortraitEquipmentQuery ForSlot(PortraitEquipmentQuery query, PortraitSlot slot)
        => slot == PortraitSlot.Facewear ? query with { Sources = [], DyeChannels = [] } : query;
}

internal sealed class PortraitSourceGuideState
{
    private readonly HashSet<string> shown = new(StringComparer.Ordinal);
    public bool ShouldShow(string character, bool hasUnread)
        => character.Length > 0 && hasUnread && shown.Add(character);
}
