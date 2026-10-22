namespace FireGaze.PortraitPreview;

/// <summary>Detached qualification supplied freshly on Framework; addresses are equality tokens only.</summary>
internal readonly record struct PortraitNativeLiveContext(PortraitIdentity Editor, PortraitCaptureOwner Owner,
    bool SessionOwnsEditor, bool CanBlockSave, string CaptureMode, bool CapturesDrained, bool LightingFitBusy);

internal enum PortraitNativeLiveStopReason
{
    Disabled, UserClosed, OwnerChanged, TerritoryChanged, LoggedOut, UpdateFailed,
    SourceInvalidated, AcquireFailed, InvalidEnable, ContextBusy, Disposed,
}

/// <summary>
/// Owns one explicit live-image attempt, never an editor, camera, light or capture transaction.
/// The caller owns the source and disposes it after this controller. Enable/Update run on Framework;
/// Draw borrows managed image descriptors only. Transient misses retry within the same owned preview lease;
/// close, lifecycle invalidation and ownership loss permanently cancel that lease's retry intent.
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
    private long startedSessionRevision;
    private readonly Func<long> readMonotonicMilliseconds;
    private PortraitIdentity automaticEditor;
    private PortraitCaptureOwner automaticOwner;
    private bool automaticAllowed;
    private long nextAutomaticAttempt;
    private int automaticFailures;

    public PortraitNativeLiveController(IPortraitNativeLiveSource source, Func<PortraitNativeLiveContext> readContext,
        Func<long>? readMonotonicMilliseconds = null)
    {
        this.source = source;
        this.readContext = readContext;
        this.readMonotonicMilliseconds = readMonotonicMilliseconds ?? (() => Environment.TickCount64);
        view = new(source);
        source.Invalidated += OnSourceInvalidated;
    }

    public bool IsEnabled { get { lock (gate) return enabled; } }
    /// <summary>Framework maintenance for one preview lease, with silent bounded-rate retries.</summary>
    public bool TryStartSessionOnce(long sessionRevision, PortraitIdentity requested)
    {
        long ticket;
        lock (gate)
        {
            if (disposed || sessionRevision <= 0 || sessionRevision < startedSessionRevision) return false;
            if (sessionRevision > startedSessionRevision)
            {
                StopLocked(PortraitNativeLiveStopReason.Disabled);
                startedSessionRevision = sessionRevision;
                automaticEditor = requested;
                automaticOwner = default;
                automaticAllowed = true;
                automaticFailures = 0;
                nextAutomaticAttempt = readMonotonicMilliseconds();
            }
            if (!automaticAllowed || enabled || readMonotonicMilliseconds() < nextAutomaticAttempt) return false;
            if (!requested.IsValid || requested.Generation == 0 || requested != automaticEditor)
            {
                automaticAllowed = false;
                StopLocked(PortraitNativeLiveStopReason.OwnerChanged);
                return false;
            }
            ticket = revision;
        }
        try
        {
            if (!source.IsFrameworkThread)
            {
                StopIfCurrent(ticket, PortraitNativeLiveStopReason.UpdateFailed);
                return false;
            }
            var context = readContext();
            lock (gate)
            {
                if (disposed || revision != ticket || !automaticAllowed) return false;
                if (!context.Owner.IsValid || !context.CanBlockSave || automaticOwner.IsValid && context.Owner != automaticOwner
                    || context.Editor != automaticEditor && context.Editor != default
                    || context.Editor == automaticEditor && !context.SessionOwnsEditor)
                {
                    automaticAllowed = false;
                    StopLocked(PortraitNativeLiveStopReason.OwnerChanged);
                    return false;
                }
                automaticOwner = context.Owner;
                if (!Matches(context, automaticEditor, automaticOwner) || !view.IsPhysicallyIdle)
                {
                    StopLocked(PortraitNativeLiveStopReason.ContextBusy);
                    ScheduleAutomaticRetryLocked(PortraitNativeLiveStopReason.ContextBusy);
                    return false;
                }
            }
            if (!TryEnable(requested)) return false;
            lock (gate)
            {
                // TryEnable reads context again. Neither that callback nor a source invalidation
                // may follow a changed owner, replace the editor or resurrect a closed lease.
                if (disposed || !automaticAllowed || boundEditor != automaticEditor || boundOwner != automaticOwner)
                {
                    automaticAllowed = false;
                    StopLocked(PortraitNativeLiveStopReason.OwnerChanged);
                    return false;
                }
                return enabled;
            }
        }
        catch (Exception)
        {
            StopIfCurrent(ticket, PortraitNativeLiveStopReason.UpdateFailed);
            return false;
        }
    }
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
                StopIfCurrent(ticket, Owns(before, editor, owner) || TemporarilyUnresolved(before, owner)
                    ? PortraitNativeLiveStopReason.ContextBusy : PortraitNativeLiveStopReason.OwnerChanged);
                return;
            }
            view.Update();
            if (!IsCurrent(ticket)) return;
            // Failed updates always retire the old descriptor. Only the same active preview
            // lease may retry later, after a fresh owner/context check and physical retirement.
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
                StopIfCurrent(ticket, Owns(after, editor, owner) || TemporarilyUnresolved(after, owner)
                    ? PortraitNativeLiveStopReason.ContextBusy : PortraitNativeLiveStopReason.OwnerChanged);
                return;
            }
            lock (gate)
                if (!disposed && enabled && revision == ticket)
                {
                    frameReady = true;
                    automaticFailures = 0;
                }
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
        {
            automaticAllowed = false;
            if (!disposed) StopLocked(reason);
        }
    }

    private void OnSourceInvalidated() => Stop(PortraitNativeLiveStopReason.SourceInvalidated);

    private bool IsCurrent(long ticket)
    {
        lock (gate) return !disposed && enabled && revision == ticket;
    }

    private void StopIfCurrent(long ticket, PortraitNativeLiveStopReason reason)
    {
        lock (gate)
            if (!disposed && revision == ticket)
            {
                StopLocked(reason);
                ScheduleAutomaticRetryLocked(reason);
            }
    }

    private void ScheduleAutomaticRetryLocked(PortraitNativeLiveStopReason reason)
    {
        if (!automaticAllowed) return;
        if (reason is not (PortraitNativeLiveStopReason.AcquireFailed or PortraitNativeLiveStopReason.InvalidEnable
            or PortraitNativeLiveStopReason.ContextBusy))
        {
            automaticAllowed = false;
            return;
        }
        var delay = 250L << Math.Min(automaticFailures, 3);
        automaticFailures = Math.Min(automaticFailures + 1, 3);
        nextAutomaticAttempt = readMonotonicMilliseconds() + delay;
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

    private static bool Owns(PortraitNativeLiveContext context, PortraitIdentity editor, PortraitCaptureOwner owner)
        => editor.IsValid && editor.Generation != 0 && context.Editor == editor && owner.IsValid
            && context.Owner == owner && context.SessionOwnsEditor && context.CanBlockSave;

    private static bool TemporarilyUnresolved(PortraitNativeLiveContext context, PortraitCaptureOwner owner)
        => context.Editor == default && owner.IsValid && context.Owner == owner && context.CanBlockSave;

    private static bool Matches(PortraitNativeLiveContext context, PortraitIdentity editor, PortraitCaptureOwner owner)
        => Owns(context, editor, owner) && context.CaptureMode == "off"
            && context.CapturesDrained && !context.LightingFitBusy;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            automaticAllowed = false;
            StopLocked(PortraitNativeLiveStopReason.Disposed);
            view.Dispose();
        }
        source.Invalidated -= OnSourceInvalidated;
    }

    public Task DrainPhysicalAsync() => view.DrainPhysicalAsync();
    internal bool IsPhysicallyIdle => view.IsPhysicallyIdle;
}
