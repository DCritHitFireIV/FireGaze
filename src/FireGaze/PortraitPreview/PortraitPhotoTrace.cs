using System.Diagnostics;

namespace FireGaze.PortraitPreview;

internal enum PortraitPhotoOperation { HistoryCapture, LoadedImage }
internal enum PortraitPhotoPhase
{
    Capture, SourceAcquire, SourceCopy, RetainLayers, LayerReadback, CpuComposite,
    CpuEncode, Upload, VisibleReadback, PngEncode, CacheWrite, LoadedImageRead, LoadedImageUpload,
}

/// <summary>Bounded operation diagnostics; never samples editor pointers or image/user data.</summary>
internal sealed class PortraitPhotoTrace(Action<string>? logger = null)
{
    public const int MaximumLinesPerOperation = 64;
    private static long serial;

    public Operation Start(PortraitPhotoOperation kind)
        => logger is null ? Operation.Silent : new(logger, Interlocked.Increment(ref serial), kind);

    internal sealed class Operation(Action<string>? logger, long id, PortraitPhotoOperation kind)
    {
        internal static readonly Operation Silent = new(null, 0, default);
        private int reserved;
        private long stages;

        public T Call<T>(PortraitPhotoPhase phase, Func<T> call, int layer = -1)
        {
            if (logger is null) return call();
            var scope = Begin(phase, layer, 2);
            try { var result = call(); Finish(scope, "end"); return result; }
            catch (Exception error) { Finish(scope, "fail", error); throw; }
        }

        public async Task<T> CallAsync<T>(PortraitPhotoPhase phase, Func<Task<T>> call, int layer = -1)
        {
            if (logger is null) return await call().ConfigureAwait(false);
            var scope = Begin(phase, layer, 3);
            try
            {
                // The public API may block synchronously before returning its Task.
                var task = call();
                Returned(scope, task.Status);
                var result = await task.ConfigureAwait(false);
                Finish(scope, "end");
                return result;
            }
            catch (Exception error) { Finish(scope, "fail", error); throw; }
        }

        public async Task CallAsync(PortraitPhotoPhase phase, Func<Task> call, int layer = -1)
        {
            if (logger is null) { await call().ConfigureAwait(false); return; }
            var scope = Begin(phase, layer, 3);
            try
            {
                var task = call();
                Returned(scope, task.Status);
                await task.ConfigureAwait(false);
                Finish(scope, "end");
            }
            catch (Exception error) { Finish(scope, "fail", error); throw; }
        }

        private Stage? Begin(PortraitPhotoPhase phase, int layer, int lines)
        {
            // Reserve a terminal line before emitting begin; even concurrent callbacks
            // cannot exhaust the budget and strand a logged stage without its end/fail.
            while (true)
            {
                var before = Volatile.Read(ref reserved);
                if (before > MaximumLinesPerOperation - lines) return null;
                if (Interlocked.CompareExchange(ref reserved, before + lines, before) == before) break;
            }
            var stage = new Stage(Interlocked.Increment(ref stages), phase, layer, Stopwatch.GetTimestamp());
            Log("begin", stage);
            return stage;
        }

        private void Returned(Stage? stage, TaskStatus status)
        {
            if (stage is not null) Log("task-returned", stage, " status=" + status);
        }

        private void Finish(Stage? stage, string state, Exception? error = null)
        {
            if (stage is null) return;
            var suffix = " elapsedMs=" + (long)Stopwatch.GetElapsedTime(stage.Start).TotalMilliseconds;
            if (error is not null) suffix += " error=" + error.GetType().Name;
            Log(state, stage, suffix);
        }

        private void Log(string state, Stage stage, string suffix = "")
        {
            try
            {
                logger?.Invoke($"{state} {stage.Phase} op={id} kind={kind} stage={stage.ID}"
                    + (stage.Layer >= 0 ? " layer=" + stage.Layer : "")
                    + $" thread={Environment.CurrentManagedThreadId} role={(Thread.CurrentThread.IsThreadPoolThread ? "pool" : "caller")}" + suffix);
            }
            catch { /* Diagnostic storage must not change capture or resource lifetimes. */ }
        }

        private sealed record Stage(long ID, PortraitPhotoPhase Phase, int Layer, long Start);
    }
}
