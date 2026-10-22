#if !LIFETIME
Console.Error.WriteLine("FAIL PluginWorkLifetime is missing; no async lifecycle coordinator exists");
return 1;
#else
using FireGaze.Lifecycle;

var checks = 0;
var failures = 0;
void Check(bool value, string name)
{
    Console.WriteLine((value ? "PASS " : "FAIL ") + name);
    checks++; if (!value) failures++;
}

await PluginWorkLifetimeTests.RunAsync(Check);
Console.WriteLine($"PluginAsyncLifecycleTests: {checks} checks, {failures} failures");
return failures == 0 ? 0 : 1;
#endif

#if LIFETIME
static class PluginWorkLifetimeTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        ConstructionIsPure(check);
        await RunningAdmissionAndGeneration(check);
        await CloseAdmissionRaceAndDrain(check);
        await DuplicateCloseAndErrors(check);
        await TaskRegistrationAndCancellation(check);
    }

    private static void ConstructionIsPure(Action<bool, string> check)
    {
        var life = new PluginWorkLifetime();
        check(life.State == PluginWorkLifetimeState.Loading && life.Generation == 1,
            "construction starts Loading at generation one");
        check(!life.TryAcquire(out _, out _), "Loading rejects ordinary work admission");
        check(life.TryAcquire(out var loadingLease, true, out _), "Loading allows only explicit bootstrap admission");
        loadingLease!.Dispose();
        check(life.State == PluginWorkLifetimeState.Loading, "releasing bootstrap work does not start or close the coordinator");
    }

    private static async Task RunningAdmissionAndGeneration(Action<bool, string> check)
    {
        var life = new PluginWorkLifetime();
        check(life.TryStartRunning(out _), "Loading transitions to Running exactly once");
        var generation = life.Generation;
        check(!life.TryStartRunning(out _), "Running cannot be started twice");
        check(life.TryAcquire(out var lease, out _), "Running admits a lease");
        var ran = false;
        check(lease!.TryExecute(() => ran = true), "a current lease executes work outside the coordinator lock");
        check(ran, "admitted work runs exactly once");
        var queuedRan = false;
        check(life.Queue(_ => { queuedRan = true; return ValueTask.CompletedTask; }, out var queuedTask),
            "Running admits a queued operation");
        await queuedTask!;
        check(queuedRan, "queued operation performs its generation check at execution and then runs while current");
        lease.Dispose();
        check(life.Generation == generation, "releasing a current lease does not change its generation");
        await life.CloseAsync();
    }

    private static async Task CloseAdmissionRaceAndDrain(Action<bool, string> check)
    {
        var life = new PluginWorkLifetime(); life.TryStartRunning(out _);
        check(life.TryAcquire(out var lease, out _), "close race obtains a pre-close lease");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var ran = Task.Run(() => lease!.TryExecute(() => { entered.Set(); release.Wait(); }));
        check(entered.Wait(TimeSpan.FromSeconds(2)), "accepted operation enters before close");
        var beginCalled = 0; var cleanupCalled = 0; var callbackInLock = false;
        var close = life.CloseAsync(() => { callbackInLock = Monitor.IsEntered(life); Interlocked.Increment(ref beginCalled); return ValueTask.CompletedTask; },
            () => { callbackInLock |= Monitor.IsEntered(life); Interlocked.Increment(ref cleanupCalled); return ValueTask.CompletedTask; });
        check(life.State == PluginWorkLifetimeState.Closing && life.Generation != lease!.Generation,
            "close atomically enters Closing and advances the operation generation");
        check(!life.TryAcquire(out _, out _), "new ordinary work is rejected after close admission closes");
        var queuedRan = false; using var queuedGate = new ManualResetEventSlim(); var admittedLease = lease!;
        var queuedTask = Task.Run(() => { queuedGate.Wait(); return admittedLease.TryExecute(() => queuedRan = true); });
        check(!queuedTask.IsCompleted, "a queued operation can wait before its execution generation check");
        release.Set(); await ran;
        await close; queuedGate.Set();
        var queuedResult = await queuedTask;
        check(!queuedResult && !queuedRan && life.State == PluginWorkLifetimeState.Closed, "a queued old-generation operation checks generation at execution and does not run after close");
        check(beginCalled == 1 && cleanupCalled == 1 && !callbackInLock, "begin/cleanup callbacks run once outside the coordinator lock");
        lease!.Dispose();
    }

    private static async Task DuplicateCloseAndErrors(Action<bool, string> check)
    {
        var life = new PluginWorkLifetime(); life.TryStartRunning(out _);
        var beginCalls = 0; var cleanupCalls = 0;
        var first = life.CloseAsync(() => { Interlocked.Increment(ref beginCalls); throw new InvalidOperationException("begin"); },
            () => { Interlocked.Increment(ref cleanupCalls); return ValueTask.CompletedTask; });
        var second = life.CloseAsync(() => ValueTask.CompletedTask, () => ValueTask.CompletedTask);
        check(ReferenceEquals(first, second), "repeated CloseAsync returns the same close task");
        var result = await first;
        check(!result.Completed && result.BeginCloseError is InvalidOperationException && life.State == PluginWorkLifetimeState.Closing,
            "begin-close failure is reported while drain still refuses false Closed");
        check(beginCalls == 1 && cleanupCalls == 1, "begin/cleanup do not repeat after a failed close task");

        var cleanupLife = new PluginWorkLifetime(); cleanupLife.TryStartRunning(out _);
        var cleanupResult = await cleanupLife.CloseAsync(() => ValueTask.CompletedTask,
            () => throw new InvalidOperationException("cleanup"));
        check(!cleanupResult.Completed && cleanupResult.CleanupError is InvalidOperationException
            && cleanupLife.State == PluginWorkLifetimeState.Closing,
            "cleanup failure is observable and cannot claim Closed");

        var cancellationLife = new PluginWorkLifetime(); cancellationLife.TryStartRunning(out _);
        using var registration = cancellationLife.ShutdownToken.Register(() => throw new InvalidOperationException("cancel callback"));
        var cancellationResult = await cancellationLife.CloseAsync();
        check(!cancellationResult.Completed && cancellationResult.BeginCloseError is AggregateException
            && cancellationLife.State == PluginWorkLifetimeState.Closing,
            "a throwing shutdown callback is reported without escaping CloseAsync or claiming Closed");
    }

    private static async Task TaskRegistrationAndCancellation(Action<bool, string> check)
    {
        var life = new PluginWorkLifetime(); life.TryStartRunning(out _);
        check(life.TryAcquire(out var lease, out _), "task registration obtains a lease");
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        check(lease!.TryTrack(completion.Task), "a cancellable or asynchronous task can be registered");
        var close = life.CloseAsync();
        check(life.ShutdownToken.IsCancellationRequested && !close.IsCompleted,
            "closing cancels the stage signal but waits for accepted task registration");
        check(!lease.TryExecute(() => throw new Exception("must not run")), "stale lease cannot execute new work during drain");
        completion.SetResult(null); var result = await close;
        check(result.Completed && life.State == PluginWorkLifetimeState.Closed, "drain completes after registered task finishes");
        check(!lease.TryTrack(Task.CompletedTask), "released/stale lease cannot register a late task");
        lease.Dispose();
    }
}
#endif
