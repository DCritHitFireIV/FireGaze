namespace FireGaze.PortraitPreview;

/// <summary>Demand-driven texture ownership; queued and resident resources share one hard bound.</summary>
internal sealed class PortraitThumbnailTextureCache<T> : IDisposable where T : class, IDisposable
{
    private const int MaximumFailureKeys = 128;
    private readonly object gate = new();
    private readonly Func<string, Task<T?>> loader;
    private readonly int capacity;
    private readonly int maximumConcurrentLoads;
    private readonly Dictionary<string, Resident> residents = new(StringComparer.Ordinal);
    private readonly LinkedList<string> recent = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LinkedListNode<string>> failures = new(StringComparer.Ordinal);
    private readonly LinkedList<string> failedOrder = new();
    private bool disposed;
    private sealed record Resident(T Texture, LinkedListNode<string> Position);
    private sealed class Pending { public bool Accept = true; }

    public PortraitThumbnailTextureCache(Func<string, Task<T?>> loader, int capacity = 32, int maximumConcurrentLoads = 2)
    {
        ArgumentNullException.ThrowIfNull(loader);
        if (capacity is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumConcurrentLoads is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(maximumConcurrentLoads));
        this.loader = loader;
        this.capacity = capacity;
        this.maximumConcurrentLoads = Math.Min(capacity, maximumConcurrentLoads);
    }

    public int Count { get { lock (gate) return residents.Count; } }
    public int InFlight { get { lock (gate) return pending.Count; } }
    public int TotalCount { get { lock (gate) return residents.Count + pending.Count; } }

    /// <summary>Returns a borrowed resident, or starts a bounded background load. No waiting or backlog.</summary>
    public T? Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        Pending ticket;
        T? evicted = null;
        lock (gate)
        {
            if (disposed) return null;
            if (residents.TryGetValue(key, out var resident))
            {
                recent.Remove(resident.Position);
                recent.AddLast(resident.Position);
                return resident.Texture;
            }
            if (failures.ContainsKey(key) || pending.ContainsKey(key) || pending.Count >= maximumConcurrentLoads) return null;
            if (residents.Count + pending.Count >= capacity)
            {
                var oldest = recent.First!;
                evicted = residents[oldest.Value].Texture;
                residents.Remove(oldest.Value);
                recent.RemoveFirst();
            }
            ticket = new Pending();
            pending.Add(key, ticket);
        }
        Release(evicted);
        // The loader's synchronous prefix cannot block an ImGui draw callback.
        _ = Task.Run(() => Load(key, ticket));
        return null;
    }

    /// <summary>Allows refreshed image data to retry. Existing pending work retains its slot until finished.</summary>
    public void Invalidate(string key)
    {
        T? released = null;
        lock (gate)
        {
            if (pending.TryGetValue(key, out var ticket)) ticket.Accept = false;
            if (residents.Remove(key, out var resident))
            {
                recent.Remove(resident.Position);
                released = resident.Texture;
            }
            if (failures.Remove(key, out var failed)) failedOrder.Remove(failed);
        }
        Release(released);
    }

    public void Dispose()
    {
        T[] released;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            released = residents.Values.Select(resident => resident.Texture).ToArray();
            residents.Clear();
            recent.Clear();
            failures.Clear();
            failedOrder.Clear();
            // Retain at most two tickets so late completions can release their own resources.
            foreach (var ticket in pending.Values) ticket.Accept = false;
        }
        foreach (var texture in released) Release(texture);
    }

    private async Task Load(string key, Pending ticket)
    {
        T? loaded = null;
        try { loaded = await loader(key).ConfigureAwait(false); }
        catch (Exception) { /* An unreadable or cancelled image stays unavailable until invalidated. */ }
        lock (gate)
        {
            if (!disposed && ticket.Accept)
            {
                pending.Remove(key);
                if (loaded is not null)
                {
                    residents.Add(key, new(loaded, recent.AddLast(key)));
                    loaded = null;
                }
                else
                {
                    failures.Add(key, failedOrder.AddLast(key));
                    if (failures.Count > MaximumFailureKeys)
                    {
                        failures.Remove(failedOrder.First!.Value);
                        failedOrder.RemoveFirst();
                    }
                }
            }
        }
        Release(loaded);
        // A cancelled result may still own a real texture while Dispose runs.
        // Keep that slot reserved until release finishes, including disposal failures.
        lock (gate)
            if (pending.TryGetValue(key, out var remaining) && ReferenceEquals(remaining, ticket)) pending.Remove(key);
    }

    private static void Release(T? texture)
    {
        // Resource release must never escape into the draw callback or an unobserved completion task.
        try { texture?.Dispose(); } catch (Exception) { }
    }
}
