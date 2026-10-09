namespace FireGaze.UIText;

internal enum UITextDiagnosticKind { Untranslated, KeptOriginal, Excluded, NeedsReview, ReadyToApply, AlreadyTranslated }
internal sealed record UITextDiagnostic(string Original, string? Context, string Translation, UITextDiagnosticKind Kind, string Reason);

internal static class UITextMaintenance
{
    public static bool CanApplyUpdate(UITextFlow.MergePreview preview, string? warning) => warning is null
        && (preview.Added > 0 || preview.Overwritten > 0);

    public static string FileSignature(IEnumerable<string> paths) => string.Join("\n", paths.Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(p => p + ":" + UITextPatchStore.HashOf(p)));

    public static string AppliedLabel(int translated, int available, bool expanded) => !expanded
        ? "已汉化"
        : available > translated ? $"已汉化 {translated}/{available} 条" : $"已汉化 {translated} 条";

    public static int NewCandidates(UITextPack pack, UITextExtraction extraction)
    {
        var originals = extraction.Entries.Where(e => e.Role == UITextRole.UI
            && !UITextText.LooksUntranslatable(e.Original) && !pack.IsSkipped(e.Original) && pack.Find(e.Original) is null)
            .Select(e => e.Original).Distinct(StringComparer.Ordinal).Count();
        return originals
            + extraction.Resources.Count(e => pack.FindResource(e.Container, e.Key) is null && !pack.IsResourceSkipped(e.Container, e.Key) && !UITextText.LooksUntranslatable(e.Value))
            + extraction.Attributes.Count(e => pack.FindAttribute(e.Value) is null && !pack.IsAttributeSkipped(e.Value) && !UITextText.LooksUntranslatable(e.Value));
    }

    public static HashSet<string> NewOriginals(UITextPack pack, UITextExtraction extraction) => extraction.Entries
        .Where(e => e.Role == UITextRole.UI && pack.Find(e.Original) is null && !pack.IsSkipped(e.Original) && !UITextText.LooksUntranslatable(e.Original))
        .Select(e => e.Original)
        .Concat(extraction.Resources.Where(e => pack.FindResource(e.Container, e.Key) is null && !pack.IsResourceSkipped(e.Container, e.Key) && !UITextText.LooksUntranslatable(e.Value)).Select(e => e.Value))
        .Concat(extraction.Attributes.Where(e => pack.FindAttribute(e.Value) is null && !pack.IsAttributeSkipped(e.Value) && !UITextText.LooksUntranslatable(e.Value)).Select(e => e.Value))
        .ToHashSet(StringComparer.Ordinal);

    public static List<UITextDiagnostic> Diagnose(UITextPack pack, UITextExtraction extraction, string query, bool applied, bool dirty)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        var all = new List<UITextDiagnostic>();
        UITextDiagnosticKind Kind(bool skipped, bool excluded, bool ambiguous, string? review, string translation)
        {
            if (excluded) return UITextDiagnosticKind.Excluded;
            if (skipped || ambiguous) return UITextDiagnosticKind.KeptOriginal;
            if (!string.IsNullOrEmpty(review)) return UITextDiagnosticKind.NeedsReview;
            if (string.IsNullOrWhiteSpace(translation)) return UITextDiagnosticKind.Untranslated;
            return !applied || dirty ? UITextDiagnosticKind.ReadyToApply : UITextDiagnosticKind.AlreadyTranslated;
        }
        foreach (var item in extraction.Entries)
        {
            var entry = pack.Find(item.Original);
            all.Add(new(item.Original, item.Context, entry?.Translated ?? string.Empty,
                Kind(pack.IsSkipped(item.Original), item.Role == UITextRole.Excluded,
                    item.Role == UITextRole.Ambiguous && entry?.IsUserSource != true, entry?.Review, entry?.Translated ?? string.Empty), item.Reason));
        }
        foreach (var item in extraction.Resources)
        {
            var entry = pack.FindResource(item.Container, item.Key);
            all.Add(new(item.Value, item.Container + " · " + item.Key, entry?.Translated ?? string.Empty,
                Kind(pack.IsResourceSkipped(item.Container, item.Key), false, false, entry?.Review, entry?.Translated ?? string.Empty), ""));
        }
        foreach (var item in extraction.Attributes)
        {
            var entry = pack.FindAttribute(item.Value);
            all.Add(new(item.Value, item.Owner, entry?.Translated ?? string.Empty,
                Kind(pack.IsAttributeSkipped(item.Value), false, false, entry?.Review, entry?.Translated ?? string.Empty), ""));
        }
        var exact = all.Where(e => string.Equals(e.Original.Trim(), query, StringComparison.OrdinalIgnoreCase)).ToList();
        return (exact.Count > 0 ? exact : all.Where(e => e.Original.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList())
            .DistinctBy(e => (e.Original, e.Kind)).Take(10).ToList();
    }
}
