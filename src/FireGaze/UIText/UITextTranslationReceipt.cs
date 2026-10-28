namespace FireGaze.UIText;

// A response owns only the exact rows selected when its request started.
// The snapshots contain immutable values, never references to editor rows.
internal sealed class UITextTranslationReceipt
{
    private readonly Snapshot[] snapshots;
    private readonly bool overwriteExisting;
    private readonly bool overwriteUser;
    private readonly HashSet<Identity> invalidated = [];

    public UITextTranslationReceipt(UITextPack baseline, IReadOnlyList<UITextTarget> targets,
        bool overwriteExisting, bool overwriteUser)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(targets);
        this.overwriteExisting = overwriteExisting;
        this.overwriteUser = overwriteUser;
        var selected = new Dictionary<Identity, Snapshot>();
        foreach (var target in targets)
        {
            // A malformed target must not gain permission over multiple row
            // kinds, or over an original that was not actually requested.
            if (target.Entry is { } literal && target.Resource is null && target.Attribute is null
                && baseline.Find(literal.Original) is { } entry
                && entry.Original == target.Original)
            {
                var identity = new Identity(Kind.Literal, string.Empty, entry.Original);
                selected[identity] = new(identity, entry.Original, target.TranslationText,
                    ValueOf(entry), baseline.IsSkipped(entry.Original));
            }
            else if (target.Resource is { } resource && target.Entry is null && target.Attribute is null
                && baseline.FindResource(resource.Container, resource.Key) is { } row
                && row.Original == target.Original && row.Original == resource.Original)
            {
                var identity = new Identity(Kind.Resource, row.Container, row.Key);
                selected[identity] = new(identity, row.Original, target.TranslationText,
                    ValueOf(row), baseline.IsResourceSkipped(row.Container, row.Key));
            }
            else if (target.Attribute is { } attribute && target.Entry is null && target.Resource is null
                && baseline.FindAttribute(attribute.Original) is { } rowAttribute
                && rowAttribute.Original == target.Original)
            {
                var identity = new Identity(Kind.Attribute, string.Empty, rowAttribute.Original);
                selected[identity] = new(identity, rowAttribute.Original, target.TranslationText,
                    ValueOf(rowAttribute), baseline.IsAttributeSkipped(rowAttribute.Original));
            }
        }

        this.snapshots = selected.Values.ToArray();
    }

    internal sealed record Result(UITextPack Pack, int Applied, int PlaceholderRejected, int Unchanged);

    // The owner serializes this with acceptance and persistence. A user action
    // invalidates a row even when its final value equals the request baseline.
    public void Invalidate(UITextTarget target)
    {
        if (target.Entry is { } entry)
            this.invalidated.Add(new(Kind.Literal, string.Empty, entry.Original));
        else if (target.Resource is { } resource)
            this.invalidated.Add(new(Kind.Resource, resource.Container, resource.Key));
        else if (target.Attribute is { } attribute)
            this.invalidated.Add(new(Kind.Attribute, string.Empty, attribute.Original));
    }

    public Result Accept(UITextPack current, IEnumerable<KeyValuePair<string, string>> translated, string channelName)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(translated);
        ArgumentNullException.ThrowIfNull(channelName);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (original, value) in translated) values[original] = value;

        // Only this independent candidate is mutable. The small acceptance
        // pack shares selected rows with the candidate, never with an input.
        // The established flow changes only Translated / Source / Review.
        var candidate = UITextPackEditMerge.Clone(current);
        var eligible = new UITextPack();
        var protectedExisting = 0;
        foreach (var snapshot in this.snapshots)
        {
            if (this.invalidated.Contains(snapshot.Identity)) continue;
            if (!values.ContainsKey(snapshot.Original)) continue;
            var identity = snapshot.Identity;
            switch (identity.Kind)
            {
                case Kind.Literal:
                    if (candidate.Find(identity.Key) is not { } entry
                        || !Matches(snapshot, ValueOf(entry), candidate.IsSkipped(identity.Key))) break;
                    if (!this.CanOverwrite(snapshot.Value)) { protectedExisting++; break; }
                    eligible.Entries.Add(entry);
                    break;
                case Kind.Resource:
                    if (candidate.FindResource(identity.Container, identity.Key) is not { } resource
                        || resource.Original != snapshot.Original
                        || !Matches(snapshot, ValueOf(resource), candidate.IsResourceSkipped(identity.Container, identity.Key))) break;
                    if (!this.CanOverwrite(snapshot.Value)) { protectedExisting++; break; }
                    eligible.Resources.Add(resource);
                    break;
                case Kind.Attribute:
                    if (candidate.FindAttribute(identity.Key) is not { } attribute
                        || !Matches(snapshot, ValueOf(attribute), candidate.IsAttributeSkipped(identity.Key))) break;
                    if (!this.CanOverwrite(snapshot.Value)) { protectedExisting++; break; }
                    eligible.Attributes.Add(attribute);
                    break;
            }
        }

        var providerTexts = this.snapshots.GroupBy(snapshot => snapshot.Original, StringComparer.Ordinal)
            .Where(group => group.Select(snapshot => snapshot.ProviderText).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().ProviderText, StringComparer.Ordinal);
        var accepted = UITextFlow.AcceptTranslations(eligible, values, channelName, this.overwriteExisting,
            this.overwriteUser, providerTexts);
        return new(candidate, accepted.Applied, accepted.PlaceholderRejected, accepted.Unchanged + protectedExisting);
    }

    private bool CanOverwrite(TranslationValue value) =>
        (this.overwriteUser || !string.Equals(value.Source, "user", StringComparison.OrdinalIgnoreCase))
        && (this.overwriteExisting || string.IsNullOrWhiteSpace(value.Translated));

    private static bool Matches(Snapshot snapshot, TranslationValue current, bool skipped) =>
        !skipped && skipped == snapshot.Skipped && current == snapshot.Value;

    private static TranslationValue ValueOf(UITextPackEntry entry) => new(entry.Translated, entry.Source, entry.Review);
    private static TranslationValue ValueOf(UITextResourceEntry entry) => new(entry.Translated, entry.Source, entry.Review);
    private static TranslationValue ValueOf(UITextAttributeEntry entry) => new(entry.Translated, entry.Source, entry.Review);

    private enum Kind { Literal, Resource, Attribute }
    private readonly record struct Identity(Kind Kind, string Container, string Key);
    private sealed record TranslationValue(string Translated, string? Source, string? Review);
    private sealed record Snapshot(Identity Identity, string Original, string ProviderText,
        TranslationValue Value, bool Skipped);
}
