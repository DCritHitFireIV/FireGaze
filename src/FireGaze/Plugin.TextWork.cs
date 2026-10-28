using FireGaze.Lifecycle;

namespace FireGaze;

public sealed partial class Plugin
{
    internal PluginWorkLifetime WorkLifetime => this.workLifetime;

    internal Task RunTextTask(Action work) => this.RunTextTask(() =>
    {
        work();
        return Task.CompletedTask;
    });

    internal Task<T> RunTextTask<T>(Func<T> work) => this.RunTextTask(() => Task.FromResult(work()));

    internal Task RunTextTask(Func<Task> work) => this.RunTextTask(async () =>
    {
        await work().ConfigureAwait(false);
        return true;
    });

    internal Task<T> RunTextTask<T>(Func<Task<T>> work)
        => this.RunTextTask(_ => work());

    internal Task<T> RunTextTask<T>(Func<PluginWorkLease, Task<T>> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!this.workLifetime.Queue(async lease =>
            {
                try { completion.TrySetResult(await work(lease).ConfigureAwait(false)); }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (Exception error) { completion.TrySetException(error); }
            }, out var scheduled))
        {
            completion.TrySetCanceled();
        }
        else
        {
            // A queued lease can become stale before its worker starts. In that
            // case no delegate runs, but callers still need a terminal task.
            _ = scheduled!.ContinueWith(_ => completion.TrySetCanceled(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return completion.Task;
    }
}
