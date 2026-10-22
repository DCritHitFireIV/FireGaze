using FireGaze.PortraitPreview;
using System.Runtime.CompilerServices;

var checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
async Task Wait(Func<bool> predicate)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!predicate()) await Task.Delay(1, timeout.Token);
}

// A pending public texture operation owns the scope before its synchronous prefix can reenter.
var pending = new TaskCompletionSource<Image>(TaskCreationOptions.RunContinuationsAsynchronously);
var closedInPrefix = new PortraitOwnedSceneTextureLifetime<Image>();
Task? draining = null;
Check(closedInPrefix.TryCopy(closedInPrefix.Generation, () => { draining = closedInPrefix.DrainPhysicalAsync(); return pending.Task; }), "admit before provider prefix");
Check(draining is { IsCompleted: false } && closedInPrefix.InFlight == 1, "prefix close cannot retire pending provider");
Check(ReferenceEquals(draining, closedInPrefix.DrainPhysicalAsync()), "all callers receive the same physical drain task");
Check(!closedInPrefix.TryCopy(closedInPrefix.Generation, () => throw new Exception("must never call")), "closing refuses another provider");
var late = new Image(); pending.SetResult(late);
await draining!.WaitAsync(TimeSpan.FromSeconds(5));
Check(late.Disposals == 1 && closedInPrefix.InFlight == 0 && closedInPrefix.FrameCount == 0, "late result released before physical drain");

// Draw retains the exact older image until its own lease ends, even after replacement and close.
var frames = new PortraitOwnedSceneTextureLifetime<Image>();
var first = new Image(); var second = new Image();
Check(frames.TryCopy(frames.Generation, () => Task.FromResult(first)), "first copied frame");
using var oldDraw = frames.TryBorrow();
Check(oldDraw?.Texture == first, "draw borrows actual copied image");
Check(frames.TryCopy(frames.Generation, () => Task.FromResult(second)), "replacement copied frame");
using var newDraw = frames.TryBorrow();
Check(newDraw?.Texture == second && first.Disposals == 0, "replacement preserves outstanding older draw");
Check(frames.FrameCount == 2 && !frames.TryCopy(frames.Generation, () => throw new Exception("capacity must reject")), "two images are a hard physical bound");
var frameDrain = frames.DrainPhysicalAsync();
Check(!frameDrain.IsCompleted && frames.TryBorrow() is null, "close revokes new draws and waits existing borrowers");
newDraw!.Dispose(); newDraw.Dispose();
Check(second.Disposals == 1 && first.Disposals == 0 && !frameDrain.IsCompleted, "new draw returns once without releasing older image");
oldDraw!.Dispose();
await frameDrain.WaitAsync(TimeSpan.FromSeconds(5));
Check(first.Disposals == 1 && frames.FrameCount == 0 && frames.Borrowers == 0, "last real borrower permits retirement");

// A plain provider exception is recoverable; uncertain release is permanently quarantined.
var errors = new PortraitOwnedSceneTextureLifetime<Image>();
Check(errors.TryCopy(errors.Generation, () => throw new InvalidOperationException("provider prefix")), "provider failure is observed");
await Wait(() => errors.InFlight == 0);
Check(errors.LastCopyFailureType == typeof(InvalidOperationException).FullName, "provider failure remains diagnostic evidence");
var good = new Image();
Check(errors.TryCopy(errors.Generation, () => Task.FromResult(good)), "failed provider does not consume permanent capacity");
await errors.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(5));
Check(good.Disposals == 1, "recovered image closes normally");
var failedRelease = new PortraitOwnedSceneTextureLifetime<Image>();
var uncertain = new Image { ThrowOnDispose = true };
Check(failedRelease.TryCopy(failedRelease.Generation, () => Task.FromResult(uncertain)), "uncertain resource begins as a valid result");
var failedDrain = failedRelease.DrainPhysicalAsync();
Check(!failedDrain.IsCompleted && failedRelease.CleanupFailures == 1 && uncertain.Disposals == 1, "throwing release cannot claim physical retirement");
failedRelease.Dispose();
Check(uncertain.Disposals == 1 && !failedRelease.TryCopy(failedRelease.Generation, () => throw new Exception("quarantined")), "failed release is never retried or reopened");

// Disposal callbacks cannot exceed the frame bound or observe a completed retirement early.
var reentrant = new PortraitOwnedSceneTextureLifetime<Image>();
var reentered = false;
var disposable = new Image { DuringDispose = () => { reentered = true; Check(!reentrant.DrainPhysicalAsync().IsCompleted, "dispose prefix remains physically owned"); } };
Check(reentrant.TryCopy(reentrant.Generation, () => Task.FromResult(disposable)), "reentrant cleanup image");
await reentrant.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(5));
Check(reentered && disposable.Disposals == 1, "reentrant cleanup completes exactly once");

// Failed input cleanup carried by the provider keeps the source lifetime alive too.
var sourceFailure = new PortraitOwnedSceneTextureLifetime<Image>();
var source = new Image { ThrowOnDispose = true };
Check(sourceFailure.TryCopy(sourceFailure.Generation, () => Task.FromException<Image>(new PortraitCaptureCleanupException([source], [new Exception("input release")] ))), "provider cleanup failure observed");
await Wait(() => sourceFailure.InFlight == 0);
Check(sourceFailure.CleanupFailures == 1 && !sourceFailure.DrainPhysicalAsync().IsCompleted, "failed source ownership blocks drain");

// Reusing a wrapper is not another resource reference, including after it was disposed.
var reuse = new PortraitOwnedSceneTextureLifetime<Image>();
var reused = new Image();
Check(reuse.TryCopy(reuse.Generation, () => Task.FromResult(reused)), "initial unique wrapper");
Check(reuse.TryCopy(reuse.Generation, () => Task.FromResult(new Image())) && reused.Disposals == 1, "old wrapper physically released");
Check(reuse.TryCopy(reuse.Generation, () => Task.FromResult(reused)) && reuse.CleanupFailures == 1, "disposed wrapper cannot be republished as a fresh frame");
Check(!reuse.DrainPhysicalAsync().IsCompleted && reused.Disposals == 1, "duplicate ownership quarantined without a second dispose");

// The second physical slot is counted before a delayed result exists.
var bounded = new PortraitOwnedSceneTextureLifetime<Image>();
Check(bounded.TryCopy(bounded.Generation, () => Task.FromResult(new Image())), "bound initial resident");
var delayed = new TaskCompletionSource<Image>(TaskCreationOptions.RunContinuationsAsynchronously);
Check(bounded.TryCopy(bounded.Generation, () => delayed.Task) && bounded.InFlight + bounded.FrameCount == 2, "resident plus pending owns both slots");
for (var index = 0; index < 16; index++)
    Check(!bounded.TryCopy(bounded.Generation, () => throw new Exception("backlog")), "pending provider cannot grow a backlog");
var boundedDrain = bounded.DrainPhysicalAsync();
var delayedResult = new Image(); delayed.SetResult(delayedResult);
await boundedDrain.WaitAsync(TimeSpan.FromSeconds(5));
Check(delayedResult.Disposals == 1, "bounded late copy physically released");

// A result submitted before source takeover can never become the new owner's image.
var changed = new PortraitOwnedSceneTextureLifetime<Image>();
var original = new Image();
Check(changed.TryCopy(changed.Generation, () => Task.FromResult(original)), "source's completed private copy");
var beforeChange = changed.Generation;
var oldCopy = new TaskCompletionSource<Image>(TaskCreationOptions.RunContinuationsAsynchronously);
Check(changed.TryCopy(beforeChange, () => oldCopy.Task), "source copy submitted before takeover");
Check(changed.InvalidateSource() > beforeChange && changed.TryBorrow() is null, "generation change revokes fresh-frame access");
using (var staticDraw = changed.TryBorrow(allowPreviousGeneration: true))
    Check(staticDraw?.Texture == original && staticDraw.Generation == beforeChange, "static last frame is explicitly labelled with its old generation");
Check(!changed.TryCopy(beforeChange, () => throw new Exception("stale capability")), "stale generation cannot submit another provider");
var obsolete = new Image(); oldCopy.SetResult(obsolete);
await Wait(() => changed.InFlight == 0);
Check(obsolete.Disposals == 1 && changed.TryBorrow() is null, "late old-source result cannot publish");
var replacement = new Image();
Check(changed.TryCopy(changed.Generation, () => Task.FromResult(replacement)), "new source can submit using its fresh generation");
using (var freshDraw = changed.TryBorrow()) Check(freshDraw?.Texture == replacement, "new source publishes its own completed copy");
await changed.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(5));
Check(original.Disposals == 1 && replacement.Disposals == 1, "both generations physically released once");

// Retaining only the physical drain task must also retain uncertain native ownership.
var retained = RetentionProbe.Begin();
for (var index = 0; index < 3; index++)
{
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
}
Check(!retained.Drain.IsCompleted && retained.Owner.IsAlive && retained.Texture.IsAlive,
    "drain task strongly owns failed cleanup until physical release is proven");
GC.KeepAlive(retained.Drain);

Console.WriteLine($"PASS: {checks} owned texture lifetime checks; pending provider, late result, draw leases, bound and cleanup quarantine; no game calls.");

sealed class Image : IDisposable
{
    public int Disposals;
    public bool ThrowOnDispose;
    public Action? DuringDispose;
    public void Dispose()
    {
        Disposals++;
        DuringDispose?.Invoke();
        if (ThrowOnDispose) throw new InvalidOperationException("unconfirmed image release");
    }
}

static class RetentionProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static (Task Drain, WeakReference Owner, WeakReference Texture) Begin()
    {
        var owner = new PortraitOwnedSceneTextureLifetime<Image>();
        var image = new Image { ThrowOnDispose = true };
        owner.TryCopy(owner.Generation, () => Task.FromResult(image));
        return (owner.DrainPhysicalAsync(), new WeakReference(owner), new WeakReference(image));
    }
}
