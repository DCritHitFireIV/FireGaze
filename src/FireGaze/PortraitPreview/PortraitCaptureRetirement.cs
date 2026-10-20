using System.Runtime.CompilerServices;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Admits ownership before calling an external provider, including its synchronous prefix.
/// Sealing never waits on Framework; retirement follows actual work and late resource release.
/// A provider that never finishes deliberately keeps the owning plugin scope alive.
/// </summary>
internal sealed class PortraitCaptureRetirement
{
    private readonly object sync = new();
    private readonly StrongBox<object?> retirementRoot = new();
    private readonly TaskCompletionSource retired;
    private int pending;
    private bool sealedAdmission;
    // A failed Dispose does not prove native/provider ownership was released. Keep both the
    // wrapper and failure strongly owned, without retrying a potentially non-idempotent release.
    private readonly Dictionary<IDisposable, Exception> quarantine = new(ReferenceEqualityComparer.Instance);
    // Remember reference identity without retaining successfully released wrappers. A callback
    // can reenter cleanup before Dispose returns, or arrive again after retirement completed.
    private readonly ConditionalWeakTable<IDisposable, object> releaseAttempts = new();
    public PortraitCaptureRetirement()
        => retired = new(retirementRoot, TaskCreationOptions.RunContinuationsAsynchronously);
    public int Pending { get { lock (sync) return pending; } }
    public int CleanupFailures { get { lock (sync) return quarantine.Count; } }
    public string? LastCleanupFailureType { get { lock (sync) return quarantine.Values.LastOrDefault()?.GetType().FullName; } }

    public void Release(IDisposable? resource)
    {
        if (resource is null) return;
        IDisposable admission;
        lock (sync)
        {
            if (quarantine.ContainsKey(resource) || releaseAttempts.TryGetValue(resource, out _)) return;
            // Detached work already owns a lease when admission is sealed. Its late cleanup
            // belongs to that same retiring scope and must be admitted before Dispose reenters.
            if (retired.Task.IsCompleted) throw new InvalidOperationException("Resource arrived after physical retirement.");
            releaseAttempts.Add(resource, new object());
            pending++;
            admission = new Lease(this);
        }
        try { resource.Dispose(); }
        catch (Exception error) { Quarantine(resource, error); }
        finally { admission.Dispose(); }
    }

    public void ObserveFailure(Exception? error)
    {
        if (error is null) return;
        if (error is PortraitCaptureCleanupException cleanup)
            foreach (var resource in cleanup.Resources) Quarantine(resource, cleanup);
        if (error is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) ObserveFailure(inner);
        else if (error.InnerException is { } inner) ObserveFailure(inner);
    }

    private void Quarantine(IDisposable resource, Exception error)
    {
        lock (sync)
        {
            if (quarantine.TryAdd(resource, error)) pending++;
        }
    }

    public IDisposable Enter()
    {
        lock (sync)
        {
            if (sealedAdmission) throw new ObjectDisposedException(nameof(PortraitCaptureRetirement));
            pending++;
            return new Lease(this);
        }
    }

    /// <summary>Admits fresh provider work only while the scope has no uncertain resources.</summary>
    public bool TryEnter(out IDisposable lifetime)
    {
        lock (sync)
        {
            lifetime = null!;
            if (sealedAdmission || quarantine.Count != 0) return false;
            pending++;
            lifetime = new Lease(this);
            return true;
        }
    }

    public Task Seal()
    {
        lock (sync)
        {
            sealedAdmission = true;
            retirementRoot.Value = this;
            if (pending == 0)
            {
                retirementRoot.Value = null;
                retired.TrySetResult();
            }
            return retired.Task;
        }
    }

    /// <summary>
    /// Task.WhenAll does not itself retain every unfinished input task. The returned task must
    /// own both the scope and a private copy of its child tasks until all outcomes are known.
    /// Use only for physical close, after new admission has already been revoked.
    /// </summary>
    public static Task CombineOwned(object owner, params Task[] drains)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(drains);
        var children = drains.ToArray();
        var all = Task.WhenAll(children);
        var root = new StrongBox<object?>(new object[] { owner, children });
        var completion = new TaskCompletionSource(root, TaskCreationOptions.RunContinuationsAsynchronously);
        _ = all.ContinueWith(static (finished, state) =>
        {
            var signal = (TaskCompletionSource)state!;
            var held = (StrongBox<object?>)signal.Task.AsyncState!;
            held.Value = null;
            if (finished.IsFaulted) signal.TrySetException(finished.Exception!.InnerExceptions);
            else if (finished.IsCanceled)
            {
                try { finished.GetAwaiter().GetResult(); }
                catch (OperationCanceledException cancellation) { signal.TrySetCanceled(cancellation.CancellationToken); }
            }
            else signal.TrySetResult();
        }, completion, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return completion.Task;
    }

    private void Leave()
    {
        lock (sync)
        {
            if (--pending == 0 && sealedAdmission)
            {
                // Pending Task.AsyncState roots the owner; a completed task keeps only this
                // empty BCL box, preserving synchronous IsCompleted without retaining a plugin.
                retirementRoot.Value = null;
                retired.TrySetResult();
            }
        }
    }

    private sealed class Lease(PortraitCaptureRetirement owner) : IDisposable
    {
        private PortraitCaptureRetirement? current = owner;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Leave();
    }
}

/// <summary>A lower capture layer carries failed resources to its owning physical lifetime.</summary>
internal sealed class PortraitCaptureCleanupException(IReadOnlyList<IDisposable> resources,
    IReadOnlyList<Exception> failures) : AggregateException("Capture resource release was not confirmed.", failures)
{
    public IReadOnlyList<IDisposable> Resources { get; } = resources;

    public static void DisposeAll(params IDisposable?[] resources)
    {
        List<IDisposable> failedResources = [];
        List<Exception> failures = [];
        foreach (var resource in resources.Where(value => value is not null).Distinct(ReferenceEqualityComparer.Instance))
        {
            try { ((IDisposable)resource!).Dispose(); }
            catch (Exception error) { failedResources.Add((IDisposable)resource!); failures.Add(error); }
        }
        if (failures.Count != 0) throw new PortraitCaptureCleanupException(failedResources, failures);
    }
}
