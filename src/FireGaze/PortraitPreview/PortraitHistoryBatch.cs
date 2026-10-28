namespace FireGaze.PortraitPreview;

/// <summary>Groups native UI samples into one mouse gesture or one quiet keyboard/input burst.</summary>
internal sealed class PortraitHistoryBatch
{
    private readonly PortraitHistoryStore store;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan idle;
    private PortraitHistoryKey? key;
    private PortraitPhotoSource? source;
    private string? snapshot;
    private DateTimeOffset lastActivity;
    private bool pointerDown;
    private bool keyDown;
    private string label = "编辑肖像";

    public PortraitHistoryBatch(PortraitHistoryStore store, Func<DateTimeOffset>? clock = null, TimeSpan? idle = null)
    {
        this.store = store;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.idle = idle ?? TimeSpan.FromMilliseconds(500);
        if (this.idle < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idle));
    }

    public PortraitHistoryWriteResult? Observe(PortraitHistoryKey observedKey, string snapshotJSON, bool pointerDown, bool keyDown, string label = "编辑肖像")
        => ObserveCore(observedKey, snapshotJSON, null, pointerDown, keyDown, label);

    public PortraitHistoryWriteResult? Observe(PortraitHistoryKey observedKey, string snapshotJSON, PortraitPhotoSource source,
        bool pointerDown, bool keyDown, string label = "编辑肖像")
        => ObserveCore(observedKey, snapshotJSON, source, pointerDown, keyDown, label);

    private PortraitHistoryWriteResult? ObserveCore(PortraitHistoryKey observedKey, string snapshotJSON, PortraitPhotoSource? observedSource,
        bool pointerDown, bool keyDown, string label)
    {
        var releasedPointer = this.pointerDown && !pointerDown;
        var releasedKey = this.keyDown && !keyDown;
        PortraitHistoryWriteResult? result = null;
        if (key is { } previous && (previous != observedKey || source != observedSource))
        {
            result = Flush();
            if (result is { Status: not (PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate) })
                return result;
        }
        var changed = snapshot != snapshotJSON;
        key = observedKey;
        source = observedSource;
        snapshot = snapshotJSON;
        this.label = label;
        if (changed || pointerDown || keyDown || releasedPointer || releasedKey) lastActivity = clock();
        this.pointerDown = pointerDown;
        this.keyDown = keyDown;
        if (releasedPointer && !pointerDown && !keyDown) result = Merge(result, Flush());
        return result;
    }

    private static PortraitHistoryWriteResult? Merge(PortraitHistoryWriteResult? earlier, PortraitHistoryWriteResult? later)
    {
        if (earlier is null) return later;
        if (later is null) return earlier;
        if (earlier.Status is not (PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate)) return earlier;
        if (later.Status is not (PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate)) return later;
        return later.Status == PortraitHistoryWriteStatus.Added ? later : earlier;
    }

    public PortraitHistoryWriteResult? AdvanceInput(bool pointerDown, bool keyDown)
    {
        if (key is not { } current || snapshot is null) return null;
        return ObserveCore(current, snapshot, source, pointerDown, keyDown, label);
    }

    public PortraitHistoryWriteResult? FlushIdle()
    {
        if (snapshot is null || pointerDown || keyDown || clock() - lastActivity < idle) return null;
        return Flush();
    }

    public PortraitHistoryWriteResult? Flush()
    {
        if (key is not { } current || snapshot is null) return null;
        var result = source is { } knownSource ? store.Record(current, snapshot, knownSource, label)
            : store.Record(current, snapshot, label);
        if (result.Status is PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate)
            Cancel();
        return result;
    }

    public void Cancel()
    {
        key = null;
        source = null;
        snapshot = null;
        pointerDown = false;
        keyDown = false;
        label = "编辑肖像";
    }
}
