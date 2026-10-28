using System.Runtime.CompilerServices;
using FireGaze.LocalPreview.Runtime;
using FireGaze.PortraitPreview;

namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    private readonly object physicalCloseSync = new();
    private readonly TaskCompletionSource physicalCloseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConditionalWeakTable<IDisposable, object> physicalReleaseAttempts = new();
    private readonly Dictionary<IDisposable, Exception> failedPhysicalOwners = new(ReferenceEqualityComparer.Instance);
    private Task? physicalDrain;
    private volatile bool physicalCloseUnknown;
    private int physicalReleasesInFlight;
    private bool physicalCloseSealed;

    // The host owns this task before clearing its logical Feature reference. Unknown hook
    // retirement deliberately stays pending and keeps both Feature and failed owners alive.
    public Task DrainPhysicalAsync()
    {
        lock (physicalCloseSync)
            return physicalDrain ??= PortraitCaptureRetirement.CombineOwned(this, physicalCloseCompletion.Task);
    }

    public void Dispose()
        => OnFramework(() => { DisposeCore(); return true; });

    private void DisposeCore()
    {
        if (disposed) return;
        disposed = true;
        var cleanupFinished = false;
        try
        {
            System.Threading.Volatile.Write(ref uiActiveEffects, []);
            CleanupPhysical(() => Framework.Update -= Update, "帧回调");
            DisposePresentationExtras();
            if (diagnosticsRegistered) CleanupPhysical(diagnosticsIpc.UnregisterFunc, "诊断 IPC");
            DisposeComponent(interaction, "菜单入口");
            DisposeComponent(companionNames, "宠物名称显示");
            DisposeComponent(companions, "宠物显示");
            DisposeComponent(companionEmotes, "宠物动作适配");
            if (runtime is not null)
            {
                CleanupPhysical(() => runtime.EmoteObserved -= RecordEmote, "动作观察回调");
                CleanupPhysical(() => runtime.NativeInputAccepted -= OnNativeAccepted, "原生输入回调");
                CleanupPhysical(() => runtime.PreviewCancelled -= OnCancelled, "试用退出回调");
                DisposeComponent(runtime, "角色显示");
            }
            DisposeComponent(hmsAdapter, "HMS 适配");
            cleanupFinished = true;
        }
        finally
        {
            lock (physicalCloseSync)
            {
                if (!cleanupFinished) physicalCloseUnknown = true;
                physicalCloseSealed = cleanupFinished;
                TryCompletePhysicalClose();
            }
            try
            {
                Log.Information(!physicalCloseCompletion.Task.IsCompletedSuccessfully
                    ? "[LocalPreview] 本地试用已停止，原生资源释放尚未确认"
                    : "[LocalPreview] FireGaze 本地试用功能已卸载");
            }
            catch { /* Logging is not physical retirement evidence. */ }
        }
    }

    private void DisposeComponent(IDisposable? component, string name)
    {
        if (component is null) return;
        lock (physicalCloseSync)
        {
            if (physicalReleaseAttempts.TryGetValue(component, out _)) return;
            // Publish the attempt before backend cleanup can reenter Feature close.
            physicalReleaseAttempts.Add(component, new object());
            physicalReleasesInFlight++;
        }
        try
        {
            component.Dispose();
            if (component is INativePhysicalCloseState { PhysicalCloseComplete: false } native)
                throw new InvalidOperationException(native.PhysicalCloseFailure ?? "Native hook retirement was not confirmed.");
        }
        catch (Exception error)
        {
            ObservePhysicalCloseFailure(error);
            lock (physicalCloseSync)
            {
                physicalCloseUnknown = true;
                failedPhysicalOwners.TryAdd(component, error);
            }
            ReportCleanupFailure(error, name);
        }
        finally
        {
            lock (physicalCloseSync)
            {
                physicalReleasesInFlight--;
                TryCompletePhysicalClose();
            }
        }
    }

    // A failed constructor has not assigned its field yet. Its typed rollback
    // exception transfers the native owner without attempting backend cleanup again.
    private bool ObservePhysicalCloseFailure(Exception error)
    {
        var unknown = false;
        var pending = new Stack<Exception>();
        var observed = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        while (pending.TryPop(out var current))
        {
            if (!observed.Add(current)) continue;
            if (current is NativePhysicalCloseException native)
            {
                lock (physicalCloseSync)
                {
                    physicalCloseUnknown = true;
                    if (!physicalReleaseAttempts.TryGetValue(native.Owner, out _))
                        physicalReleaseAttempts.Add(native.Owner, new object());
                    failedPhysicalOwners.TryAdd(native.Owner, error);
                }
                unknown = true;
            }
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            else if (current.InnerException is { } inner)
                pending.Push(inner);
        }
        return unknown;
    }

    // Dynamic hook setup can fail after the Feature has received its owner. Read
    // only managed close state here; no native probe or cleanup occurs at admission.
    private bool NativeAdmissionAvailable()
    {
        if (disposed || physicalCloseUnknown) return false;
        foreach (var owner in new IDisposable?[] { runtime, interaction, companionEmotes, gposeEmotes, specialHotbarView })
        {
            if (owner is not INativePhysicalCloseState native) continue;
            Exception failure;
            try
            {
                if (native.PhysicalCloseFailure is not { } reason) continue;
                failure = new InvalidOperationException(reason);
            }
            catch (Exception error) { failure = error; }
            lock (physicalCloseSync)
            {
                physicalCloseUnknown = true;
                failedPhysicalOwners.TryAdd(owner, failure);
            }
            // This is an observation, not an owner Dispose attempt. Full Feature
            // cleanup still closes independent resources inside that owner once.
            ReportCleanupFailure(failure, "原生资源状态");
        }
        return !physicalCloseUnknown;
    }

    private void CleanupPhysical(Action cleanup, string name)
    {
        lock (physicalCloseSync) physicalReleasesInFlight++;
        try { cleanup(); }
        catch (Exception error)
        {
            physicalCloseUnknown = true;
            ReportCleanupFailure(error, name);
        }
        finally
        {
            lock (physicalCloseSync)
            {
                physicalReleasesInFlight--;
                TryCompletePhysicalClose();
            }
        }
    }

    // Called under physicalCloseSync. A reentrant DisposeCore may finish while an
    // earlier disable/rollback cleanup still owns its native call and final outcome.
    private void TryCompletePhysicalClose()
    {
        if (physicalCloseSealed && physicalReleasesInFlight == 0 && !physicalCloseUnknown)
            physicalCloseCompletion.TrySetResult();
    }

    private void Cleanup(Action cleanup, string name)
    {
        try { cleanup(); }
        catch (Exception error) { ReportCleanupFailure(error, name); }
    }

    private void ReportCleanupFailure(Exception error, string name)
    {
        try { Log.Error(error, "[LocalPreview] {Component} 清理失败", name); }
        catch { /* Continue ownership cleanup even when the host log is unavailable. */ }
    }
}
