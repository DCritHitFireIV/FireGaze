namespace FireGaze.PortraitPreview;

internal sealed record PortraitThumbnailCaptureTicket(long Generation, string Owner, long RecordID, string StateKey);

/// <summary>Read-only sampling never changes an editor to obtain a historical image.</summary>
internal sealed class PortraitThumbnailCaptureGate
{
    private readonly Func<DateTimeOffset> clock;
    private readonly TimeSpan settle;
    private string owner = "";
    private long recordID;
    private string stateKey = "";
    private DateTimeOffset since;
    private long generation;
    private bool ready;
    private bool captured;
    private PortraitThumbnailCaptureTicket? pending;

    public PortraitThumbnailCaptureGate(Func<DateTimeOffset>? clock = null, TimeSpan? settle = null)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.settle = settle ?? TimeSpan.FromMilliseconds(200);
        if (this.settle < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(settle));
    }

    public bool Observe(string observedOwner, long observedRecordID, string observedKey, bool isReady)
    {
        isReady &= observedRecordID > 0 && !string.IsNullOrEmpty(observedOwner) && !string.IsNullOrEmpty(observedKey);
        if (owner != observedOwner || recordID != observedRecordID || stateKey != observedKey || ready != isReady)
        {
            generation++;
            owner = observedOwner;
            recordID = observedRecordID;
            stateKey = observedKey;
            ready = isReady;
            captured = false;
            since = clock();
        }
        return ready && !captured && pending is null && clock() - since >= settle;
    }

    public PortraitThumbnailCaptureTicket? Begin()
    {
        if (!ready || captured || pending is not null || clock() - since < settle) return null;
        return pending = new(generation, owner, recordID, stateKey);
    }

    public bool CanAccept(PortraitThumbnailCaptureTicket ticket)
        => ready && pending == ticket && ticket.Generation == generation
            && ticket.Owner == owner && ticket.RecordID == recordID && ticket.StateKey == stateKey;

    public void Complete(PortraitThumbnailCaptureTicket ticket, bool success)
    {
        var accepted = CanAccept(ticket);
        if (pending == ticket) pending = null;
        if (!accepted) return;
        captured = success;
        if (!success) since = clock();
    }

    public void Reset()
    {
        generation++;
        owner = stateKey = "";
        recordID = 0;
        ready = captured = false;
        // An in-flight GPU copy still owns its resources until its completion is observed.
        // Forget the ticket's eligibility, not its resource lifecycle.
        pending = null;
    }
}
