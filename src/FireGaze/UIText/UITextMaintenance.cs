namespace FireGaze.UIText;

internal enum UITextDiagnosticKind { Untranslated, KeptOriginal, Excluded, NeedsReview, ReadyToApply, AlreadyTranslated }
internal sealed record UITextDiagnostic(string Original, string? Context, string Translation, UITextDiagnosticKind Kind, string Reason);

internal static class UITextMaintenance
{
    public static bool CanApplyUpdate(UITextFlow.MergePreview preview, string? warning, int safetyRepairs = 0) => safetyRepairs > 0
        || (warning is null && (preview.Added > 0 || preview.Overwritten > 0));

    /// <summary>Compare recorded patch IL to its verified baseline; never infer original IPC names from translations.</summary>
    public static int FunctionalRepairs(string originalPath, string patchedPath, string originalHash, string patchedHash)
    {
        if (originalHash.Length == 0 || patchedHash.Length == 0
            || !string.Equals(UITextPatchStore.HashOf(originalPath), originalHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(UITextPatchStore.HashOf(patchedPath), patchedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("原始备份或插件文件已经变化，需要重新核对原始包。");
        using var original = dnlib.DotNet.ModuleDefMD.Load(originalPath);
        using var patched = dnlib.DotNet.ModuleDefMD.Load(patchedPath);
        var identities = UIStringExtractor.FunctionalIdentifiers(original);
        var methods = patched.GetTypes().SelectMany(t => t.Methods).ToDictionary(m => m.FullName, StringComparer.Ordinal);
        var repairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in original.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            for (var i = 0; i < method.Body.Instructions.Count; i++)
            {
                var instruction = method.Body.Instructions[i];
                if (instruction.OpCode.Code != dnlib.DotNet.Emit.Code.Ldstr || instruction.Operand is not string text
                    || !identities.Contains(text)) continue;
                if (!methods.TryGetValue(method.FullName, out var current) || !current.HasBody
                    || current.Body.Instructions.Count != method.Body.Instructions.Count
                    || current.Body.Instructions[i].OpCode.Code != dnlib.DotNet.Emit.Code.Ldstr)
                    throw new IOException("插件与原始备份的代码不一致，需要重新核对原始包。");
                if (!Equals(current.Body.Instructions[i].Operand, text)) repairs.Add(text);
            }
        }
        return repairs.Count;
    }

    public static int FunctionalTranslationChanges(UITextPack before, UITextPack candidate, UITextExtraction extraction) => extraction.Entries
        .Where(e => e.IsFunctionalIdentifier && candidate.Find(e.Original)?.HasTranslation == true
            && candidate.IsSkipped(e.Original) && !before.IsSkipped(e.Original))
        .Select(e => e.Original).Distinct(StringComparer.Ordinal).Count();

    public static bool SuspectFunctionalTranslations(UITextPack pack, UITextExtraction extraction) => extraction.Entries
        .Any(e => e.IsFunctionalIdentifier && pack.Entries.Any(p => p.HasTranslation
            && p.Original != e.Original && p.Translated.Trim() == e.Original));

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
