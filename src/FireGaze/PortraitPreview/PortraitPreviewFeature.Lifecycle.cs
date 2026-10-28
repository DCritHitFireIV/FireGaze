using FireGaze.LocalPreview.Runtime;

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
        Exception? closeFailure = null;
        try { DisposeCore(); }
        catch (Exception failure) { closeFailure = failure; }
        try { DisposeCaptureProgress(); }
        catch (Exception failure)
        {
            closeFailure = closeFailure is null ? failure : new AggregateException(closeFailure, failure);
        }
        if (closeFailure is null) return;
        // Callback/window cleanup can fail after the native drain was already
        // aggregated. That earlier task cannot confirm this later release.
        lock (incompletePhysicalClose)
            physicalDrain = PortraitCaptureRetirement.CombineOwned(this,
                physicalDrain ?? Task.CompletedTask, incompletePhysicalClose.Task);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeFailure).Throw();
    }

    private void DisposeCore()
    {
        Volatile.Write(ref analysisSession, null);
        // Photos require a Framework close receipt before their accepted file
        // work can be declared settled. A failed viewport retirement can prevent
        // Release entirely; preserve that unacquired receipt as pending.
        Task photoDrain = photos is null ? Task.CompletedTask
            : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        Task nativePhysical = Task.CompletedTask;
        Task remainingRelease = Task.CompletedTask;
        var historyBatchAccepted = true;
        void Release()
        {
            List<Exception> failures = [];
            void Cleanup(Action release)
            {
                try { release(); }
                catch (Exception failure) { failures.Add(failure); }
            }
            // A failed independent callback removal must not skip the existing
            // restore-before-target-close order or leave its save hook in business mode.
            Cleanup(DisposeEditorOpening);
            Cleanup(DisposeSourceCensus);
            Cleanup(() => { if (target is not null && !CancelLightingFitForEdit()) _ = target.AbortCapturedEditor(); });
            Cleanup(DisposeNativeLive);
            Cleanup(DisposeLightingFit);
            Cleanup(() => photoDrain = photos?.BeginDispose() ?? Task.CompletedTask);
            Task photoReadbackDrain = Task.CompletedTask;
            Cleanup(() => photoReadbackDrain = photoReadbackRuntime?.Close() ?? Task.CompletedTask);
            Cleanup(() => liveImageService?.Dispose());
            Cleanup(() => nativePhysical = PortraitCaptureRetirement.CombineOwned(this,
                photos?.DrainPhysicalAsync() ?? Task.CompletedTask, photoReadbackDrain,
                liveImageService?.DrainPhysicalAsync() ?? Task.CompletedTask,
                lightingFitCapture?.DrainPhysicalAsync() ?? Task.CompletedTask,
                window?.DrainPhysicalAsync() ?? Task.CompletedTask, nativeLivePhysicalDrain, sourceCensusPhysicalDrain));
            Cleanup(() => liveCaptureAPI?.Dispose());
            Cleanup(() => { if (historyBatch is not null) historyBatchAccepted = FlushEditing(); });
            Cleanup(() =>
            {
                if (session?.Close() == false && !target.AbortCapturedEditor())
                    Plugin.Log.Error("[肖像同步预览] 外观还原及编辑器关闭均失败：{Error}", target.LastError ?? "未知原因");
            });
            Cleanup(() => { if (ownedSavePolicy is not null) QueueOwnedCacheIfNeeded(); });
            Cleanup(DisposeLibrary);
            Cleanup(() =>
            {
                target?.Dispose();
                if (target is INativePhysicalCloseState { PhysicalCloseComplete: false } native)
                    throw new NativePhysicalCloseException(target,
                        new InvalidOperationException(native.PhysicalCloseFailure ?? "Portrait native close was not confirmed."));
            });
            if (failures.Count != 0) throw new AggregateException("Portrait resource retirement is incomplete.", failures);
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
        List<Exception> closeFailures = [];
        void CompleteIndependentClose(Action release)
        {
            try { release(); }
            catch (Exception failure) { closeFailures.Add(failure); }
        }
        CompleteIndependentClose(() => Plugin.Framework.Update -= Update);
        CompleteIndependentClose(() => pluginInterface.UiBuilder.Draw -= Draw);
        CompleteIndependentClose(() =>
        {
            if (Plugin.Framework.IsInFrameworkUpdateThread) BeginRelease();
            else Plugin.Framework.RunOnFrameworkThread(BeginRelease).GetAwaiter().GetResult();
        });
        managedDrain = PortraitCaptureRetirement.CombineOwned(this, remainingRelease.ContinueWith(finished =>
        {
            return DrainManagedAsyncCore(photoDrain, finished, historyBatchAccepted);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap());
        physicalDrain = PortraitCaptureRetirement.CombineOwned(this, remainingRelease.ContinueWith(finished =>
            finished.IsCompletedSuccessfully ? nativePhysical : incompletePhysicalClose.Task,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap());
        CompleteIndependentClose(() => diagnostics?.UnregisterFunc());
        CompleteIndependentClose(windows.RemoveAllWindows);
        CompleteIndependentClose(() => window?.Dispose());
        if (closeFailures.Count != 0) throw new AggregateException("Portrait feature close is incomplete.", closeFailures);
    }

    private Task DrainManagedAsyncCore(Task photoDrain, Task remainingRelease, bool historyBatchAccepted)
    {
        var drains = new List<Task>(7) { photoDrain, Task.FromResult(historyBatchAccepted) };
        void BeginDrain(Func<Task> begin)
        {
            try { drains.Add(begin()); }
            catch (Exception failure) { drains.Add(Task.FromException(failure)); }
        }
        // A failed native retirement still owns accepted managed writes. Start
        // every writer before propagating either release or persistence failure.
        BeginDrain(() => cacheWriter.BeginDispose());
        BeginDrain(() => history?.BeginDispose() ?? Task.CompletedTask);
        BeginDrain(() => glamourPlateCache?.BeginDispose() ?? Task.CompletedTask);
        BeginDrain(DrainFavoritePersistence);
        BeginDrain(DrainStorageObservation);
        return PortraitManagedDrain.Combine(
            error => Plugin.Log.Warning(error, "[肖像同步预览] 托管文件排空失败"), drains.ToArray()).ContinueWith(finished =>
        {
            Exception? managedFailure = null;
            try { finished.GetAwaiter().GetResult(); }
            catch (Exception failure) { managedFailure = failure; }
            Exception? releaseFailure = null;
            try { remainingRelease.GetAwaiter().GetResult(); }
            catch (Exception failure) { releaseFailure = failure; }
            if (releaseFailure is not null && managedFailure is not null)
                throw new AggregateException(releaseFailure, managedFailure);
            if ((releaseFailure ?? managedFailure) is { } closeFailure)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closeFailure).Throw();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
