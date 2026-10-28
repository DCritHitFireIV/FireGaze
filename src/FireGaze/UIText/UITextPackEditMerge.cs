using System.Text.Json;

namespace FireGaze.UIText;

// A candidate is built independently of the editor and disk snapshots. Only
// differences between baseline and local are applied; the caller adopts this
// candidate and advances its baseline only after persistence succeeds.
internal static class UITextPackEditMerge
{
    internal sealed record EditIntent(UITextTarget Target, bool Translation, bool Skipped);
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        IgnoreReadOnlyProperties = true,
    };

    public static UITextPack Clone(UITextPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        // ToJSON changes Meta.Format on its input. Serialize and deserialize
        // with identical typed options to preserve all writable JSON fields
        // without changing the input or running a format migration.
        return JsonSerializer.Deserialize<UITextPack>(JsonSerializer.Serialize(pack, CloneOptions), CloneOptions)
            ?? throw new InvalidOperationException("无法复制编辑器译文包。");
    }

    public static UITextPack Merge(UITextPack baseline, UITextPack local, UITextPack latest,
        IEnumerable<EditIntent>? intents = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(latest);
        var edited = Clone(local);
        var result = Clone(latest);
        var actions = intents?.ToArray() ?? [];
        ValidateResourceIdentities(baseline, edited, result, actions);

        MergeRows(baseline.Entries, edited.Entries, result.Entries, entry => entry.Original,
            SameLiteral, MergeLiteral);
        MergeRows(baseline.Resources, edited.Resources, result.Resources, entry => (entry.Container, entry.Key),
            SameResource, MergeResource);
        MergeRows(baseline.Attributes, edited.Attributes, result.Attributes, entry => entry.Original,
            SameAttribute, MergeAttribute);
        MergeSkipped(baseline.Skipped, edited.Skipped, result.Skipped);
        MergeSkipped(baseline.SkippedResources, edited.SkippedResources, result.SkippedResources);
        MergeSkipped(baseline.SkippedAttributes, edited.SkippedAttributes, result.SkippedAttributes);

        // Value equality is not evidence that no edit occurred. Explicit empty
        // input or skip/unskip owns only the fields that the user acted on.
        foreach (var intent in actions)
        {
            var target = intent.Target;
            if (target.Entry is { } literal && edited.Find(literal.Original) is { } entry)
            {
                var row = result.Find(entry.Original);
                if (row is null) { row = entry; result.Entries.Add(row); }
                if (intent.Translation) (row.Translated, row.Source, row.Review) = (entry.Translated, entry.Source, entry.Review);
                if (intent.Skipped)
                {
                    if (edited.IsSkipped(entry.Original)) result.MarkSkipped(entry.Original);
                    else result.UnmarkSkipped(entry.Original);
                }
            }
            else if (target.Resource is { } resource && edited.FindResource(resource.Container, resource.Key) is { } source
                && source.Original == target.Original)
            {
                var row = result.FindResource(source.Container, source.Key);
                if (row is not null && !string.Equals(row.Original, source.Original, StringComparison.Ordinal))
                    throw new InvalidOperationException($"资源身份冲突：{source.Container}/{source.Key} 的原文已从“{source.Original}”变为“{row.Original}”。");
                if (row is null) { row = source; result.Resources.Add(row); }
                if (intent.Translation) (row.Translated, row.Source, row.Review) = (source.Translated, source.Source, source.Review);
                if (intent.Skipped)
                {
                    if (edited.IsResourceSkipped(source.Container, source.Key)) result.MarkResourceSkipped(source.Container, source.Key);
                    else result.UnmarkResourceSkipped(source.Container, source.Key);
                }
            }
            else if (target.Attribute is { } attribute && edited.FindAttribute(attribute.Original) is { } attributeSource)
            {
                var row = result.FindAttribute(attributeSource.Original);
                if (row is null) { row = attributeSource; result.Attributes.Add(row); }
                if (intent.Translation) (row.Translated, row.Source, row.Review) = (attributeSource.Translated, attributeSource.Source, attributeSource.Review);
                if (intent.Skipped)
                {
                    if (edited.IsAttributeSkipped(attributeSource.Original)) result.MarkAttributeSkipped(attributeSource.Original);
                    else result.UnmarkAttributeSkipped(attributeSource.Original);
                }
            }
        }

        result.Meta.Format = Changed(baseline.Meta.Format, edited.Meta.Format, result.Meta.Format);
        result.Meta.UpdatedAt = Changed(baseline.Meta.UpdatedAt, edited.Meta.UpdatedAt, result.Meta.UpdatedAt);
        result.Meta.PluginVersion = Changed(baseline.Meta.PluginVersion, edited.Meta.PluginVersion, result.Meta.PluginVersion);
        result.Meta.Source = Changed(baseline.Meta.Source, edited.Meta.Source, result.Meta.Source);
        return result;
    }

    private static void MergeRows<T, TKey>(IReadOnlyList<T> baseline, IReadOnlyList<T> local, List<T> result,
        Func<T, TKey> key, Func<T, T, bool> same, Action<T, T, T> merge) where TKey : notnull
    {
        var before = baseline.ToDictionary(key);
        var edited = local.ToDictionary(key);
        // A local removal is a real delta (for example extraction pruning).
        // Identities added only by the background writer are never removed.
        result.RemoveAll(row => before.ContainsKey(key(row)) && !edited.ContainsKey(key(row)));
        var targets = result.ToDictionary(key);
        foreach (var row in local)
        {
            var identity = key(row);
            if (!before.TryGetValue(identity, out var prior))
            {
                // Both writers may independently add the same identity. The
                // explicit local addition owns its fields, as other local
                // field conflicts do. Rows are from an independent deep clone.
                if (targets.TryGetValue(identity, out var existing)) result.Remove(existing);
                result.Add(row);
                targets[identity] = row;
            }
            else if (targets.TryGetValue(identity, out var target))
            {
                merge(prior, row, target);
            }
            else if (!same(prior, row))
            {
                // A background removal must not erase an actual unsaved edit;
                // an untouched old editor row must not resurrect that removal.
                result.Add(row);
                targets[identity] = row;
            }
        }
    }

    private static void MergeLiteral(UITextPackEntry baseline, UITextPackEntry local, UITextPackEntry target)
    {
        if ((baseline.Translated, baseline.Source, baseline.Review) != (local.Translated, local.Source, local.Review))
        {
            target.Translated = local.Translated;
            target.Source = local.Source;
            target.Review = local.Review;
        }
        target.Context = Changed(baseline.Context, local.Context, target.Context);
        target.PreserveID = Changed(baseline.PreserveID, local.PreserveID, target.PreserveID);
        target.Role = Changed(baseline.Role, local.Role, target.Role);
        target.RoleReason = Changed(baseline.RoleReason, local.RoleReason, target.RoleReason);
    }

    private static void ValidateResourceIdentities(UITextPack baseline, UITextPack local, UITextPack latest,
        IReadOnlyList<EditIntent> intents)
    {
        foreach (var before in baseline.Resources)
        {
            var current = latest.FindResource(before.Container, before.Key);
            if (current is null || current.Original == before.Original) continue;
            var edited = local.FindResource(before.Container, before.Key);
            if (edited is null || !SameResource(before, edited)
                || baseline.IsResourceSkipped(before.Container, before.Key) != local.IsResourceSkipped(before.Container, before.Key))
                throw ResourceConflict(before.Container, before.Key);
        }
        foreach (var row in local.Resources)
            if (baseline.FindResource(row.Container, row.Key) is null
                && latest.FindResource(row.Container, row.Key) is { } current && current.Original != row.Original)
                throw ResourceConflict(row.Container, row.Key);
        foreach (var action in intents)
            if (action.Target.Resource is { } row
                && latest.FindResource(row.Container, row.Key) is { } current && current.Original != action.Target.Original)
                throw ResourceConflict(row.Container, row.Key);
    }

    private static InvalidOperationException ResourceConflict(string container, string key) =>
        new($"资源身份冲突：{container}/{key} 的原文已更新。未保存的编辑已保留，请先导出校对内容并核对新原文。");

    private static void MergeResource(UITextResourceEntry baseline, UITextResourceEntry local, UITextResourceEntry target)
    {
        if ((baseline.Translated, baseline.Source, baseline.Review) != (local.Translated, local.Source, local.Review))
        {
            target.Translated = local.Translated;
            target.Source = local.Source;
            target.Review = local.Review;
        }
        target.Original = Changed(baseline.Original, local.Original, target.Original);
    }

    private static void MergeAttribute(UITextAttributeEntry baseline, UITextAttributeEntry local, UITextAttributeEntry target)
    {
        if ((baseline.Translated, baseline.Source, baseline.Review) != (local.Translated, local.Source, local.Review))
        {
            target.Translated = local.Translated;
            target.Source = local.Source;
            target.Review = local.Review;
        }
        target.Context = Changed(baseline.Context, local.Context, target.Context);
    }

    private static bool SameLiteral(UITextPackEntry baseline, UITextPackEntry local) =>
        (baseline.Translated, baseline.Source, baseline.Review, baseline.Context, baseline.PreserveID, baseline.Role, baseline.RoleReason)
        == (local.Translated, local.Source, local.Review, local.Context, local.PreserveID, local.Role, local.RoleReason);

    private static bool SameResource(UITextResourceEntry baseline, UITextResourceEntry local) =>
        (baseline.Translated, baseline.Source, baseline.Review, baseline.Original)
        == (local.Translated, local.Source, local.Review, local.Original);

    private static bool SameAttribute(UITextAttributeEntry baseline, UITextAttributeEntry local) =>
        (baseline.Translated, baseline.Source, baseline.Review, baseline.Context)
        == (local.Translated, local.Source, local.Review, local.Context);

    private static T Changed<T>(T baseline, T local, T latest) =>
        EqualityComparer<T>.Default.Equals(baseline, local) ? latest : local;

    private static void MergeSkipped(IReadOnlyList<string> baseline, IReadOnlyList<string> local, List<string> result)
    {
        var before = baseline.ToHashSet(StringComparer.Ordinal);
        var edited = local.ToHashSet(StringComparer.Ordinal);
        result.RemoveAll(token => before.Contains(token) && !edited.Contains(token));
        var current = result.ToHashSet(StringComparer.Ordinal);
        foreach (var token in local)
            if (!before.Contains(token) && current.Add(token)) result.Add(token);
    }
}
