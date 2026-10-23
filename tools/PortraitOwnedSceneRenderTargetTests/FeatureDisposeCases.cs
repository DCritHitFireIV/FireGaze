using FireGaze.PortraitPreview;
using Dalamud.Interface.Textures.TextureWraps;

// Scratch feasibility harness only. Lifecycle methods are never implemented here.
internal static class FeatureDisposeCases
{
    internal static Task Run() => SingleThreadScratch.Run(RunCases);
    private static async Task RunCases()
    {
        FireGaze.Plugin.Framework.Attach(SynchronizationContext.Current!, Environment.CurrentManagedThreadId);
        var cases = new (string, Func<Task>)[] {
            ("ready scene waits for future qualified return after event removal", FutureReturn),
            ("pending owned copy retains source after native retirement", PendingCopy),
            ("draw borrower retains source after native retirement", DrawBorrower),
            ("unused request cancels without fabricated native return", Unused),
            ("off-framework entry dispatches before stop and again before release", Dispatch),
            ("double dispose cannot repeat close or source invalidation", DoubleDispose),
            ("real managed drain rejects false persistence result", ManagedFailure),
            ("release exception keeps physical ownership incomplete", ReleaseFailure),
            ("synchronous release exception retains fallback physical root", SynchronousReleaseFailure),
            ("no owned scene closes without waiting for native return", NoScene),
            ("provider cleanup uncertainty blocks feature physical completion", CleanupFailure),
            ("pending photo readback independently blocks feature physical completion", PendingPhotoReadback),
            ("foreign callback thread is rejected rather than treated as retirement", ForeignCallback),
            ("StopOwnedViewport waits for pending first-frame proof and COM cleanup", AuxiliaryDrains),
        };
        var failures = new List<Exception>();
        foreach (var (name, run) in cases)
        {
            try { await run(); Console.WriteLine("PASS extracted product lifecycle: " + name); }
            catch (Exception error) { failures.Add(error); Console.Error.WriteLine("FAIL extracted product lifecycle " + name + ": " + error); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
        Console.WriteLine($"PASS {cases.Length} extracted product lifecycle cases; Framework/native/file boundaries remain test doubles");
    }
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    internal static async Task Wait(Func<bool> condition)
    {
        var time = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && time.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(2);
        Check(condition(), "bounded asynchronous completion");
    }
    private static async Task Finish(PortraitPreviewFeature feature)
    {
        await feature.PhysicalDrain.WaitAsync(TimeSpan.FromSeconds(2));
        await feature.ManagedDrain.WaitAsync(TimeSpan.FromSeconds(2));
    }
    private static async Task FutureReturn()
    {
        var feature = new PortraitPreviewFeature(); feature.Start(); feature.Source(1); feature.Source(2);
        feature.CompleteCopy(); await feature.CopySettled();
        FireGaze.Plugin.Framework.Raise(); feature.RaiseDraw();
        Check(feature.Updates == 1 && feature.Draws == 1, "fixture events were not attached");
        feature.Dispose();
        FireGaze.Plugin.Framework.Raise(); feature.RaiseDraw();
        Check(feature.Updates == 1 && feature.Draws == 1, "canonical Dispose left events subscribed");
        Check(feature.SourceEpoch == 7 && feature.SourceAlive && !feature.NativeReleased && !feature.ManagedSealed,
            "source/writer retired before owned native return");
        Check(!feature.PhysicalDrain.IsCompleted && !feature.ManagedDrain.IsCompleted, "feature drain completed without native return");
        feature.Source(3); // No feature Update call after Dispose.
        await Finish(feature);
        Check(feature.SourceEpoch == 8 && !feature.SourceAlive && feature.NativeReleased && feature.ManagedSealed,
            "future native return did not finish canonical close");
        Check(feature.Trace.IndexOf("native-retired") < feature.Trace.IndexOf("dispose-census")
            && feature.Trace.IndexOf("close-source") < feature.Trace.IndexOf("dispose-target")
            && feature.Trace.IndexOf("dispose-target") < feature.Trace.IndexOf("seal-cache"), "canonical release ordering regressed");
    }
    private static async Task PendingCopy()
    {
        var feature = new PortraitPreviewFeature(); feature.Start(); feature.Source(1); feature.Source(2);
        Check(feature.Copies == 1, "fixture did not enter real product adapter");
        feature.Dispose(); feature.Source(3);
        await Wait(() => feature.NativeReleased);
        Check(feature.SourceEpoch == 7 && feature.SourceAlive && !feature.ManagedSealed && !feature.PhysicalDrain.IsCompleted,
            "native retirement forgot pending owned copy");
        var late = feature.CompleteCopy(); await Finish(feature);
        Check(late.Disposed && feature.SourceEpoch == 8, "late provider result published or source never closed");
    }
    private static async Task DrawBorrower()
    {
        var feature = new PortraitPreviewFeature(); feature.Start(); feature.Source(1); feature.Source(2);
        var image = feature.CompleteCopy(); await feature.CopySettled();
        var draw = feature.BorrowImage(); Check(draw is not null, "fixture never published a real product frame");
        feature.Dispose(); feature.Source(3); await Wait(() => feature.NativeReleased);
        Check(feature.SourceAlive && feature.SourceEpoch == 7 && !image.Disposed && !feature.PhysicalDrain.IsCompleted,
            "canonical feature close forgot held owned draw");
        draw!.Dispose(); await Finish(feature);
        Check(image.Disposed && !feature.SourceAlive, "last draw release did not finish canonical feature close");
    }
    private static async Task Unused()
    {
        var feature = new PortraitPreviewFeature(); feature.Start(); feature.Dispose(); await Finish(feature);
        Check(!feature.NativeReleased && !feature.SourceAlive && feature.SourceEpoch == 8 && feature.Copies == 0,
            "unused request required or fabricated native work");
    }
    private static async Task Dispatch()
    {
        var feature = new PortraitPreviewFeature(); feature.Start(); feature.Source(1); feature.Source(2);
        feature.CompleteCopy(); await feature.CopySettled();
        var before = FireGaze.Plugin.Framework.DispatchCalls;
        await Task.Run(feature.Dispose);
        feature.Source(3); await Finish(feature);
        Check(FireGaze.Plugin.Framework.DispatchCalls >= before + 2, "canonical off-thread paths did not call Framework dispatcher");
    }
    private static async Task DoubleDispose()
    {
        var feature = new PortraitPreviewFeature(); feature.Start(); feature.Source(1); feature.Source(2);
        feature.CompleteCopy(); await feature.CopySettled();
        feature.Dispose(); var physical = feature.PhysicalDrain; var managed = feature.ManagedDrain;
        feature.Dispose(); Check(feature.CaptureProgressDisposals == 1, "repeated Dispose repeated capture cleanup");
        feature.Source(3); await Finish(feature); feature.Dispose();
        Check(feature.SourceEpoch == 8 && feature.TargetDisposals == 1 && feature.CaptureProgressDisposals == 1
            && ReferenceEquals(physical, feature.PhysicalDrain) && ReferenceEquals(managed, feature.ManagedDrain), "repeated Dispose repeated physical close");
    }
    private static async Task ManagedFailure()
    {
        var feature = new PortraitPreviewFeature(); feature.NoScene(); feature.FailManaged(); feature.Dispose();
        await feature.PhysicalDrain.WaitAsync(TimeSpan.FromSeconds(2));
        try { await feature.ManagedDrain.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (IOException) { Check(!feature.SourceAlive && feature.ManagedSealed, "managed failure bypassed close"); return; }
        throw new Exception("real PortraitManagedDrain treated false writer result as durable success");
    }
    private static async Task ReleaseFailure()
    {
        var feature = new PortraitPreviewFeature(); feature.Start(); feature.Source(1); feature.Source(2);
        feature.CompleteCopy(); await feature.CopySettled(); feature.FailTarget(); feature.Dispose(); feature.Source(3);
        try { await feature.ManagedDrain.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (IOException) {
            Check(!feature.PhysicalDrain.IsCompleted && feature.CaptureProgressDisposals == 1,
                "release failure reported physical completion or skipped outer finally"); return;
        }
        throw new Exception("release exception was hidden from managed close");
    }
    private static async Task NoScene()
    {
        var feature = new PortraitPreviewFeature(); feature.NoScene(); feature.Dispose(); await Finish(feature);
        Check(feature.SourceEpoch == 8 && !feature.SourceAlive && !feature.NativeReleased, "no-scene close still waited for native source");
    }
    private static async Task PendingPhotoReadback()
    {
        var feature = new PortraitPreviewFeature(); feature.NoScene();
        var completion = feature.PendingPhotoReadback();
        feature.Dispose();
        await feature.ManagedDrain.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!feature.PhysicalDrain.IsCompleted && feature.PhotoReadbackCloseCalls == 1,
            "canonical feature close forgot the independent readback drain");
        feature.Dispose();
        Check(feature.PhotoReadbackCloseCalls == 1, "double dispose repeated readback close");
        completion.SetResult();
        await Finish(feature);
    }
    private static Task SynchronousReleaseFailure()
    {
        var feature = new PortraitPreviewFeature(); feature.NoScene(); feature.FailTarget();
        try { feature.Dispose(); }
        catch (IOException) {
            Check(!feature.PhysicalDrain.IsCompleted && feature.CaptureProgressDisposals == 1,
                "synchronous close exception lost fallback ownership or outer finally");
            feature.Dispose();
            Check(feature.CaptureProgressDisposals == 1 && feature.TargetDisposals == 1,
                "second close repeated uncertain native release");
            return Task.CompletedTask;
        }
        throw new Exception("synchronous target close failure did not escape Dispose");
    }
    private static async Task CleanupFailure()
    {
        var feature = new PortraitPreviewFeature(); feature.FailInputCleanup(); feature.Start(); feature.Source(1); feature.Source(2);
        feature.Dispose(); feature.Source(3); var image = feature.CompleteCopy(); await feature.CopySettled();
        Check(feature.NativeReleased && image.Disposed && feature.SourceAlive && feature.SourceEpoch == 7
            && !feature.ManagedDrain.IsCompleted && !feature.PhysicalDrain.IsCompleted, "uncertain provider release certified feature close");
    }
    private static async Task ForeignCallback()
    {
        var feature = new PortraitPreviewFeature(); feature.Start(); feature.Source(1); feature.Source(2);
        feature.CompleteCopy(); await feature.CopySettled(); feature.Dispose();
        await Task.Run(() => feature.ForeignSource(3));
        Check(!feature.NativeReleased && feature.SourceAlive && feature.SourceEpoch == 7 && !feature.PhysicalDrain.IsCompleted,
            "foreign managed callback was mistaken for the exact bound native thread");
    }
    private static async Task AuxiliaryDrains()
    {
        var feature = new PortraitPreviewFeature();
        var auxiliary = feature.EnablePendingAuxiliaryDrains();
        feature.Start(); feature.Source(1); feature.Source(2);
        Check(feature.Copies == 1, "auxiliary fixture did not enter the real owned adapter");
        var image = feature.CompleteCopy();
        await Wait(() => feature.ProofPending);
        await Wait(() => auxiliary.ComEntered.IsSet);
        feature.Dispose(); feature.Source(3);
        Check(feature.NativeReleased && feature.SourceAlive && feature.SourceEpoch == 7
            && !feature.PhysicalDrain.IsCompleted && !feature.ManagedDrain.IsCompleted,
            "StopOwnedViewport aggregation certified pending auxiliary drains");
        auxiliary.Release.Set();
        auxiliary.Raw.TrySetResult(new PortraitOwnedSceneRawFrame(new byte[16], 2, 2, 8, 28));
        await Finish(feature);
        await auxiliary.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(image.Disposed && !feature.SourceAlive && feature.SourceEpoch == 8,
            "pending first-frame proof or COM cleanup did not finish the canonical close");
    }
}

internal static class SingleThreadScratch
{
    internal static Task Run(Func<Task> body)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            using var queue = new System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)>();
            SynchronizationContext.SetSynchronizationContext(new LoopContext(queue));
            try
            {
                var running = body();
                _ = running.ContinueWith(_ => queue.CompleteAdding(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                foreach (var item in queue.GetConsumingEnumerable()) item.Callback(item.State);
                running.GetAwaiter().GetResult();
                finished.TrySetResult();
            }
            catch (Exception error) { finished.TrySetException(error); }
        }) { IsBackground = true, Name = "FireGaze lifecycle scratch Framework" }.Start();
        return finished.Task;
    }
    private sealed class LoopContext(System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> queue)
        : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
    }
}

namespace FireGaze
{
    internal static class Plugin
    {
        internal static readonly DisposalFramework Framework = new();
        internal static readonly DisposalLog Log = new();
    }
    internal sealed class DisposalFramework
    {
        private SynchronizationContext? context;
        private int ownerThread;
        internal int DispatchCalls;
        internal void Attach(SynchronizationContext value, int thread) { context = value; ownerThread = thread; }
        internal bool IsInFrameworkUpdateThread => Environment.CurrentManagedThreadId == ownerThread;
        internal event Action? Update;
        internal void Raise() => Update?.Invoke();
        internal Task RunOnFrameworkThread(Action action)
        {
            DispatchCalls++;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Execute()
            {
                try { action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
            }
            if (IsInFrameworkUpdateThread) Execute(); else context!.Post(_ => Execute(), null);
            return completion.Task;
        }
    }
    internal sealed class DisposalLog
    {
        internal void Error(string text, object? value) { }
        internal void Warning(Exception error, string text) { }
    }
}

namespace FireGaze.PortraitPreview
{
    internal sealed unsafe partial class PortraitPreviewFeature
    {
        private readonly OwnedExperimentBrokerCases.Fixture fixture;
        private bool disposed;
        private Task ownedViewportDrain = Task.CompletedTask;
        private Task? managedDrain, physicalDrain, incompletePhysicalDrain;
        private readonly TaskCompletionSource incompletePhysicalClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private object? analysisSession = new();
        private readonly DisposalObject photos, window, diagnostics, windows, cacheWriter, history;
        private readonly DisposalPhotoReadback photoReadbackRuntime = new();
        private DisposalObject? glamourPlateCache = null, liveImageService = null, lightingFitCapture = null, liveCaptureAPI = null;
        private readonly DisposalInterface pluginInterface = new();
        private readonly DisposalTarget target;
        private readonly DisposalSession session;
        private Task nativeLivePhysicalDrain = Task.CompletedTask, sourceCensusPhysicalDrain = Task.CompletedTask;
        private readonly PortraitOwnedSceneRenderTargetAdapter adapter;
        private readonly TextureApi textures = new();
        internal long SourceEpoch = 7;
        private readonly int nativeThread = Environment.CurrentManagedThreadId;
        internal bool SourceAlive = true;
        internal int Updates, Draws, CaptureProgressDisposals;
        internal bool ManagedSealed => cacheWriter.Sealed && history.Sealed;
        internal bool ProofPending => ownedViewportProof?.Pending == true;
        internal bool NativeReleased => fixture.Runtime.Retires == 1;
        internal readonly List<string> Trace = [];
        internal Task PhysicalDrain => DrainPhysicalAsync();
        internal Task ManagedDrain => DrainManagedAsync();
        internal int TargetDisposals => target.Disposals;
        internal int Copies => textures.Copies;
        internal int PhotoReadbackCloseCalls => photoReadbackRuntime.CloseCalls;
        internal TaskCompletionSource PendingPhotoReadback()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            photoReadbackRuntime.Physical = completion.Task;
            return completion;
        }
        internal PortraitPreviewFeature()
        {
            Action<IPortraitOwnedSceneLease>? copy = null;
            PortraitOwnedSceneRenderTargetAdapter? createdAdapter = null;
            fixture = new(true, allowAdvance: () => createdAdapter!.Images.InFlight == 0 && !createdAdapter.Images.HasCurrentFrame,
                afterAdvance: lease => copy!(lease), requestCurrent: () => SourceEpoch == 7 && SourceAlive);
            fixture.Runtime.AfterRetire = () => Trace.Add("native-retired");
            adapter = createdAdapter = new(new ScratchJointRead(), textures, () => true);
            ownedViewport = new((after, _) => { copy = after; return fixture.Broker; }, () => fixture.Runtime, adapter);
            photos = new(this, "photos"); window = new(this, "window"); diagnostics = new(this, "diagnostics"); windows = new(this, "windows");
            cacheWriter = new(this, "cache"); history = new(this, "history");
            target = new(this); session = new(this);
            Plugin.Framework.Update += Update; pluginInterface.UiBuilder.Draw += Draw;
        }
        internal void Start() { if (!ownedViewport!.TryStart(fixture.SourceSnapshot, 3)) throw new Exception("fixture start: " + ownedViewport.LastError); }
        internal void NoScene() { ownedViewport = null; }
        internal void Source(uint frame)
        {
            if (Environment.CurrentManagedThreadId != nativeThread)
                throw new Exception($"scratch callback thread drift: creation={nativeThread} callback={Environment.CurrentManagedThreadId}; fixture binds exact native thread");
            fixture.Render(frame);
        }
        internal void ForeignSource(uint frame) => fixture.Render(frame);
        internal Wrap CompleteCopy() { var image = new Wrap(900); textures.Completion.SetResult(image); return image; }
        internal Task CopySettled() => FeatureDisposeCases.Wait(() => adapter.Images.InFlight == 0);
        internal PortraitOwnedSceneTextureLifetime<IDalamudTextureWrap>.Borrow? BorrowImage() => adapter.Images.TryBorrow();
        internal void FailManaged() => cacheWriter.ManagedTask = Task.FromResult(false);
        internal void FailTarget() => target.Fail = true;
        internal void FailInputCleanup() => textures.InputDisposeFailure = true;
        internal AuxiliaryState EnablePendingAuxiliaryDrains()
        {
            var raw = new TaskCompletionSource<PortraitOwnedSceneRawFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            var proofIdentity = new PortraitOwnedSceneFirstFrameIdentity(Guid.NewGuid(), 7, 1, 2,
                new(1, 2, 3, 4, 5), new(Guid.NewGuid(), 1, 3, 7));
            ownedViewportProof = new(adapter.Images, _ => raw.Task, () => proofIdentity, () => true);
            var entered = new ManualResetEventSlim(false);
            var release = new ManualResetEventSlim(false);
            var com = new FakeCom { OnCall = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(2)); } };
            ownedViewportCom = new PortraitOwnedSceneRenderTargetComReader(com, () => 10, () => true);
            var completion = Task.Run(() => ownedViewportCom.TryRead(ComChecks.Kernel(), () => true, out _));
            return new AuxiliaryState(raw, entered, release, completion);
        }
        internal void RaiseDraw() => pluginInterface.UiBuilder.Raise();
        private void DisposeEditorOpening() => Trace.Add("dispose-opening");
        private void DisposeSourceCensus()
        {
            if (!Plugin.Framework.IsInFrameworkUpdateThread) throw new Exception("source release escaped Framework dispatcher");
            Trace.Add("dispose-census"); SourceEpoch++;
        }
        private static bool CancelLightingFitForEdit() => true;
        private static void DisposeNativeLive() { }
        private static void DisposeLightingFit() { }
        private static void FlushEditing() { }
        private static void QueueOwnedCacheIfNeeded() { }
        private static void DisposeLibrary() { }
        private static Task DrainFavoritePersistence() => Task.CompletedTask;
        private static Task DrainStorageObservation() => Task.CompletedTask;
        private void DisposeCaptureProgress() => CaptureProgressDisposals++;
        private void Update() => Updates++;
        private void Draw() => Draws++;
        private sealed class DisposalTarget(PortraitPreviewFeature owner)
        {
            internal bool Fail;
            internal int Disposals;
            internal string? LastError => null;
            internal bool AbortCapturedEditor() => true;
            internal void Dispose() { Disposals++; if (Fail) throw new IOException("injected target release failure"); owner.Trace.Add("dispose-target"); }
        }
        private sealed class DisposalSession(PortraitPreviewFeature owner)
        {
            internal bool Close() { owner.Trace.Add("close-source"); owner.SourceAlive = false; return true; }
        }
        private sealed class DisposalInterface { internal readonly DisposalUI UiBuilder = new(); }
        private sealed class DisposalUI { internal event Action? Draw; internal void Raise() => Draw?.Invoke(); }
        private sealed class DisposalObject(PortraitPreviewFeature owner, string name) : IDisposable
        {
            internal bool Sealed;
            internal Task ManagedTask = Task.CompletedTask;
            internal Task BeginDispose() { Sealed = true; owner.Trace.Add("seal-" + name); return ManagedTask; }
            internal Task DrainPhysicalAsync() => Task.CompletedTask;
            public void Dispose() { }
            internal void UnregisterFunc() { }
            internal void RemoveAllWindows() { }
        }
        // External GPU retirement boundary only; the linked product lifecycle owns aggregation.
        private sealed class DisposalPhotoReadback
        {
            internal Task Physical = Task.CompletedTask;
            internal int CloseCalls;
            internal Task Close() { CloseCalls++; return Physical; }
        }
    }
    internal sealed record AuxiliaryState(TaskCompletionSource<PortraitOwnedSceneRawFrame> Raw,
        ManualResetEventSlim ComEntered, ManualResetEventSlim Release, Task Completion);
    internal sealed class ScratchJointRead : IPortraitOwnedSceneRenderTargetReadApi
    {
        public bool TryRead(PortraitOwnedSceneImageOwner owner, IPortraitOwnedSceneLease lease, nint view,
            out PortraitOwnedSceneRenderTargetRead value)
        {
            var phase = new PortraitOwnedSceneSlotPhase(OwnedExperimentBrokerCases.Source, new(1, 2, 3, 4, 5), 7, 1,
                lease.NativeFrame, 6, Environment.CurrentManagedThreadId, true, true);
            var com = new PortraitOwnedSceneRenderTargetComDescription(503, 505, new(512, 512, 1, 1, 27, 1, 0, 0, 0x28, 0, 0), new(29, 4, 0, 1));
            value = new(owner, new(view, 402, 7, 403, 404), phase, 501, 502, 503, 504, 505, 512, 512, 400, 450, 0x1450, 0, 1, 1, com);
            return lease.IsCurrent && lease.CanAccess(view);
        }
    }
}
