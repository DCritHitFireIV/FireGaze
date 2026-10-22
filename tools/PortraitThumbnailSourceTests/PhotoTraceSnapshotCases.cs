using System.Reflection;
using System.Text.Json;
using FireGaze.PortraitPreview;

internal static class PhotoTraceSnapshotCases
{
    public static async Task Run(Action<bool, string> check)
    {
        // Missing in-memory state made a saturated Dalamud log conceal the hung
        // provider prefix. Exercise real trace calls with independently gated work.
        var property = typeof(PortraitPhotoTrace.Operation).GetProperty("Snapshot", BindingFlags.Public | BindingFlags.Instance);
        check(property is not null, "capture phase evidence remains readable without a working log sink");
        JsonElement Read(PortraitPhotoTrace.Operation operation)
            => JsonSerializer.SerializeToElement(property!.GetValue(operation));
        string Last(PortraitPhotoTrace.Operation operation)
            => Read(operation).EnumerateArray().Last().GetProperty("State").GetString()!;

        var loggerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var loggerRelease = new ManualResetEventSlim();
        var providerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var providerRelease = new ManualResetEventSlim();
        var providerCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new PortraitPhotoTrace(line =>
        {
            if (!line.StartsWith("begin ", StringComparison.Ordinal)) return;
            loggerEntered.TrySetResult();
            if (!loggerRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        }).Start(PortraitPhotoOperation.HistoryCapture);
        var work = Task.Run(() => operation.CallAsync(PortraitPhotoPhase.LayerReadback, () =>
        {
            providerEntered.TrySetResult();
            if (!providerRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return providerCompletion.Task;
        }, 0));
        try
        {
            await loggerEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(Last(operation) == "logging-begin", "a blocked logger is distinguished from an entered provider");
            loggerRelease.Set();
            await providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(Last(operation) == "invoking", "a synchronous provider prefix is observable before a Task exists");
            providerRelease.Set();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (Last(operation) != "awaiting" && DateTime.UtcNow < deadline) await Task.Delay(1);
            check(Last(operation) == "awaiting" && !work.IsCompleted,
                "a physically pending returned Task is distinguished from the provider prefix");
            providerCompletion.SetResult(7);
            check(await work == 7 && Last(operation) == "completed", "actual provider completion retires the diagnostic phase");
            var snapshot = Read(operation)[0];
            check(snapshot.GetProperty("Phase").GetString() == "LayerReadback"
                && snapshot.GetProperty("Layer").GetInt32() == 0
                && snapshot.GetProperty("ThreadID").GetInt32() > 0,
                "background evidence identifies the bounded phase and layer without reading editor memory");
        }
        finally
        {
            loggerRelease.Set(); providerRelease.Set(); providerCompletion.TrySetResult(7);
            await work.WaitAsync(TimeSpan.FromSeconds(3));
        }

        var silent = new PortraitPhotoTrace().Start(PortraitPhotoOperation.HistoryCapture);
        await silent.CallAsync(PortraitPhotoPhase.SourceCopy, () => Task.FromResult(1));
        check(Last(silent) == "completed", "in-memory evidence does not require logging to be enabled");
        var failing = new PortraitPhotoTrace(_ => throw new IOException("private logger path"))
            .Start(PortraitPhotoOperation.HistoryCapture);
        try { failing.Call<int>(PortraitPhotoPhase.SourceAcquire, () => throw new ArgumentException("private game data")); }
        catch (ArgumentException) { }
        check(Last(failing) == "failed" && Read(failing)[0].GetProperty("ErrorType").GetString() == "ArgumentException"
            && !Read(failing).ToString().Contains("private", StringComparison.Ordinal),
            "logger failure cannot conceal the real operation failure or expose its message");
        for (var i = 0; i < 100; i++) silent.Call(PortraitPhotoPhase.SourceAcquire, () => 1);
        check(Read(silent).GetArrayLength() <= 32 && Read(silent).EnumerateArray().All(row => row.GetProperty("State").GetString() == "completed"),
            "per-operation memory stays bounded after the logging admission budget is exhausted");
    }
}
