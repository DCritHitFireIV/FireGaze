namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    /// <summary>Managed cache/history writes accepted during close, drained outside Framework.</summary>
    public Task DrainManagedAsync() => managedDrain ?? Task.CompletedTask;
    /// <summary>Actual provider work and late texture release, awaited only after Framework close.</summary>
    public Task DrainPhysicalAsync()
    {
        if (physicalDrain is { } completedClose) return completedClose;
        // A close exception before aggregation leaves physical ownership unknown. The
        // host keeps only this task, so that task must also retain the unsealed Feature.
        lock (incompletePhysicalClose)
            return physicalDrain ?? (incompletePhysicalDrain ??=
                PortraitCaptureRetirement.CombineOwned(this, incompletePhysicalClose.Task));
    }
    public void Dispose()
    {
        if (disposed) return;
        try { DisposeCore(); }
        finally { DisposeCaptureProgress(); }
    }

    private void DisposeCore()
    {
        Volatile.Write(ref analysisSession, null);
        Task photoDrain = Task.CompletedTask;
        Task nativePhysical = Task.CompletedTask;
        Task remainingRelease = Task.CompletedTask;
        void Release()
        {
            DisposeEditorOpening();
            DisposeSourceCensus();
            if (!CancelLightingFitForEdit()) _ = target.AbortCapturedEditor();
            DisposeNativeLive();
            DisposeLightingFit();
            photoDrain = photos.BeginDispose();
            var photoReadbackDrain = photoReadbackRuntime?.Close() ?? Task.CompletedTask;
            liveImageService?.Dispose();
            nativePhysical = PortraitCaptureRetirement.CombineOwned(this, photos.DrainPhysicalAsync(), photoReadbackDrain,
                liveImageService?.DrainPhysicalAsync() ?? Task.CompletedTask,
                lightingFitCapture?.DrainPhysicalAsync() ?? Task.CompletedTask,
                window.DrainPhysicalAsync(), nativeLivePhysicalDrain, sourceCensusPhysicalDrain);
            liveCaptureAPI?.Dispose();
            FlushEditing();
            if (!session.Close() && !target.AbortCapturedEditor())
                Plugin.Log.Error("[肖像同步预览] 外观还原及编辑器关闭均失败：{Error}", target.LastError ?? "未知原因");
            QueueOwnedCacheIfNeeded();
            DisposeLibrary();
            target.Dispose();
        }
        void BeginRelease()
        {
            StopOwnedViewport("Feature disposal is waiting for the independent scene to retire.");
            if (ownedViewportDrain.IsCompletedSuccessfully) { Release(); return; }
            remainingRelease = ownedViewportDrain.ContinueWith(finished =>
            {
                finished.GetAwaiter().GetResult();
                return Plugin.Framework.RunOnFrameworkThread(Release);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        }
        disposed = true;
        Plugin.Framework.Update -= Update;
        pluginInterface.UiBuilder.Draw -= Draw;
        if (Plugin.Framework.IsInFrameworkUpdateThread) BeginRelease();
        else Plugin.Framework.RunOnFrameworkThread(BeginRelease).GetAwaiter().GetResult();
        managedDrain = PortraitCaptureRetirement.CombineOwned(this, remainingRelease.ContinueWith(finished =>
        {
            finished.GetAwaiter().GetResult();
            return DrainManagedAsyncCore(photoDrain);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap());
        physicalDrain = PortraitCaptureRetirement.CombineOwned(this, remainingRelease.ContinueWith(finished =>
            finished.IsCompletedSuccessfully ? nativePhysical : incompletePhysicalClose.Task,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap());
        diagnostics.UnregisterFunc();
        windows.RemoveAllWindows();
        window.Dispose();
    }

    private Task DrainManagedAsyncCore(Task photoDrain)
    {
        var drains = new List<Task>(3)
        {
            photoDrain,
            cacheWriter.BeginDispose(),
            history.BeginDispose(),
        };
        if (glamourPlateCache is not null) drains.Add(glamourPlateCache.BeginDispose());
        drains.Add(DrainFavoritePersistence());
        drains.Add(DrainStorageObservation());
        return PortraitManagedDrain.Combine(
            error => Plugin.Log.Warning(error, "[肖像同步预览] 托管文件排空失败"), drains.ToArray());
    }

    private void StopOwnedViewport(string reason)
    {
        if (ownedViewport is null || ownedViewportClosing) return;
        ownedViewportClosing = true;
        ownedViewportProof?.Dispose();
        ownedViewport.Stop(reason); ownedViewport.Dispose();
        ownedViewportDrain = PortraitCaptureRetirement.CombineOwned(ownedViewport,
            ownedViewport.DrainPhysicalAsync(), ownedViewportCom?.DrainPhysicalAsync() ?? Task.CompletedTask,
            ownedViewportProof?.DrainPhysicalAsync() ?? Task.CompletedTask);
    }
}
