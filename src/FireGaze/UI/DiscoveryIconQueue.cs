using FireGaze.Translate;

namespace FireGaze.UI;

internal sealed class DiscoveryIconQueue
{
    private readonly Queue<TranslationIndexEntry> waiting = new();
    private readonly HashSet<string> queued = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> retryAt = new(StringComparer.Ordinal);

    public int Count => this.waiting.Count;
    public int CoolingCount(DateTime now) => this.retryAt.Count(p => p.Value > now);

    public bool TryEnqueue(TranslationIndexEntry entry, DateTime now)
    {
        if (this.queued.Contains(entry.InternalName)
            || (this.retryAt.TryGetValue(entry.InternalName, out var retry) && now < retry)) return false;
        this.retryAt.Remove(entry.InternalName);
        this.queued.Add(entry.InternalName);
        this.waiting.Enqueue(entry);
        return true;
    }

    public TranslationIndexEntry Dequeue() => this.waiting.Dequeue();

    public void Complete(TranslationIndexEntry entry, bool ok, DateTime now)
    {
        if (ok) return;
        this.queued.Remove(entry.InternalName);
        this.retryAt[entry.InternalName] = now.AddMinutes(2);
    }

    public void RetainWaiting(string[] names)
    {
        var keep = new HashSet<string>(names, StringComparer.Ordinal);
        var count = this.waiting.Count;
        for (var i = 0; i < count; i++)
        {
            var entry = this.waiting.Dequeue();
            if (keep.Contains(entry.InternalName)) this.waiting.Enqueue(entry);
            else this.queued.Remove(entry.InternalName);
        }
    }

    public void Reset()
    {
        this.waiting.Clear();
        this.queued.Clear();
        this.retryAt.Clear();
    }
}
