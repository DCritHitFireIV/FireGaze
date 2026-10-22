using System.Numerics;
using FireGaze.PortraitPreview;

internal static class ObserverFeatureCases
{
    internal static void Run(Action<bool, string> check)
    {
#if OWNED_OBSERVER_FEATURE
        var source = new PortraitOwnedSceneObserverCameraSnapshot(Vector3.UnitZ, Vector3.Zero, 0, 0, 1);
        var side = new PortraitOwnedSceneObserverCameraSnapshot(Vector3.UnitX, Vector3.Zero, MathF.PI / 2, 0, 1);
        foreach (var gesture in new Action<PortraitPreviewFeature>[]
        {
            feature => feature.SetOwnedObserver(side),
            feature => feature.NudgeOwnedObserver(Vector3.UnitX),
            feature => feature.ResetOwnedObserver(),
            feature => feature.CloseOwnedViewportView(),
        })
        {
            var feature = new PortraitPreviewFeature();
            var first = feature.ReplaceScene(source, side);
            gesture(feature);
            var second = feature.ReplaceScene(source, side);
            feature.DrainQueue();
            check(first.SetCalls == 0 && second.SetCalls == 0 && feature.StopCalls == 0 && feature.Observer == source,
                "an old queued camera/reset/close gesture cannot write or stop a replacement scene");
        }

        var current = new PortraitPreviewFeature();
        var session = current.ReplaceScene(source, source);
        current.SetOwnedObserver(side);
        check(session.SetCalls == 0 && current.Observer == source,
            "observer API never writes a session before its Framework mutation queue runs");
        current.DrainQueue();
        check(session.SetCalls == 1 && current.Observer == side,
            "a current observer gesture applies exactly once after its queue runs");
        current.SetOwnedObserver(side); current.DrainQueue();
        check(session.SetCalls == 1,
            "submitting the identical observer camera does not revoke its published image");
        current.ResetOwnedObserver(); current.DrainQueue();
        check(session.SetCalls == 2 && current.Observer == source,
            "reset restores the matching session source camera through the same guarded route");
        current.NudgeOwnedObserver(Vector3.UnitX); current.DrainQueue();
        check(session.SetCalls == 3 && current.Observer!.Value.Target.X > 0,
            "a genuine current screen-button nudge reaches only the current detached observer");
        for (var press = 2; press <= 4; press++)
        {
            current.NudgeOwnedObserver(Vector3.UnitX); current.DrainQueue();
            check(session.SetCalls == press + 2
                && MathF.Abs(current.Observer!.Value.Target.X - press * .007479697f) < .00001f,
                "each screen-button press applies one equal displacement without lost clicks or inherited momentum");
        }
        current.NudgeOwnedObserver(-Vector3.UnitX); current.DrainQueue();
        check(session.SetCalls == 7 && MathF.Abs(current.Observer!.Value.Target.X - .022439091f) < .00001f,
            "the opposite screen-button press moves immediately in the requested direction");

        foreach (var transition in new[] { "attempt", "closing", "source" })
        {
            var feature = new PortraitPreviewFeature(); var scene = feature.ReplaceScene(source, source);
            feature.SetOwnedObserver(side);
            feature.Invalidate(transition); feature.DrainQueue();
            check(scene.SetCalls == 0 && feature.Observer == source,
                "queued camera rejects changed attempt/retirement/source identity before native session access: " + transition);
        }

        var closed = new PortraitPreviewFeature(); closed.ReplaceScene(source, source);
        closed.CloseOwnedViewportView(); closed.DrainQueue();
        check(closed.StopCalls == 1 && !closed.Visible,
            "the current viewport close still stops its owner and hides the drained view");
        var delayedClose = new PortraitPreviewFeature(); delayedClose.ReplaceScene(source, source);
        delayedClose.DelayDrain(); delayedClose.CloseOwnedViewportView(); delayedClose.DrainQueue();
        check(!delayedClose.Visible && delayedClose.BlocksOperations && delayedClose.StopCalls == 1,
            "closing hides the viewport immediately while the independent physical drain still blocks operations");
        delayedClose.CompleteDrain();
        check(!delayedClose.Visible && !delayedClose.BlocksOperations,
            "a completed asynchronous drain cannot leave a stale visible viewport");
        var retired = new PortraitPreviewFeature(); retired.ReplaceScene(source, source);
        retired.Invalidate("closing"); retired.Invalidate("source");
        retired.CloseOwnedViewportView(); retired.DrainQueue();
        check(!retired.Visible && retired.StopCalls == 0,
            "the current already-retired viewport can return to the composition view without sampling its old source");
        var rejected = new PortraitPreviewFeature(); var rejecting = rejected.ReplaceScene(source, source);
        rejecting.Accept = false;
        rejected.SetOwnedObserver(side); rejected.DrainQueue();
        check(rejected.Observer == source && rejecting.SetCalls == 1,
            "a native session rejection cannot publish a camera which was never applied");
        var absent = new PortraitPreviewFeature();
        absent.NudgeOwnedObserver(Vector3.One); absent.CloseOwnedViewportView(); absent.DrainQueue();
        check(absent.StopCalls == 0, "an absent scene cannot accept or close a future session");
#else
        check(false, "production observer Feature methods must be directly exercised by the queue regression");
#endif
    }
}

#if OWNED_OBSERVER_FEATURE
namespace FireGaze.PortraitPreview
{
    // Substitute only external scene/queue boundaries. The tested observer methods are
    // the source-linked production partial, including the real queued closure guards.
    internal sealed unsafe partial class PortraitPreviewFeature
    {
        private ObserverFeatureSession? ownedViewport;
        private PortraitOwnedSceneObserverCameraSnapshot? ownedObserver, ownedSourceObserver;
        private long ownedViewportAttempt;
        private object? ownedViewportContext;
        private object? currentContext;
        private bool ownedViewportClosing, ownedViewportViewRequested, drainCompleted = true;
        private bool OwnedViewportBlocksOperations => ownedViewport is not null && (!ownedViewportClosing || !drainCompleted);
        private readonly Queue<Action> queue = new();
        internal int StopCalls { get; private set; }
        internal bool Visible => ownedViewportViewRequested;
        internal bool BlocksOperations => OwnedViewportBlocksOperations;
        internal void DelayDrain() => drainCompleted = false;
        internal void CompleteDrain() => drainCompleted = true;
        internal PortraitOwnedSceneObserverCameraSnapshot? Observer => ownedObserver;
        internal ObserverFeatureSession ReplaceScene(PortraitOwnedSceneObserverCameraSnapshot initial,
            PortraitOwnedSceneObserverCameraSnapshot source)
        {
            ownedViewport = new(); ownedObserver = initial; ownedSourceObserver = source;
            ownedViewportAttempt++; ownedViewportClosing = false; ownedViewportViewRequested = true;
            ownedViewportContext = currentContext = new object(); return ownedViewport;
        }
        internal void Invalidate(string transition)
        {
            if (transition == "attempt") ownedViewportAttempt++;
            else if (transition == "closing") ownedViewportClosing = true;
            else currentContext = new object();
        }
        internal void DrainQueue() { while (queue.TryDequeue(out var action)) action(); }
        private void Mutate(Action action, bool preserveSourceCensus = false)
        {
            if (!preserveSourceCensus) throw new Exception("observer gesture tried to mutate portrait state");
            queue.Enqueue(action);
        }
        private object? ReadSourceCensusContext() => currentContext;
        private void StopOwnedViewport(string reason)
        { if (ownedViewport is null || ownedViewportClosing) return; StopCalls++; ownedViewportClosing = true; }
    }

    internal sealed class ObserverFeatureSession
    {
        internal int SetCalls;
        internal bool Accept = true;
        internal bool TrySetObserverCamera(PortraitOwnedSceneObserverCameraSnapshot camera)
        { SetCalls++; return Accept; }
    }
}
#endif
