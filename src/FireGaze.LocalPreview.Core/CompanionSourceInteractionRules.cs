namespace FireGaze.LocalPreview.Core;

/// <summary>
/// Detached values read from one localized Companion row.  This is data only: it does not
/// retain an Excel row reference, native address, or player identity.
/// </summary>
public readonly record struct CompanionSourceInteractionRow(
    uint CompanionId,
    string Name,
    uint BehaviorId,
    byte Special,
    bool TitleRequired,
    bool PokeReaction);

/// <summary>
/// Conservative interaction evidence derived from a client Companion row.  A rule describes
/// what the catalog knows; it never claims that a native action was queued or rendered.
/// </summary>
public sealed record CompanionSourceInteractionRule(
    uint CompanionId,
    string Name,
    bool IsFollowingType,
    bool IsOrdinaryPokeCandidate,
    bool RequiresTitle,
    string? ContextualDescription);

/// <summary>Small diagnostic-only summary of detached interaction evidence.</summary>
public readonly record struct CompanionSourceInteractionSummary(
    int CatalogRows,
    int RowsWithMetadata,
    int FollowingRows,
    int OrdinaryPokeCandidates,
    int TitleGatedPokeCandidates,
    int ContextualRows,
    bool ProductionAdmission);

public static class CompanionSourceInteractionRules
{
    /// <summary>Native title used by the current CN client's ordinary poke branch.</summary>
    public const ushort FollowingTitleId = 268;

    /// <summary>Client emote id used by the ordinary targeted poke branch.</summary>
    public const uint PokeEmoteId = 28;

    public static CompanionSourceInteractionRule? FromCatalogEntry(CompanionTrialCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.BehaviorId is null && entry.Special is null && entry.TitleRequired is null
            && entry.PokeReaction is null && string.IsNullOrWhiteSpace(entry.ContextualDescription))
            return null;

        var following = entry.BehaviorId is 1;
        // A poke capability is only actionable when the client supplied all three flags.  A
        // missing flag remains unknown and is therefore kept out of the future consumer.
        var poke = following && entry.Special is 0 && entry.PokeReaction is true;
        return new(entry.CompanionId, entry.Name, following, poke,
            poke && entry.TitleRequired is true, entry.ContextualDescription);
    }

    public static CompanionSourceInteractionRule FromClientRow(CompanionSourceInteractionRow row)
        => new(row.CompanionId, row.Name, row.BehaviorId == 1,
            row.BehaviorId == 1 && row.Special == 0 && row.PokeReaction,
            row.BehaviorId == 1 && row.Special == 0 && row.PokeReaction && row.TitleRequired,
            row.CompanionId == 106
                ? "附近有人使用烹调师制作技能时，左右嗅嗅动作更容易出现。"
                : null);

    public static CompanionSourceInteractionSummary Summarize(
        IEnumerable<CompanionTrialCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var rows = entries.ToArray();
        var rules = rows.Select(FromCatalogEntry).Where(rule => rule is not null)
            .Select(rule => rule!).ToArray();
        return new(rows.Length, rules.Length, rules.Count(x => x.IsFollowingType),
            rules.Count(x => x.IsOrdinaryPokeCandidate), rules.Count(x => x.RequiresTitle),
            rules.Count(x => !string.IsNullOrWhiteSpace(x.ContextualDescription)),
            CompanionInteractionAdmission.ProductionAdmission);
    }
}

/// <summary>
/// Keeps the detached interaction seam honest while the reviewed native owner ABI is still
/// incomplete.  A queue receipt is never presented as a visible action without readback.
/// </summary>
public static class CompanionInteractionAdmission
{
    /// <summary>
    /// Intentionally false until physical owner/title handling and the native consumer have
    /// been independently verified on the CN client.
    /// </summary>
    public const bool ProductionAdmission = false;

    public static bool HasVisibleReadback(bool queued, bool visualReadbackConfirmed)
        => queued && visualReadbackConfirmed;

    public static bool TryAdmit(CompanionTrialCatalogEntry entry, bool queued,
        bool visualReadbackConfirmed)
        => ProductionAdmission
            && entry.SourceInteractionRule is { IsOrdinaryPokeCandidate: true }
            && HasVisibleReadback(queued, visualReadbackConfirmed);
}
