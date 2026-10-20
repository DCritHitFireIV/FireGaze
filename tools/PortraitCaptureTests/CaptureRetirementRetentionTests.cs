using System.Runtime.CompilerServices;
using FireGaze.PortraitPreview;

static class CaptureRetirementRetentionTests
{
    public static int Run()
    {
        foreach (var retained in new[] { Begin(), BeginCombined() })
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            }
            if (retained.Drain.IsCompleted || !retained.Owner.IsAlive || !retained.Resource.IsAlive)
                throw new InvalidOperationException("physical drain task lost the actual cleanup owner/quarantined resource while still pending");
            GC.KeepAlive(retained.Drain);
        }
        var successful = new PortraitCaptureRetirement();
        var first = successful.Seal();
        if (!first.IsCompletedSuccessfully || !ReferenceEquals(first, successful.Seal()))
            throw new InvalidOperationException("physical seal must remain idempotent and complete immediately when truly idle");
        var pending = new TaskCompletionSource();
        var error = new IOException("file outcome must not bypass physical work");
        var combined = PortraitCaptureRetirement.CombineOwned(new object(), Task.FromException(error), pending.Task);
        if (combined.IsCompleted) throw new InvalidOperationException("a file failure bypassed another physical operation");
        pending.SetResult();
        if (!combined.IsFaulted || combined.Exception!.InnerExceptions.Single() != error)
            throw new InvalidOperationException("combined retirement lost the actual physical outcome");
        var completed = CompletedOwner();
        for (var attempt = 0; attempt < 3; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        if (!completed.Drain.IsCompletedSuccessfully || completed.Owner.IsAlive)
            throw new InvalidOperationException("a completed retirement task still roots its old scope");
        GC.KeepAlive(completed.Drain);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var canceled = PortraitCaptureRetirement.CombineOwned(new object(), Task.FromCanceled(cancel.Token));
        try { canceled.GetAwaiter().GetResult(); throw new InvalidOperationException("canceled physical drain became successful"); }
        catch (OperationCanceledException cancellation) when (cancellation.CancellationToken == cancel.Token) { }
        return 7;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task Drain, WeakReference Owner, WeakReference Resource) Begin()
    {
        var owner = new PortraitCaptureRetirement();
        var resource = new Uncertain();
        owner.Release(resource);
        return (owner.Seal(), new WeakReference(owner), new WeakReference(resource));
    }

    private sealed class Uncertain : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("release remains unconfirmed");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task Drain, WeakReference Owner, WeakReference Resource) BeginCombined()
    {
        var owner = new PortraitCaptureRetirement();
        var resource = new Uncertain();
        owner.Release(resource);
        return (PortraitCaptureRetirement.CombineOwned(owner, owner.Seal(), Task.CompletedTask),
            new WeakReference(owner), new WeakReference(resource));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task Drain, WeakReference Owner) CompletedOwner()
    {
        var owner = new object();
        return (PortraitCaptureRetirement.CombineOwned(owner, Task.CompletedTask), new WeakReference(owner));
    }
}
