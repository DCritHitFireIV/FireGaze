using System.Diagnostics;

namespace FireGaze.PortraitPreview;

internal enum PortraitPhotoOperation { HistoryCapture, LoadedImage }
internal enum PortraitPhotoPhase
{
    Capture, SourceAcquire, SourceCopy, RetainLayers, LayerReadback, CpuComposite,
    CpuEncode, Upload, VisibleReadback, PngEncode, CacheWrite, LoadedImageRead, LoadedImageUpload,
}

internal sealed record PortraitPhotoStageSnapshot(long OperationID, string Kind, long StageID,
    string Phase, int Layer, string State, int ThreadID, string? TaskStatus, string? ErrorType);

/// <summary>Bounded operation diagnostics; never samples editor pointers or image/user data.</summary>
internal sealed class PortraitPhotoTrace(Action<string>? logger = null)
{
    public const int MaximumLinesPerOperation = 64;
    private static long serial;

    public Operation Start(PortraitPhotoOperation kind)
        => new(logger, Interlocked.Increment(ref serial), kind);

    internal sealed class Operation(Action<string>? logger, long id, PortraitPhotoOperation kind, bool tracking = true)
    {
        internal static readonly Operation Silent = new(null, 0, default, false);
        private int reserved;
        private long stages;
        private readonly object snapshotSync = new();
        private readonly Dictionary<long, PortraitPhotoStageSnapshot> snapshots = [];

        // This only copies bounded managed values. It remains readable through the
        // diagnostic server even if the game thread or the external log sink stalls.
        public PortraitPhotoStageSnapshot[] Snapshot
        {
            get { lock (snapshotSync) return snapshots.Values.OrderBy(value => value.StageID).ToArray(); }
        }

        public T Call<T>(PortraitPhotoPhase phase, Func<T> call, int layer = -1)
        {
            if (!tracking) return call();
            var scope = Begin(phase, layer, 2);
            try { var result = call(); Finish(scope, "end"); return result; }
            catch (Exception error) { Finish(scope, "fail", error); throw; }
        }

        public async Task<T> CallAsync<T>(PortraitPhotoPhase phase, Func<Task<T>> call, int layer = -1)
        {
            if (!tracking) return await call().ConfigureAwait(false);
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
            if (!tracking) { await call().ConfigureAwait(false); return; }
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
            Update(stage, "logging-begin");
            Log("begin", stage);
            Update(stage, "invoking");
            return stage;
        }

        private void Returned(Stage? stage, TaskStatus status)
        {
            if (stage is null) return;
            Update(stage, "logging-task-returned", status.ToString());
            Log("task-returned", stage, " status=" + status);
            Update(stage, "awaiting", status.ToString());
        }

        private void Finish(Stage? stage, string state, Exception? error = null)
        {
            if (stage is null) return;
            var suffix = " elapsedMs=" + (long)Stopwatch.GetElapsedTime(stage.Start).TotalMilliseconds;
            if (error is not null) suffix += " error=" + error.GetType().Name;
            Update(stage, "logging-terminal", errorType: error?.GetType().Name);
            Log(state, stage, suffix);
            Update(stage, error is null ? "completed" : "failed", errorType: error?.GetType().Name);
        }

        private void Update(Stage stage, string state, string? taskStatus = null, string? errorType = null)
        {
            lock (snapshotSync)
            {
                snapshots.TryGetValue(stage.ID, out var previous);
                snapshots[stage.ID] = new(id, kind.ToString(), stage.ID, stage.Phase.ToString(), stage.Layer,
                    state, Environment.CurrentManagedThreadId, taskStatus ?? previous?.TaskStatus, errorType);
            }
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
