namespace FireGaze.PortraitPreview;

/// <summary>Detached qualification supplied freshly on Framework; addresses are equality tokens only.</summary>
internal readonly record struct PortraitNativeLiveContext(PortraitIdentity Editor, PortraitCaptureOwner Owner,
    bool SessionOwnsEditor, bool CanBlockSave, string CaptureMode, bool CapturesDrained, bool LightingFitBusy);

internal enum PortraitNativeLiveStopReason
{
    Disabled, UserClosed, OwnerChanged, TerritoryChanged, LoggedOut, UpdateFailed,
    SourceInvalidated, AcquireFailed, InvalidEnable, Disposed,
}

/// <summary>
/// Owns one explicit live-image attempt, never an editor, camera, light or capture transaction.
/// The caller owns the source and disposes it after this controller. Enable/Update run on Framework;
/// Draw borrows managed image descriptors only. Every stop requires another explicit TryEnable.
/// </summary>
internal sealed class PortraitNativeLiveController : IDisposable
{
    private readonly IPortraitNativeLiveSource source;
    private readonly PortraitNativeLiveView view;
    private readonly Func<PortraitNativeLiveContext> readContext;
    private readonly object gate = new();
    private PortraitIdentity boundEditor;
    private PortraitCaptureOwner boundOwner;
    private bool enabled;
    private bool frameReady;
    private bool disposed;
    private long revision;
    private PortraitNativeLiveStopReason stopReason;

    public PortraitNativeLiveController(IPortraitNativeLiveSource source, Func<PortraitNativeLiveContext> readContext)
    {
        this.source = source;
        this.readContext = readContext;
        view = new(source);
        source.Invalidated += OnSourceInvalidated;
    }

    public bool IsEnabled { get { lock (gate) return enabled; } }
    public PortraitNativeLiveStopReason StopReason { get { lock (gate) return stopReason; } }

    public bool TryEnable(PortraitIdentity requested)
    {
        long ticket;
        lock (gate)
        {
            if (disposed) return false;
            StopLocked(PortraitNativeLiveStopReason.Disabled);
            ticket = revision;
        }
        try
        {
            if (!requested.IsValid || requested.Generation == 0 || !source.IsFrameworkThread)
            {
                StopIfCurrent(ticket, PortraitNativeLiveStopReason.InvalidEnable);
                return false;
            }
            var context = readContext();
            if (!Matches(context, requested, context.Owner))
            {
                StopIfCurrent(ticket, PortraitNativeLiveStopReason.InvalidEnable);
                return false;
            }
            lock (gate)
            {
                // A native callback in qualification may have invalidated or disposed this attempt.
                if (disposed || revision != ticket) return false;
                boundEditor = requested;
                boundOwner = context.Owner;
                enabled = true;
                view.SetEnabled(true);
                return true;
            }
        }
        catch (Exception)
        {
            StopIfCurrent(ticket, PortraitNativeLiveStopReason.InvalidEnable);
            return false;
        }
    }

    public void Update()
    {
        long ticket;
        PortraitIdentity editor;
        PortraitCaptureOwner owner;
        lock (gate)
        {
            if (disposed || !enabled) return;
            ticket = revision;
            editor = boundEditor;
            owner = boundOwner;
            // The new frame is not available to Draw until both context and descriptor are verified.
            frameReady = false;
        }
        try
        {
            if (!source.IsFrameworkThread)
            {
                StopIfCurrent(ticket, PortraitNativeLiveStopReason.UpdateFailed);
                return;
            }
            var before = readContext();
            if (!IsCurrent(ticket)) return;
            if (!Matches(before, editor, owner))
            {
                StopIfCurrent(ticket, PortraitNativeLiveStopReason.OwnerChanged);
                return;
            }
            view.Update();
            if (!IsCurrent(ticket)) return;
            // The view intentionally permits retries. This controller converts any failed update
            // into a final stop for this attempt, without changing the acquire-only view contract.
            using (var verified = view.TryBorrow(out var borrow) ? borrow : null)
            {
                if (verified is null || !verified.TryGetImage(out var image)
                    || image.Key.Editor != editor || image.Key.Owner != owner)
                {
                    StopIfCurrent(ticket, PortraitNativeLiveStopReason.AcquireFailed);
                    return;
                }
            }
            var after = readContext();
            if (!IsCurrent(ticket)) return;
            if (!Matches(after, editor, owner))
            {
                StopIfCurrent(ticket, PortraitNativeLiveStopReason.OwnerChanged);
                return;
            }
            lock (gate)
                if (!disposed && enabled && revision == ticket) frameReady = true;
        }
        catch (Exception)
        {
            StopIfCurrent(ticket, PortraitNativeLiveStopReason.UpdateFailed);
        }
    }

    public bool TryBorrow(out PortraitNativeLiveBorrow? borrow)
    {
        lock (gate)
        {
            borrow = null;
            return !disposed && enabled && frameReady && view.TryBorrow(out borrow);
        }
    }

    /// <summary>Call at close intent, owner loss, territory change, logout and the outer update catch.</summary>
    public void Stop(PortraitNativeLiveStopReason reason)
    {
        lock (gate)
            if (!disposed) StopLocked(reason);
    }

    private void OnSourceInvalidated() => Stop(PortraitNativeLiveStopReason.SourceInvalidated);

    private bool IsCurrent(long ticket)
    {
        lock (gate) return !disposed && enabled && revision == ticket;
    }

    private void StopIfCurrent(long ticket, PortraitNativeLiveStopReason reason)
    {
        lock (gate)
            if (!disposed && revision == ticket) StopLocked(reason);
    }

    // Called under gate; disabling the view invalidates outstanding descriptors while its
    // owned texture remains retained until all outstanding Draw borrowers have returned.
    private void StopLocked(PortraitNativeLiveStopReason reason)
    {
        revision++;
        enabled = false;
        frameReady = false;
        boundEditor = default;
        boundOwner = default;
        stopReason = reason;
        view.SetEnabled(false);
    }

    private static bool Matches(PortraitNativeLiveContext context, PortraitIdentity editor, PortraitCaptureOwner owner)
        => editor.IsValid && editor.Generation != 0 && context.Editor == editor && owner.IsValid
            && context.Owner == owner && context.SessionOwnsEditor && context.CanBlockSave
            && context.CaptureMode == "off" && context.CapturesDrained && !context.LightingFitBusy;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            StopLocked(PortraitNativeLiveStopReason.Disposed);
            view.Dispose();
        }
        source.Invalidated -= OnSourceInvalidated;
    }

    public Task DrainPhysicalAsync() => view.DrainPhysicalAsync();
}
