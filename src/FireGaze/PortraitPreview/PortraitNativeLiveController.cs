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
    private long nextFrameAttempt;
    private int frameFailures;

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

    /// <summary>
    /// Re-arms automatic acquisition after the feature intentionally paused this
    /// controller for a source-scope transition within the same preview lease.
    /// This is only called by the owning feature after it has returned to the
    /// portrait source; ordinary Stop remains final until an explicit re-arm.
    /// </summary>
    internal void RearmAutomaticSession(long sessionRevision, PortraitIdentity requested)
    {
        lock (gate)
        {
            if (disposed || sessionRevision <= 0 || !requested.IsValid || requested.Generation == 0
                || sessionRevision < startedSessionRevision) return;
            if (sessionRevision > startedSessionRevision)
                startedSessionRevision = sessionRevision;
            automaticEditor = requested;
            automaticOwner = default;
            automaticAllowed = true;
            automaticFailures = 0;
            nextAutomaticAttempt = readMonotonicMilliseconds();
        }
    }
    public PortraitNativeLiveStopReason StopReason { get { lock (gate) return stopReason; } }

    public bool TryEnable(PortraitIdentity requested)
    {
        long ticket;
        PortraitCaptureOwner existingOwner;
        bool reuse;
        lock (gate)
        {
            if (disposed) return false;
            reuse = enabled && boundEditor == requested;
            existingOwner = boundOwner;
            if (!reuse) StopLocked(PortraitNativeLiveStopReason.Disabled);
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
            if (!Matches(context, requested, context.Owner) || reuse && context.Owner != existingOwner)
            {
                StopIfCurrent(ticket, PortraitNativeLiveStopReason.InvalidEnable);
                return false;
            }
            lock (gate)
            {
                // A native callback in qualification may have invalidated or disposed this attempt.
                if (disposed || revision != ticket) return false;
                if (reuse) return enabled;
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
            // Keep the already verified descriptor visible while a candidate is acquired.
            // The view atomically replaces it only after the post-acquire context check.
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
                if (Owns(before, editor, owner))
                {
                    // History photos temporarily exclude acquisition. They do not revoke
                    // this editor's independently retained managed display descriptor.
                    lock (gate) if (revision == ticket) stopReason = PortraitNativeLiveStopReason.ContextBusy;
                    return;
                }
                StopIfCurrent(ticket, Owns(before, editor, owner) || TemporarilyUnresolved(before, owner)
                    ? PortraitNativeLiveStopReason.ContextBusy : PortraitNativeLiveStopReason.OwnerChanged);
                return;
            }
            bool refreshed = false;
            if (readMonotonicMilliseconds() >= nextFrameAttempt)
                refreshed = view.UpdatePreservingCurrent(image =>
                {
                    if (!IsCurrent(ticket)) return false;
                    if (image.Key.Editor != editor || image.Key.Owner != owner)
                    {
                        StopIfCurrent(ticket, PortraitNativeLiveStopReason.AcquireFailed);
                        return false;
                    }
                    var after = readContext();
                    if (!IsCurrent(ticket)) return false;
                    if (Matches(after, editor, owner)) return true;
                    if (Owns(after, editor, owner)) return false;
                    StopIfCurrent(ticket, TemporarilyUnresolved(after, owner)
                        ? PortraitNativeLiveStopReason.ContextBusy : PortraitNativeLiveStopReason.OwnerChanged);
                    return false;
                });
            if (!IsCurrent(ticket)) return;
            if (!refreshed)
            {
                // A failed native lookup can itself reenter logout or editor replacement.
                // Keeping the prior descriptor requires the same final ownership proof
                // even when there was no candidate for the publication callback.
                var afterMiss = readContext();
                if (!IsCurrent(ticket)) return;
                if (!Owns(afterMiss, editor, owner))
                {
                    StopIfCurrent(ticket, TemporarilyUnresolved(afterMiss, owner)
                        ? PortraitNativeLiveStopReason.ContextBusy : PortraitNativeLiveStopReason.OwnerChanged);
                    return;
                }
            }
            // Initial misses have no image to keep. A same-session transient miss retains
            // only the prior verified descriptor; malformed frames still fail closed.
            using (var verified = view.TryBorrow(out var borrow) ? borrow : null)
            {
                if (verified is null || !verified.TryGetImage(out var image)
                    || image.Key.Editor != editor || image.Key.Owner != owner)
                {
                    StopIfCurrent(ticket, PortraitNativeLiveStopReason.AcquireFailed);
                    return;
                }
            }
            lock (gate)
                if (!disposed && enabled && revision == ticket)
                {
                    frameReady = true;
                    if (refreshed)
                    {
                        automaticFailures = frameFailures = 0;
                        nextFrameAttempt = 0;
                    }
                    else if (readMonotonicMilliseconds() >= nextFrameAttempt)
                    {
                        nextFrameAttempt = readMonotonicMilliseconds() + (250L << Math.Min(frameFailures, 3));
                        frameFailures = Math.Min(frameFailures + 1, 3);
                    }
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

    /// <summary>
    /// History photographs only read independently retained source SRVs and copy into their
    /// own outputs. A freshly owned, stable display may coexist with that specific operation;
    /// capture demand itself is deliberately not a requirement for physical retirement here.
    /// </summary>
    internal bool CanShareReadOnlyPhotoSource(PortraitIdentity editor, PortraitCaptureOwner owner)
    {
        long ticket;
        lock (gate)
        {
            if (disposed || !enabled || !frameReady || boundEditor != editor || boundOwner != owner) return false;
            ticket = revision;
        }
        try
        {
            if (!source.IsFrameworkThread) return false;
            var context = readContext();
            if (!Owns(context, editor, owner) || context.CaptureMode != "off" || context.LightingFitBusy) return false;
            lock (gate)
                return !disposed && enabled && frameReady && revision == ticket
                    && boundEditor == editor && boundOwner == owner
                    && view.CanShareReadOnlyPhotoSource(editor, owner);
        }
        catch (Exception) { return false; }
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
        nextFrameAttempt = 0;
        frameFailures = 0;
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
