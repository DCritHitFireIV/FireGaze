namespace FireGaze.PortraitPreview;

/// <summary>Groups native UI samples into one mouse gesture or one quiet keyboard/input burst.</summary>
internal sealed class PortraitHistoryBatch
{
    private readonly PortraitHistoryStore store;
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan idle;
    private PortraitHistoryKey? key;
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
    {
        var releasedPointer = this.pointerDown && !pointerDown;
        var releasedKey = this.keyDown && !keyDown;
        PortraitHistoryWriteResult? result = null;
        if (key is { } previous && previous != observedKey) result = Flush();
        var changed = snapshot != snapshotJSON;
        key = observedKey;
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
        return Observe(current, snapshot, pointerDown, keyDown, label);
    }

    public PortraitHistoryWriteResult? FlushIdle()
    {
        if (snapshot is null || pointerDown || keyDown || clock() - lastActivity < idle) return null;
        return Flush();
    }

    public PortraitHistoryWriteResult? Flush()
    {
        if (key is not { } current || snapshot is null) return null;
        var result = store.Record(current, snapshot, label);
        Cancel();
        return result;
    }

    public void Cancel()
    {
        key = null;
        snapshot = null;
        pointerDown = false;
        keyDown = false;
        label = "编辑肖像";
    }
}
