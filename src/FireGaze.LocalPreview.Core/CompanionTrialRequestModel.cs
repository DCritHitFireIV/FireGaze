using System.Globalization;

namespace FireGaze.LocalPreview.Core;

/// <summary>How an explicit pet request treats an already requested companion.</summary>
public enum CompanionTrialRequestMode
{
    /// <summary>One request per companion id. This is the normal pet behavior.</summary>
    Normal,

    /// <summary>
    /// The user explicitly opened the same-species interaction category. Each click adds
    /// one independent instance, including for pets that have no special interaction data.
    /// </summary>
    SameSpeciesInteraction,
}

/// <summary>
/// One catalog row used by the native-free request model and future UI. Optional source flags
/// are detached snapshots from the localized Companion sheet; ordinary catalog callers can keep
/// using the two-argument constructor without opting into interaction metadata.
/// </summary>
public sealed record CompanionTrialCatalogEntry(uint CompanionId, string Name)
{
    public uint? BehaviorId { get; init; }
    public byte? Special { get; init; }
    public bool? TitleRequired { get; init; }
    public bool? PokeReaction { get; init; }
    public string? ContextualDescription { get; init; }

    public CompanionSourceInteractionRule? SourceInteractionRule
        => CompanionSourceInteractionRules.FromCatalogEntry(this);
}

/// <summary>Stable identity for one manually added repeated pet instance.</summary>
public readonly record struct CompanionTrialInstanceIdentity(
    long RequestId,
    uint CompanionId,
    int InstanceIndex);

/// <summary>Detached request data safe to pass to UI or a future native adapter.</summary>
public readonly record struct CompanionTrialRequest(
    long RequestId,
    uint CompanionId,
    int InstanceIndex,
    string Name)
{
    public CompanionTrialInstanceIdentity Identity
        => new(RequestId, CompanionId, InstanceIndex);
}

/// <summary>Result of one explicit user request.</summary>
public readonly record struct CompanionTrialRequestResult(
    bool Accepted,
    CompanionTrialRequest? Request,
    long? ExistingRequestId,
    int ExistingCount,
    string Reason)
{
    public static CompanionTrialRequestResult Rejected(
        string reason,
        int existingCount = 0,
        long? existingRequestId = null)
        => new(false, null, existingRequestId, existingCount, reason);
}

/// <summary>
/// Searchable, native-free companion catalog. Searching only filters catalog rows; it never
/// creates a summon request or changes the request model.
/// </summary>
public sealed class CompanionTrialCatalog
{
    private readonly CompanionTrialCatalogEntry[] entries;

    public CompanionTrialCatalog(IEnumerable<CompanionTrialCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        this.entries = entries
            .Where(entry => entry.CompanionId != 0 && !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => entry with { Name = entry.Name.Trim() })
            .GroupBy(entry => entry.CompanionId)
            .Select(group => group.First())
            .OrderBy(entry => entry.Name, StringComparer.CurrentCulture)
            .ThenBy(entry => entry.CompanionId)
            .ToArray();
    }

    public IReadOnlyList<CompanionTrialCatalogEntry> Entries
        => Array.AsReadOnly(entries);

    public bool TryGet(uint companionId, out CompanionTrialCatalogEntry entry)
    {
        var match = entries.FirstOrDefault(candidate => candidate.CompanionId == companionId);
        if (match is null)
        {
            entry = null!;
            return false;
        }

        entry = match;
        return true;
    }

    /// <summary>Matches trimmed localized names case-insensitively or an exact numeric id.</summary>
    public IReadOnlyList<CompanionTrialCatalogEntry> Search(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        if (normalized.Length == 0) return Entries;

        return entries
            .Where(entry => entry.Name.Contains(normalized, StringComparison.CurrentCultureIgnoreCase)
                || string.Equals(entry.CompanionId.ToString(CultureInfo.InvariantCulture), normalized,
                    StringComparison.Ordinal))
            .ToArray();
    }
}

/// <summary>
/// Owns only explicit user request intent. Ordinary requests remain singleton per companion;
/// repeated instances are available only after switching to the same-species category and
/// adding each instance manually.
/// </summary>
public sealed class CompanionTrialRequestModel
{
    public const int MaxInstancesPerCompanion = 30;

    private readonly CompanionTrialCatalog catalog;
    private readonly List<CompanionTrialRequest> requests = [];
    private long nextRequestId;

    public CompanionTrialRequestModel(CompanionTrialCatalog catalog)
        => this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public CompanionTrialRequestMode Mode { get; private set; }

    public IReadOnlyList<CompanionTrialRequest> Requests
        => Array.AsReadOnly(requests.ToArray());

    public IReadOnlyList<CompanionTrialCatalogEntry> Search(string? query)
        => catalog.Search(query);

    public void SetMode(CompanionTrialRequestMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Mode = mode;
    }

    public int CountFor(uint companionId)
        => requests.Count(request => request.CompanionId == companionId);

    /// <summary>Adds exactly one request for the selected catalog row.</summary>
    public CompanionTrialRequestResult Request(uint companionId)
        => RequestCore(companionId, Mode == CompanionTrialRequestMode.SameSpeciesInteraction);

    /// <summary>Replays one explicitly saved instance without changing the manual request mode.</summary>
    public CompanionTrialRequestResult Restore(uint companionId) => RequestCore(companionId, allowRepeated: true);

    private CompanionTrialRequestResult RequestCore(uint companionId, bool allowRepeated)
    {
        if (!catalog.TryGet(companionId, out var entry))
            return CompanionTrialRequestResult.Rejected("companion is not in the catalog");

        var existing = requests.Where(request => request.CompanionId == companionId).ToArray();
        if (!allowRepeated && existing.Length != 0)
        {
            var first = existing[0];
            return CompanionTrialRequestResult.Rejected(
                "companion already has a request", existing.Length, first.RequestId);
        }

        if (existing.Length >= MaxInstancesPerCompanion)
            return CompanionTrialRequestResult.Rejected(
                "maximum repeated instances reached", existing.Length);

        var nextIndex = existing.Length == 0 ? 1 : existing.Max(request => request.InstanceIndex) + 1;
        var request = new CompanionTrialRequest(
            checked(++nextRequestId), companionId, nextIndex, entry.Name);
        requests.Add(request);
        return new(true, request, null, existing.Length + 1, string.Empty);
    }

    /// <summary>Removes only the request with this exact identity.</summary>
    public bool Remove(long requestId)
    {
        var index = requests.FindIndex(request => request.RequestId == requestId);
        if (index < 0) return false;
        requests.RemoveAt(index);
        return true;
    }

    /// <summary>Removes every explicit request while retaining monotonically increasing identities.</summary>
    public void Clear()
        => requests.Clear();
}
