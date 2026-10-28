namespace FireGaze.LocalPreview.Interaction;

/// <summary>Which collection is shown by the "我的宠物" panel.</summary>
public enum MinionTrialView
{
    Owned,
    Summoned,
}

/// <summary>Result of a user action in the local pet collection.</summary>
public enum MinionTrialActionResult
{
    Started,
    Queued,
    Recalled,
    Noop,
    NotFound,
}

/// <summary>
/// UI-facing state for local companion trials. It deliberately does not hold
/// native pointers or call the game. The current bridge owns one
/// CompanionData.CompanionObject, therefore additional pets are represented as
/// explicit requested entries until a multi-actor bridge is available.
/// </summary>
public sealed class MinionTrialCollection
{
    private readonly Dictionary<uint, PreviewCatalogEntry> catalog;
    private readonly HashSet<uint> owned = [];
    private readonly List<uint> pending = [];
    private readonly List<uint> saved = [];
    private uint? active;
    private uint? selected;

    public MinionTrialCollection(IEnumerable<PreviewCatalogEntry> entries)
    {
        catalog = entries
            .Where(entry => entry.Kind == PreviewKind.Minion && entry.Id is > 0 and <= ushort.MaxValue)
            .GroupBy(entry => entry.Id)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public MinionTrialView View { get; set; } = MinionTrialView.Owned;
    public uint? SelectedId => selected;
    public IReadOnlyList<uint> OwnedIds => owned.Order().ToArray();
    /// <summary>The one pet currently occupying the native CompanionObject slot.</summary>
    public uint? ActiveId => active;
    /// <summary>Pet requests waiting for the native slot to become available, in summon order.</summary>
    public IReadOnlyList<uint> PendingIds => pending.ToArray();
    /// <summary>
    /// Compatibility view for older callers. This is a requested sequence, not a claim that
    /// multiple native companions exist; new UI and persistence code must use ActiveId/PendingIds.
    /// </summary>
    [Obsolete("Use ActiveId and PendingIds to distinguish the native pet from queued requests.")]
    public IReadOnlyList<uint> SummonedIds => CurrentIds().ToArray();
    /// <summary>The one persisted group slot. It is intentionally a single slot so the UI
    /// cannot silently grow a second profile that the native bridge cannot restore.</summary>
    public IReadOnlyList<uint> SavedIds => saved.ToArray();
    public bool SupportsConcurrentNativeCompanions => false;
    public string NativeLimitation => "当前游戏角色只提供一个本地宠物槽位；第二只及之后会保留为待接入请求。";

    public IReadOnlyList<PreviewCatalogEntry> VisibleEntries
        => (View == MinionTrialView.Owned ? (IEnumerable<uint>)owned : CurrentIds())
            .Where(catalog.ContainsKey)
            .Select(id => catalog[id])
            .ToArray();

    private IEnumerable<uint> CurrentIds()
    {
        if (active is { } current) yield return current;
        foreach (var id in pending) yield return id;
    }

    /// <summary>Replaces ownership from the character collection and drops invalid selections.</summary>
    public IReadOnlyList<uint> SetOwned(IEnumerable<uint> ids)
    {
        owned.Clear();
        foreach (var id in ids.Where(catalog.ContainsKey).Distinct()) owned.Add(id);
        if (active is { } current && !owned.Contains(current)) active = null;
        pending.RemoveAll(id => !owned.Contains(id));
        PromoteNext();
        if (selected is { } selectedId && !owned.Contains(selectedId)) selected = null;
        return OwnedIds;
    }

    /// <summary>Left-click in the owned view. A duplicate summon is never created.</summary>
    public MinionTrialActionResult Summon(uint id)
    {
        if (!owned.Contains(id) || !catalog.ContainsKey(id)) return MinionTrialActionResult.NotFound;
        if (active == id || pending.Contains(id)) return MinionTrialActionResult.Noop;
        var result = active is null ? MinionTrialActionResult.Started : MinionTrialActionResult.Queued;
        if (active is null) active = id;
        else pending.Add(id);
        return result;
    }

    /// <summary>Right-click in either view. Removing the selected id is idempotent.</summary>
    public MinionTrialActionResult Recall(uint id)
    {
        if (active == id)
        {
            active = null;
            if (selected == id) selected = null;
            PromoteNext();
            return MinionTrialActionResult.Recalled;
        }
        if (!pending.Remove(id)) return MinionTrialActionResult.NotFound;
        if (selected == id) selected = null;
        return MinionTrialActionResult.Recalled;
    }

    /// <summary>Recalls every requested local pet and clears the current action target.</summary>
    public int RecallAll()
    {
        var count = (active is null ? 0 : 1) + pending.Count;
        active = null;
        pending.Clear();
        selected = null;
        return count;
    }

    /// <summary>Stores the complete current requested group in the only saved slot.</summary>
    public bool SaveCurrent()
    {
        if (active is null && pending.Count == 0) return false;
        saved.Clear();
        saved.AddRange(CurrentIds().Where(catalog.ContainsKey).Distinct());
        return saved.Count != 0;
    }

    /// <summary>Clears the single saved group slot.</summary>
    public bool ClearSaved()
    {
        if (saved.Count == 0) return false;
        saved.Clear();
        return true;
    }

    /// <summary>Restores the one saved slot from configuration, filtering IDs that
    /// are not part of the current client catalog.</summary>
    public IReadOnlyList<uint> RestoreSaved(IEnumerable<uint> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        saved.Clear();
        saved.AddRange(ids.Where(catalog.ContainsKey).Distinct());
        return SavedIds;
    }

    /// <summary>Queues every saved pet for one-click summon, preserving saved order.</summary>
    public MinionTrialActionResult LoadSaved()
    {
        if (saved.Count == 0) return MinionTrialActionResult.NotFound;
        var result = MinionTrialActionResult.Noop;
        foreach (var id in saved.ToArray())
        {
            var currentResult = Summon(id);
            if (result == MinionTrialActionResult.Noop && currentResult is MinionTrialActionResult.Started or MinionTrialActionResult.Queued)
                result = currentResult;
        }
        return result;
    }

    /// <summary>Only a currently summoned local pet can receive an action target.</summary>
    public bool Select(uint id)
    {
        if (active != id) return false;
        selected = id;
        return true;
    }

    /// <summary>Left-click in the summoned view is intentionally inert.</summary>
    public MinionTrialActionResult ActivateLeftClick(uint id)
        => active == id || pending.Contains(id) ? MinionTrialActionResult.Noop : MinionTrialActionResult.NotFound;

    private void PromoteNext()
    {
        if (active is not null || pending.Count == 0) return;
        active = pending[0];
        pending.RemoveAt(0);
    }
}
