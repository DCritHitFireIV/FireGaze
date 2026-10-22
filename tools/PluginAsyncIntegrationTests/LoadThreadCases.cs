using System.Collections.Concurrent;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FireGaze;

internal static class LoadThreadCases
{
    internal static async Task<(int Checks, int Failures)> Run()
    {
        var checks = 0;
        var failures = 0;
        void Check(bool value, string name)
        {
            checks++;
            if (!value) failures++;
            Console.WriteLine($"{(value ? "PASS" : "FAIL")} {name}");
        }

        var originalFramework = typeof(Plugin).GetProperty(nameof(Plugin.Framework))!.GetValue(null);
        var originalLog = typeof(Plugin).GetProperty(nameof(Plugin.Log))!.GetValue(null);
        try
        {
            foreach (var mode in new[] { "failure", "cancel", "close" })
            {
                using var scheduler = new FrameworkQueue();
                var framework = DispatchProxy.Create<IFramework, CallbackProxy>();
                ((CallbackProxy)(object)framework).Callback = scheduler.Invoke;
                var log = DispatchProxy.Create<IPluginLog, CallbackProxy>();
                SetService(nameof(Plugin.Framework), framework);
                SetService(nameof(Plugin.Log), log);

                var configThreads = new ConcurrentQueue<int>();
                var host = DispatchProxy.Create<IDalamudPluginInterface, CallbackProxy>();
                ((CallbackProxy)(object)host).Callback = (method, _) =>
                {
                    if (method.Name == "get_ConfigFile")
                    {
                        configThreads.Enqueue(Environment.CurrentManagedThreadId);
                        throw new InitializationProbeException();
                    }
                    return null;
                };
                var plugin = new Plugin(host);
                using var cancellation = new CancellationTokenSource();
                var load = Task.Run(() => plugin.LoadAsync(cancellation.Token));
                Check(scheduler.Submitted.Wait(TimeSpan.FromSeconds(5)), "actual LoadAsync reaches the Framework dispatcher");
                Check(configThreads.IsEmpty, "actual initialization does not touch host services before Framework dispatch");
                Check(!load.IsCompleted, "actual LoadAsync awaits queued Framework work");
                if (mode == "cancel") cancellation.Cancel();
                var close = mode == "close" ? plugin.DisposeAsync().AsTask() : Task.CompletedTask;
                scheduler.AllowExecution.Set();
                Exception? error = null;
                try { await load.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception e) { error = e; }
                if (mode != "failure")
                {
                    Check(configThreads.IsEmpty, $"{mode} before queued initialization prevents host service access");
                    Check(error is OperationCanceledException, $"queued loading {mode} preserves cancellation outcome");
                }
                else
                {
                    Check(configThreads.Count == 1 && configThreads.Single() == scheduler.ThreadId,
                        "actual InitializeCore begins on the Framework thread");
                    Check(error is InitializationProbeException, "initialization failure survives asynchronous cleanup");
                }
                await close.WaitAsync(TimeSpan.FromSeconds(5));
                await plugin.DisposeAsync();
                Check(scheduler.OffThreadCleanupCalls == 0, "failed or cancelled loading releases Framework resources on that thread");
            }
        }
        finally
        {
            SetService(nameof(Plugin.Framework), originalFramework);
            SetService(nameof(Plugin.Log), originalLog);
        }
        return (checks, failures);
    }

    private static void SetService(string name, object? value) => typeof(Plugin).GetProperty(name)!.SetValue(null, value);

    private sealed class InitializationProbeException : Exception;

    private sealed class FrameworkQueue : IDisposable
    {
        private readonly BlockingCollection<(Action Action, TaskCompletionSource Completion)> pending = new();
        private readonly Thread thread;
        internal readonly ManualResetEventSlim Submitted = new();
        internal readonly ManualResetEventSlim AllowExecution = new();
        internal int ThreadId;
        internal int OffThreadCleanupCalls;

        internal FrameworkQueue()
        {
            thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                foreach (var work in pending.GetConsumingEnumerable())
                {
                    AllowExecution.Wait();
                    try { work.Action(); work.Completion.TrySetResult(); }
                    catch (Exception e) { work.Completion.TrySetException(e); }
                }
            }) { IsBackground = true, Name = "Framework loading boundary fixture" };
            thread.Start();
        }

        internal object? Invoke(MethodInfo method, object?[]? args)
        {
            if (method.Name == "get_IsInFrameworkUpdateThread") return Environment.CurrentManagedThreadId == ThreadId;
            if (method.Name == "RunOnFrameworkThread" && args is [Action action])
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add((action, done));
                Submitted.Set();
                return done.Task;
            }
            if (method.Name == "remove_Update" && Environment.CurrentManagedThreadId != ThreadId)
                Interlocked.Increment(ref OffThreadCleanupCalls);
            if (method.ReturnType == typeof(void)) return null;
            throw new InvalidOperationException($"Unexpected Framework call: {method}");
        }

        public void Dispose()
        {
            AllowExecution.Set();
            pending.CompleteAdding();
            if (!thread.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("Framework fixture did not stop");
            pending.Dispose();
            Submitted.Dispose();
            AllowExecution.Dispose();
        }
    }
}

public class CallbackProxy : DispatchProxy
{
    internal Func<MethodInfo, object?[]?, object?>? Callback;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Callback?.Invoke(targetMethod!, args);
}
