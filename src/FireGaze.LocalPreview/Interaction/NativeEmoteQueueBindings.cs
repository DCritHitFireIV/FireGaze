namespace FireGaze.LocalPreview.Interaction;

/// <summary>
/// Carries the numeric ID from one native emote-log emission to that emission's
/// queued formatter invocation. Keys are opaque identities, never dereferenced.
/// </summary>
public sealed class NativeEmoteQueueBindings
{
    private readonly Dictionary<nint, ushort> bindings = new();

    public void CaptureEmission(ushort emoteId, IReadOnlyList<nint> before, IReadOnlyList<nint> after)
    {
        RetainLive(after);
        if (emoteId == 0) return;
        var oldItems = before.ToHashSet();
        // An already bound item can belong to a nested native emission. The
        // outer call must not relabel it. More than one new unbound item is
        // unexpected: preserve the original messages instead of guessing.
        var emitted = after.Where(key => key != 0 && !oldItems.Contains(key) && !bindings.ContainsKey(key))
            .Distinct().ToArray();
        if (emitted.Length == 1) bindings.Add(emitted[0], emoteId);
    }

    public bool TryGet(nint queueItem, out ushort emoteId) => bindings.TryGetValue(queueItem, out emoteId);

    public void RetainLive(IReadOnlyList<nint> liveItems)
    {
        var live = liveItems.ToHashSet();
        foreach (var key in bindings.Keys.Where(key => !live.Contains(key)).ToArray()) bindings.Remove(key);
    }

    /// <param name="nativeResult">Native formatter result: zero retries, one succeeds, minus one drops.</param>
    public void CompleteFormat(nint queueItem, int nativeResult)
    {
        if (nativeResult != 0) bindings.Remove(queueItem);
    }

    public void Clear() => bindings.Clear();
}
