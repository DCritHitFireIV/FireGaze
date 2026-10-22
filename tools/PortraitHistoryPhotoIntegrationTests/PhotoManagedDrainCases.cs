using System.Reflection;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal static class PhotoManagedDrainCases
{
    internal static async Task<int> RunAsync(string root, ITextureProvider textures,
        ITextureReadbackProvider readback, int historyBytes)
    {
        var checks = 0;
        var failures = new List<string>();
        void Check(bool value, string name)
        {
            if (!value) failures.Add(name);
            checks++; Console.WriteLine((value ? "PASS " : "FAIL ") + name);
        }

        // These fields are the actual service's accepted managed I/O boundaries. The capture and
        // encoding fields below represent physical GPU work and must remain excluded from drain.
        var write = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var physicalCapture = new TaskCompletionSource<PortraitThumbnailLayers?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var physicalEncoding = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var photos = new PortraitHistoryPhotos(Path.Combine(root, "managed-drain-fault.bin"),
            textures, readback, new PortraitThumbnailSource(), historyBytes);
        Set(photos, "writing", write.Task);
        Set(photos, "maintenance", maintenance.Task);
        Set(photos, "capture", physicalCapture.Task);
        Set(photos, "encoding", physicalEncoding.Task);

        var close = Task.Factory.StartNew(photos.BeginDispose, CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        var drain = await close.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!drain.IsCompleted, "photo Framework close returns while managed I/O and GPU tasks remain pending");
        Check(!drain.IsCompleted && ReferenceEquals(drain, photos.BeginDispose()),
            "repeated photo close returns one drain task without admitting another lifetime");
        write.SetException(new IOException("simulated managed write failure"));
        await Task.Delay(50);
        var waitedForEveryManagedTask = !drain.IsCompleted;
        maintenance.SetResult();
        Exception? drainError = null;
        try { await drain; }
        catch (Exception failure) { drainError = failure; }
        Check(waitedForEveryManagedTask,
            "a failed write cannot skip another accepted managed maintenance task during close");
        var error = photos.Diagnostics().GetType().GetProperty("lastError")!.GetValue(photos.Diagnostics()) as string;
        Check(error == "simulated managed write failure", "managed close failure remains observable without another Framework poll");
        Check(!physicalCapture.Task.IsCompleted && !physicalEncoding.Task.IsCompleted,
            "managed photo drain never waits for physical GPU capture or encoding");
        physicalCapture.SetResult(null);
        physicalEncoding.SetResult([]);
        Check(drainError is IOException && drain.IsFaulted,
            "actual photo write fault survives close after all managed tasks settle");
        var reported = new List<Exception>();
        var hostPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostDrain = PortraitManagedDrain.Combine(reported.Add, drain, hostPending.Task);
        Check(!hostDrain.IsCompleted && reported.Count == 0,
            "photo failure cannot skip another accepted host persistence task");
        hostPending.SetResult();
        try { await hostDrain; }
        catch (IOException) { }
        Check(hostDrain.IsFaulted && reported.Count == 1 && ReferenceEquals(reported[0], drainError),
            "production host aggregator logs and preserves actual photo failure");

        var falsePhotos = new PortraitHistoryPhotos(Path.Combine(root, "managed-drain-false.bin"),
            textures, readback, new PortraitThumbnailSource(), historyBytes);
        var falseMaintenance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(falsePhotos, "writing", Task.FromResult(false));
        Set(falsePhotos, "maintenance", falseMaintenance.Task);
        var falseDrain = falsePhotos.BeginDispose();
        Check(!falseDrain.IsCompleted, "false photo write result still waits for accepted maintenance");
        falseMaintenance.SetResult();
        try { await falseDrain; }
        catch (IOException) { }
        Check(falseDrain.IsFaulted, "actual unsuccessful photo write result cannot become a successful final drain");

        // The official provider owns token callbacks. A synchronous callback may await Present;
        // Framework close must not execute that callback inline while holding the service lock.
        using var cancellationPhotos = new PortraitHistoryPhotos(Path.Combine(root, "managed-cancellation.bin"),
            textures, readback, new PortraitThumbnailSource(), historyBytes);
        var lifetime = (CancellationTokenSource)typeof(PortraitHistoryPhotos)
            .GetField("lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cancellationPhotos)!;
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        using var callback = lifetime.Token.Register(() =>
        {
            callbackEntered.Set();
            if (!releaseCallback.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("cancellation callback not released");
        });
        var cancellationClose = Task.Factory.StartNew(cancellationPhotos.BeginDispose, CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        var returnedWhileBlocked = false;
        var drainCompletedWhileBlocked = false;
        var physicalPendingWhileBlocked = false;
        try
        {
            Check(callbackEntered.Wait(TimeSpan.FromSeconds(5)), "actual photo lifetime invokes registered provider cancellation callback");
            returnedWhileBlocked = ReferenceEquals(await Task.WhenAny(cancellationClose, Task.Delay(250)), cancellationClose);
            if (returnedWhileBlocked)
            {
                drainCompletedWhileBlocked = cancellationClose.Result.IsCompletedSuccessfully;
                physicalPendingWhileBlocked = !PhotoPhysicalDrainCases.PhysicalDrain(cancellationPhotos).IsCompleted;
            }
        }
        finally { releaseCallback.Set(); }
        await (await cancellationClose.WaitAsync(TimeSpan.FromSeconds(5)));
        Check(returnedWhileBlocked, "photo Framework close returns while a provider cancellation callback is blocked");
        Check(drainCompletedWhileBlocked, "managed file drain excludes Present-dependent provider cancellation callbacks");
        Check(physicalPendingWhileBlocked, "host physical drain retains provider scope through blocked cancellation callbacks");
        await PhotoPhysicalDrainCases.PhysicalDrain(cancellationPhotos).WaitAsync(TimeSpan.FromSeconds(5));
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
        return checks;
    }

    private static void Set(PortraitHistoryPhotos photos, string name, object value)
        => typeof(PortraitHistoryPhotos).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(photos, value);
}
