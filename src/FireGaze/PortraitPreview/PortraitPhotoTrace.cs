using System.Diagnostics;

namespace FireGaze.PortraitPreview;

internal enum PortraitPhotoOperation { HistoryCapture, LoadedImage }
internal enum PortraitPhotoPhase
{
    Capture, SourceAcquire, SourceCopy, RetainLayers, LayerReadback, CpuComposite,
    CpuEncode, Upload, VisibleReadback, PngEncode, CacheWrite, LoadedImageRead, LoadedImageUpload,
}

internal sealed record PortraitPhotoStageSnapshot(long OperationID, string Kind, long StageID,
    string Phase, int Layer, string State, int ThreadID, string? TaskStatus, string? ErrorType,
    string? ErrorFrames);

/// <summary>Bounded operation diagnostics; never samples editor pointers or image/user data.</summary>
internal sealed class PortraitPhotoTrace(Action<string>? logger = null)
{
    public const int MaximumLinesPerOperation = 64;
    private static long serial;
    private PortraitPhotoStageSnapshot? lastFailure;

    // The current operation is replaced by automatic retries. Keep its preceding
    // failure independently, without a photo-service lock or external log sink.
    public PortraitPhotoStageSnapshot? LastFailure => Volatile.Read(ref lastFailure);

    public Operation Start(PortraitPhotoOperation kind)
        => new(logger, Interlocked.Increment(ref serial), kind,
            onFailure: failure => Volatile.Write(ref lastFailure, failure));

    internal sealed class Operation(Action<string>? logger, long id, PortraitPhotoOperation kind, bool tracking = true,
        Action<PortraitPhotoStageSnapshot>? onFailure = null)
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
            var evidence = Update(stage, "logging-terminal", errorType: error?.GetType().Name,
                errorFrames: ReadErrorFrames(error));
            if (error is not null) onFailure?.Invoke(evidence with { State = "failed" });
            Log(state, stage, suffix);
            Update(stage, error is null ? "completed" : "failed", errorType: error?.GetType().Name);
        }

        private PortraitPhotoStageSnapshot Update(Stage stage, string state, string? taskStatus = null,
            string? errorType = null, string? errorFrames = null)
        {
            lock (snapshotSync)
            {
                snapshots.TryGetValue(stage.ID, out var previous);
                return snapshots[stage.ID] = new(id, kind.ToString(), stage.ID, stage.Phase.ToString(), stage.Layer,
                    state, Environment.CurrentManagedThreadId, taskStatus ?? previous?.TaskStatus, errorType,
                    errorFrames ?? previous?.ErrorFrames);
            }
        }

        private static string? ReadErrorFrames(Exception? error)
        {
            if (error is null) return null;
            try
            {
                // Method metadata only: no exception message, source file, image,
                // player data or local directory is included in this evidence.
                var frames = new StackTrace(error.GetBaseException(), false).GetFrames() ?? [];
                return string.Join("\n", frames.Take(8).Select(frame =>
                {
                    var method = frame.GetMethod();
                    var name = method is null ? "unavailable" : method.DeclaringType?.FullName + "." + method.Name;
                    return name.Length <= 255 ? name : name[..255];
                }));
            }
            catch { return null; }
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
