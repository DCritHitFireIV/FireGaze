namespace FireGaze.PortraitPreview;

/// <summary>
/// A failed write reports every actually changed value while it still belongs to this owner.
/// Null OwnedState means the backend performed no mutation, or proved that owner was destroyed.
/// </summary>
internal sealed record PortraitLightingFitWriteResult(bool Applied, PortraitLightingFitState? OwnedState);

/// <summary>All calls except the returned CPU task are synchronous Framework operations.</summary>
internal interface IPortraitLightingFitBackend
{
    bool IsFrameworkThread { get; }
    bool TryRead(out PortraitLightingFitState state);
    bool IsEditorOwnerDestroyed(string owner);
    PortraitLightingFitWriteResult Write(PortraitLightingFitState expected, IReadOnlyList<int> desired);
    void SetHistorySuppressed(bool value);
    /// <summary>Own settled source references before returning. Always release late GPU resources.</summary>
    Task<IReadOnlyList<PortraitLightRgba>> CaptureAsync(PortraitLightingFitState state);
    bool CommitHistory(PortraitLightingFitState expected, IReadOnlyList<int> baseline);
}

/// <summary>
/// Lifetime-scoped adapter with retained partial-write cleanup and physical readback barriers.
/// Poll must continue after UI close until both barriers drain. Capture tasks contain CPU data only.
/// Not connected to the live Feature until its backend range and capture semantics are verified.
/// </summary>
internal sealed class PortraitLightingFitAdapter(IPortraitLightingFitBackend backend) : IPortraitLightingFitSessionAdapter
{
    private PortraitLightingFitState? cleanupBaseline;
    private PortraitLightingFitState? cleanupOwned;
    private bool[]? cleanupChannels;
    private PendingCapture? capture;
    public bool IsFrameworkThread => backend.IsFrameworkThread;
    public bool HasPendingCleanup => cleanupOwned is not null;
    public bool HasPendingNativeWrite => HasPendingCleanup;
    public bool HasPendingCapture => capture is not null;

    public bool TryRead(out PortraitLightingFitState state)
    {
        state = null!;
        if (!IsFrameworkThread || !Cleanup() || !backend.TryRead(out var current) || !Valid(current)) return false;
        state = Copy(current);
        return true;
    }

    public bool IsEditorOwnerDestroyed(string owner) => IsFrameworkThread && backend.IsEditorOwnerDestroyed(owner);

    public bool TrySet(PortraitLightingFitState expected, IReadOnlyList<int> desired)
    {
        if (!IsFrameworkThread || !Valid(expected) || desired is not { Count: 6 } || !TryRead(out var current)
            || !Same(expected, current) || !Enumerable.Range(0, 6).All(i => current.Ranges[i].Contains(desired[i]))) return false;
        if (current.Parameters.SequenceEqual(desired)) return true;
        var result = backend.Write(Copy(current), desired.ToArray());
        if (result.OwnedState is { } owned && Valid(owned) && owned.EditorIdentity == current.EditorIdentity)
        {
            cleanupBaseline = Copy(current);
            cleanupOwned = Copy(owned);
            cleanupChannels = Enumerable.Range(0, 6).Select(axis => owned.Parameters[axis] != current.Parameters[axis]).ToArray();
        }
        if (result.Applied && backend.TryRead(out var actual) && Valid(actual)
            && Same(current with { Parameters = desired.ToArray() }, actual))
        {
            ClearCleanup();
            return true;
        }
        // Do not expose a partially applied native sample as a future baseline. A failed cleanup
        // remains observable and blocks reads/new sessions; later Framework Poll retries it.
        _ = Cleanup();
        return false;
    }

    public void SetSamplingHistorySuppressed(bool suppressed)
    {
        RequireFramework();
        backend.SetHistorySuppressed(suppressed);
    }

    public Task<PortraitLightingFitCapture> CaptureAsync(PortraitLightingFitState expected)
    {
        RequireFramework();
        if (capture is not null || !TryRead(out var current) || !Same(expected, current))
            return Task.FromException<PortraitLightingFitCapture>(new InvalidOperationException("Lighting readback owner is unavailable or busy."));
        Task<IReadOnlyList<PortraitLightRgba>> task;
        try { task = backend.CaptureAsync(Copy(expected)); }
        catch (Exception exception) { return Task.FromException<PortraitLightingFitCapture>(exception); }
        var pending = new PendingCapture(Copy(expected), task);
        capture = pending;
        return pending.Publication.Task;
    }

    public void CancelCapturePublication()
    {
        RequireFramework();
        if (capture is { } pending) pending.Cancelled = true;
    }

    public void Poll()
    {
        RequireFramework();
        _ = Cleanup();
        if (capture is not { Task.IsCompleted: true } pending) return;
        capture = null;
        try
        {
            var pixels = pending.Task.GetAwaiter().GetResult();
            if (pending.Cancelled) { pending.Publication.TrySetCanceled(); return; }
            if (!TryRead(out var current) || !Same(current, pending.Expected) || pixels is null
                || pixels.Count != (long)current.Width * current.Height
                || pixels.Any(pixel => pixel.A is < 0 or > 255 || !pixel.Rgb.IsValid))
                throw new InvalidOperationException("Lighting sample changed before Framework publication.");
            pending.Publication.TrySetResult(new(Copy(current), Array.AsReadOnly(pixels.ToArray())));
        }
        catch (Exception exception)
        {
            if (pending.Cancelled) pending.Publication.TrySetCanceled();
            else pending.Publication.TrySetException(exception);
        }
    }

    public bool TryCommitHistory(PortraitLightingFitState expected, IReadOnlyList<int> baseline)
    {
        if (!IsFrameworkThread || baseline is not { Count: 6 } || !TryRead(out var current) || !Same(current, expected)
            || !Enumerable.Range(0, 6).All(i => current.Ranges[i].Contains(baseline[i]))) return false;
        return backend.CommitHistory(Copy(current), baseline.ToArray());
    }

    private bool Cleanup()
    {
        if (cleanupOwned is not { } owned || cleanupBaseline is not { } baseline) return true;
        if (cleanupChannels is not { Length: 6 } channels) return false;
        if (!IsFrameworkThread) return false;
        if (backend.IsEditorOwnerDestroyed(owned.EditorIdentity)) { ClearCleanup(); return true; }
        if (!backend.TryRead(out var current) || !Valid(current)) return false;
        if (current.EditorIdentity != owned.EditorIdentity) { ClearCleanup(); return true; }
        var desired = current.Parameters.ToArray();
        for (var i = 0; i < 6; i++)
        {
            if (channels[i] && current.Parameters[i] == owned.Parameters[i] && current.Ranges[i].Contains(baseline.Parameters[i]))
                desired[i] = baseline.Parameters[i];
            else channels[i] = false;
        }
        if (desired.SequenceEqual(current.Parameters)) { ClearCleanup(); return true; }
        var result = backend.Write(Copy(current), desired);
        // The native writer reports all six attempted values, including channels
        // it left unchanged. Never expand this lease to a player's intervening
        // edit when another compensation attempt is only partly successful.
        if (result.OwnedState is { } actualOwned && Valid(actualOwned) && actualOwned.EditorIdentity == owned.EditorIdentity)
            cleanupOwned = Copy(actualOwned);
        if (!result.Applied || !backend.TryRead(out var actual) || !Valid(actual)
            || !Same(current with { Parameters = desired }, actual)) return false;
        ClearCleanup();
        return true;
    }

    private void ClearCleanup() { cleanupBaseline = null; cleanupOwned = null; cleanupChannels = null; }
    private void RequireFramework()
    {
        if (!IsFrameworkThread) throw new InvalidOperationException("Lighting adapter requires Framework.");
    }
    private static bool Valid(PortraitLightingFitState state) => state is not null
        && !string.IsNullOrWhiteSpace(state.EditorIdentity) && !string.IsNullOrWhiteSpace(state.FixedStateKey)
        && state.Width > 0 && state.Height > 0 && (long)state.Width * state.Height <= 1_048_576
        && state.Parameters is { Count: 6 } && state.Ranges is { Count: 6 }
        && Enumerable.Range(0, 6).All(i => state.Ranges[i].Contains(state.Parameters[i]));
    private static PortraitLightingFitState Copy(PortraitLightingFitState state) => state with
    { Parameters = Array.AsReadOnly(state.Parameters.ToArray()), Ranges = Array.AsReadOnly(state.Ranges.ToArray()) };
    private static bool Same(PortraitLightingFitState first, PortraitLightingFitState second) => first.EditorIdentity == second.EditorIdentity
        && first.FixedStateKey == second.FixedStateKey && first.Width == second.Width && first.Height == second.Height
        && first.Parameters.SequenceEqual(second.Parameters) && first.Ranges.SequenceEqual(second.Ranges);
    private sealed class PendingCapture(PortraitLightingFitState expected, Task<IReadOnlyList<PortraitLightRgba>> task)
    {
        public PortraitLightingFitState Expected { get; } = expected;
        public Task<IReadOnlyList<PortraitLightRgba>> Task { get; } = task;
        public TaskCompletionSource<PortraitLightingFitCapture> Publication { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled;
    }
}
