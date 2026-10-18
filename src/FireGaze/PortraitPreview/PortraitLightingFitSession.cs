namespace FireGaze.PortraitPreview;

/// <summary>
/// EditorIdentity must identify one editor instance/generation and local character. FixedStateKey must
/// include every fixed rendering field (pose/time, camera, appearance, lights' direction/brightness,
/// background/decoration, crop/texture generation), excluding only the six controlled RGB values.
/// </summary>
internal sealed record PortraitLightingFitState(string EditorIdentity, string FixedStateKey, int Width, int Height,
    IReadOnlyList<int> Parameters, IReadOnlyList<PortraitNativeLightRange> Ranges);

/// <summary>CPU-owned straight-alpha pixels only. No texture, native pointer or disposable resource may escape here.</summary>
internal sealed record PortraitLightingFitCapture(PortraitLightingFitState State, IReadOnlyList<PortraitLightRgba> Pixels);

internal interface IPortraitLightingFitSessionAdapter
{
    bool IsFrameworkThread { get; }
    bool TryRead(out PortraitLightingFitState state);
    /// <summary>Must prove the original editor/source generation is destroyed. Temporary unreadability is not proof.</summary>
    bool IsEditorOwnerDestroyed(string editorIdentity) => false;

    /// <summary>
    /// On Framework, compare the entire expected state immediately before applying only the six RGB
    /// fields to the current state. False must not mutate anything. Never apply an old full snapshot.
    /// The adapter must read back/verify the actual native values before returning true.
    /// </summary>
    bool TrySet(PortraitLightingFitState expected, IReadOnlyList<int> desired);

    /// <summary>
    /// All ordinary sampling-history observers must be suppressed until false, while native save
    /// protection remains in force. This call must not generate an edit/history entry itself.
    /// </summary>
    void SetSamplingHistorySuppressed(bool suppressed);

    /// <summary>
    /// Schedule one settled real render/readback for expected. No worker-thread native writes.
    /// Validate identity/state before and after capture. Returned data must be fully CPU-owned.
    /// Keep a single-flight guard for the adapter's lifetime, including editor close/reopen.
    /// Session cancellation does not cancel this task or wait for Present; the adapter must release
    /// late GPU resources itself. Do not rely on unverified public capture cancellation semantics.
    /// </summary>
    Task<PortraitLightingFitCapture> CaptureAsync(PortraitLightingFitState expected);

    /// <summary>
    /// Atomically verify current expected, append exactly one baseline-to-current lighting edit,
    /// and publish its history baseline despite sampling suppression. False appends no record.
    /// All non-lighting fields come from the current state; do not restore an old full snapshot.
    /// </summary>
    bool TryCommitHistory(PortraitLightingFitState expected, IReadOnlyList<int> baseline);
}

internal sealed record PortraitLightingFitSessionOptions(int ProbeStep = 16, TimeSpan? CaptureTimeout = null,
    PortraitLightFitOptions? FitOptions = null);

internal enum PortraitLightingFitSessionPhase { Idle, CapturingBaseline, CapturingProbe, CapturingCandidate, Completed, Stopped }
internal enum PortraitLightingFitSessionOutcome
{
    None, Accepted, Cancelled, TimedOut, ExternalChange, CaptureFailed, CalibrationRejected, CandidateRejected, CommitFailed, AdapterFailed,
}

/// <summary>
/// Framework-polled, nonblocking sampling transaction. The eight captures are baseline, six isolated
/// axis probes and an independently verified candidate. At most one task is in flight. A stopped task
/// remains a start barrier until drained; closing/cancelling never waits for a GPU/Present callback.
/// </summary>
internal sealed class PortraitLightingFitSession
{
    private readonly IPortraitLightingFitSessionAdapter adapter;
    private readonly TimeProvider time;
    private PortraitLightingFitState? baseline;
    private PortraitLightingFitState? lastOwned;
    private PortraitLightingFitState? captureExpected;
    private Task<PortraitLightingFitCapture>? pendingCapture;
    private DateTimeOffset captureStarted;
    private PortraitLightingFitSessionOptions options = new();
    private PortraitColorToneTarget? target;
    private bool[] subjectMask = [];
    private PortraitLightPixelSelection? selection;
    private PortraitLightRgb[] baselinePixels = [];
    private PortraitLightRgb[] targetPixels = [];
    private readonly List<PortraitLightSample> probes = [];
    private int probeIndex;
    private bool suppressionRequested;
    private bool pendingCleanup;

    public PortraitLightingFitSession(IPortraitLightingFitSessionAdapter adapter, TimeProvider? time = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        this.time = time ?? TimeProvider.System;
    }

    public PortraitLightingFitSessionPhase Phase { get; private set; }
    public PortraitLightingFitSessionOutcome Outcome { get; private set; }
    public PortraitLightingFitFailure FitFailure { get; private set; }
    public PortraitLightFitProposal? Proposal { get; private set; }
    public PortraitLightingFitVerification? Verification { get; private set; }
    public bool IsActive => Phase is PortraitLightingFitSessionPhase.CapturingBaseline
        or PortraitLightingFitSessionPhase.CapturingProbe or PortraitLightingFitSessionPhase.CapturingCandidate;
    public bool HasPendingCapture => pendingCapture is not null;
    public bool RollbackComplete { get; private set; } = true;
    public bool HasPendingCleanup => pendingCleanup;
    public bool HistorySuppressionReleased { get; private set; } = true;

    public bool TryStart(PortraitColorToneTarget colorTarget, IReadOnlyList<bool> mask, PortraitLightingFitSessionOptions? settings = null)
    {
        RequireFramework();
        DrainLateCaptureIfComplete();
        if (!IsActive && pendingCleanup) AttemptCleanup();
        if (!IsActive && suppressionRequested && !pendingCleanup) ReleaseSuppression();
        if (IsActive || pendingCapture is not null || suppressionRequested || pendingCleanup || colorTarget is null || mask is null) return false;
        var requestedOptions = settings ?? new();
        var timeout = requestedOptions.CaptureTimeout ?? TimeSpan.FromSeconds(10);
        if (requestedOptions.ProbeStep is < 1 or > 255 || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2)
            || !TryRead(out var initial) || (long)initial!.Width * initial.Height != mask.Count || !mask.Any(value => value)) return false;
        options = requestedOptions;
        baseline = Copy(initial);
        lastOwned = Copy(initial);
        target = colorTarget;
        subjectMask = mask.ToArray();
        selection = null;
        baselinePixels = [];
        targetPixels = [];
        probes.Clear();
        probeIndex = 0;
        Proposal = null;
        Verification = null;
        FitFailure = PortraitLightingFitFailure.None;
        Outcome = PortraitLightingFitSessionOutcome.None;
        RollbackComplete = true;
        HistorySuppressionReleased = false;
        Phase = PortraitLightingFitSessionPhase.CapturingBaseline;
        try
        {
            suppressionRequested = true;
            adapter.SetSamplingHistorySuppressed(true);
        }
        catch { Stop(PortraitLightingFitSessionOutcome.AdapterFailed); return false; }
        return BeginCapture();
    }

    public void Poll()
    {
        RequireFramework();
        if (!IsActive) { DrainLateCaptureIfComplete(); if (pendingCleanup) AttemptCleanup(); if (!pendingCleanup) ReleaseSuppression(); return; }
        if (!TryRead(out var current)) { Stop(PortraitLightingFitSessionOutcome.AdapterFailed); return; }
        if (!Same(current!, lastOwned!)) { Stop(PortraitLightingFitSessionOutcome.ExternalChange); return; }
        var capture = pendingCapture;
        if (capture is null) { Stop(PortraitLightingFitSessionOutcome.AdapterFailed); return; }
        if (!capture.IsCompleted)
        {
            if (time.GetUtcNow() - captureStarted >= (options.CaptureTimeout ?? TimeSpan.FromSeconds(10)))
                Stop(PortraitLightingFitSessionOutcome.TimedOut);
            return;
        }
        pendingCapture = null;
        PortraitLightingFitCapture frame;
        try { frame = capture.GetAwaiter().GetResult(); }
        catch { Stop(PortraitLightingFitSessionOutcome.CaptureFailed); return; }
        if (frame?.State is null || frame.Pixels is null || !Valid(frame.State) || !Same(frame.State, captureExpected!)
            || frame.Pixels.Count != (long)frame.State.Width * frame.State.Height)
        { Stop(PortraitLightingFitSessionOutcome.CaptureFailed); return; }
        if (Phase == PortraitLightingFitSessionPhase.CapturingBaseline)
        {
            if (!PortraitLightPixelSelection.TryCreate(frame.Pixels, subjectMask, PortraitLightingFit.MaximumPixels,
                    out selection, out var selectionFailure) || !selection!.TryExtract(frame.Pixels, out var pixels)
                || !target!.TryMap(pixels!, out var mapped))
            {
                FitFailure = selectionFailure;
                Stop(PortraitLightingFitSessionOutcome.CaptureFailed);
                return;
            }
            baselinePixels = pixels!;
            targetPixels = mapped!;
            BeginProbe();
            return;
        }
        if (selection is null || !selection.TryExtract(frame.Pixels, out var extracted))
        { Stop(PortraitLightingFitSessionOutcome.CaptureFailed); return; }
        if (Phase == PortraitLightingFitSessionPhase.CapturingProbe)
        {
            probes.Add(new(captureExpected!.Parameters.ToArray(), extracted!));
            if (!ApplyOwned(baseline!.Parameters)) return;
            probeIndex++;
            if (probeIndex < 6) BeginProbe();
            else BeginCandidate();
            return;
        }
        Verification = Proposal!.ValidateRenderedCandidate(new(captureExpected!.Parameters.ToArray(), extracted!));
        if (!Verification.Accepted)
        {
            FitFailure = Verification.Failure;
            Stop(PortraitLightingFitSessionOutcome.CandidateRejected);
            return;
        }
        // A capture result is not permission to commit after a concurrent Framework-side edit.
        if (!TryRead(out current) || !Same(current!, lastOwned!))
        { Stop(PortraitLightingFitSessionOutcome.ExternalChange); return; }
        bool committed;
        try { committed = adapter.TryCommitHistory(Copy(current!), Array.AsReadOnly(baseline!.Parameters.ToArray())); }
        catch { committed = false; }
        if (!committed) { Stop(PortraitLightingFitSessionOutcome.CommitFailed); return; }
        Outcome = PortraitLightingFitSessionOutcome.Accepted;
        Phase = PortraitLightingFitSessionPhase.Completed;
        ReleaseSuppression();
    }

    public void Cancel()
    {
        RequireFramework();
        if (IsActive) Stop(PortraitLightingFitSessionOutcome.Cancelled);
        else { DrainLateCaptureIfComplete(); if (pendingCleanup) AttemptCleanup(); if (!pendingCleanup) ReleaseSuppression(); }
    }

    private void BeginProbe()
    {
        var desired = baseline!.Parameters.ToArray();
        var range = baseline.Ranges[probeIndex];
        var step = Math.Max(range.Quantum, options.ProbeStep / range.Quantum * range.Quantum);
        var positiveRoom = range.Maximum - desired[probeIndex];
        var negativeRoom = desired[probeIndex] - range.Minimum;
        var delta = positiveRoom >= step ? step : negativeRoom >= step ? -step
            : positiveRoom >= negativeRoom ? positiveRoom : -negativeRoom;
        desired[probeIndex] += delta;
        if (!ApplyOwned(desired)) return;
        Phase = PortraitLightingFitSessionPhase.CapturingProbe;
        BeginCapture();
    }

    private void BeginCandidate()
    {
        if (!PortraitLightingFit.TryPropose(new(new(baseline!.Parameters, baselinePixels), baseline.Ranges, probes.ToArray(), targetPixels),
                options.FitOptions ?? new(), out var proposal, out var failure))
        {
            FitFailure = failure;
            Stop(PortraitLightingFitSessionOutcome.CalibrationRejected);
            return;
        }
        Proposal = proposal;
        if (!ApplyOwned(proposal!.CandidateParameters)) return;
        Phase = PortraitLightingFitSessionPhase.CapturingCandidate;
        BeginCapture();
    }

    private bool BeginCapture()
    {
        if (pendingCapture is not null) { Stop(PortraitLightingFitSessionOutcome.AdapterFailed); return false; }
        captureExpected = Copy(lastOwned!);
        captureStarted = time.GetUtcNow();
        try { pendingCapture = adapter.CaptureAsync(Copy(captureExpected)); }
        catch { Stop(PortraitLightingFitSessionOutcome.AdapterFailed); return false; }
        if (pendingCapture is not null) return true;
        Stop(PortraitLightingFitSessionOutcome.AdapterFailed);
        return false;
    }

    private bool ApplyOwned(IReadOnlyList<int> desired)
    {
        if (!TryRead(out var current)) { Stop(PortraitLightingFitSessionOutcome.AdapterFailed); return false; }
        if (!Same(current!, lastOwned!)) { Stop(PortraitLightingFitSessionOutcome.ExternalChange); return false; }
        bool applied;
        try { applied = adapter.TrySet(Copy(current!), Array.AsReadOnly(desired.ToArray())); }
        catch { applied = false; }
        if (!applied) { Stop(PortraitLightingFitSessionOutcome.AdapterFailed); return false; }
        lastOwned = Copy(current! with { Parameters = desired.ToArray() });
        return true;
    }

    private void Stop(PortraitLightingFitSessionOutcome outcome)
    {
        Outcome = outcome;
        Phase = PortraitLightingFitSessionPhase.Stopped;
        pendingCleanup = !AttemptCleanup();
        if (!pendingCleanup) ReleaseSuppression();
        // Observe a late fault without a closure over this session, native writes, or a blocking wait.
        if (pendingCapture is { IsCompleted: false } late)
            _ = late.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        DrainLateCaptureIfComplete();
    }

    private bool AttemptCleanup()
    {
        if (baseline is null || lastOwned is null) { RollbackComplete = true; pendingCleanup = false; return true; }
        if (!TryRead(out var current))
        {
            try
            {
                if (adapter.IsEditorOwnerDestroyed(baseline.EditorIdentity))
                { RollbackComplete = true; pendingCleanup = false; return true; }
            }
            catch { /* An unreadable owner remains a cleanup obligation. */ }
            RollbackComplete = false; pendingCleanup = true; return false;
        }
        // The original editor was destroyed/replaced. Never write an old snapshot to the new instance;
        // there is no owned field left to restore, so the old cleanup obligation is safely abandoned.
        if (current!.EditorIdentity != baseline.EditorIdentity)
        { RollbackComplete = true; pendingCleanup = false; return true; }
        var desired = current.Parameters.ToArray();
        for (var axis = 0; axis < 6; axis++)
            if (current.Parameters[axis] == lastOwned.Parameters[axis] && current.Ranges[axis].Contains(baseline.Parameters[axis]))
                desired[axis] = baseline.Parameters[axis];
        if (desired.SequenceEqual(current.Parameters)) { RollbackComplete = true; pendingCleanup = false; return true; }
        try { RollbackComplete = adapter.TrySet(Copy(current), Array.AsReadOnly(desired)); }
        catch { RollbackComplete = false; }
        pendingCleanup = !RollbackComplete;
        return RollbackComplete;
    }

    private void ReleaseSuppression()
    {
        if (!suppressionRequested) return;
        try
        {
            adapter.SetSamplingHistorySuppressed(false);
            HistorySuppressionReleased = true;
            suppressionRequested = false;
        }
        catch { HistorySuppressionReleased = false; }
    }

    private void DrainLateCaptureIfComplete()
    {
        if (IsActive || pendingCapture is not { IsCompleted: true } late) return;
        pendingCapture = null;
        try { _ = late.GetAwaiter().GetResult(); } catch { /* A late CPU result/fault cannot affect the editor. */ }
    }

    private bool TryRead(out PortraitLightingFitState? state)
    {
        state = null;
        try
        {
            if (!adapter.TryRead(out var current) || !Valid(current)) return false;
            state = Copy(current);
            return true;
        }
        catch { return false; }
    }

    private static bool Valid(PortraitLightingFitState state)
    {
        if (state is null || string.IsNullOrEmpty(state.EditorIdentity) || string.IsNullOrEmpty(state.FixedStateKey)
            || state.Width <= 0 || state.Height <= 0 || (long)state.Width * state.Height > 16777216
            || state.Parameters is not { Count: 6 } || state.Ranges is not { Count: 6 }) return false;
        for (var i = 0; i < 6; i++) if (!state.Ranges[i].Contains(state.Parameters[i])) return false;
        return true;
    }

    private static PortraitLightingFitState Copy(PortraitLightingFitState state) => state with
    {
        Parameters = Array.AsReadOnly(state.Parameters.ToArray()),
        Ranges = Array.AsReadOnly(state.Ranges.ToArray()),
    };

    private static bool Same(PortraitLightingFitState a, PortraitLightingFitState b) =>
        a.EditorIdentity == b.EditorIdentity && a.FixedStateKey == b.FixedStateKey && a.Width == b.Width && a.Height == b.Height
        && a.Parameters.SequenceEqual(b.Parameters) && a.Ranges.SequenceEqual(b.Ranges);

    private void RequireFramework()
    {
        if (!adapter.IsFrameworkThread) throw new InvalidOperationException("Lighting fit session entry points require the Framework thread.");
    }
}
