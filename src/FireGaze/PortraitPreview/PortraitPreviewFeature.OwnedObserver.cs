using System.Numerics;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    public void SetOwnedObserver(PortraitOwnedSceneObserverCameraSnapshot camera) => MutateOwnedObserver(() =>
    {
        if (ownedObserver == camera) return;
        if (ownedViewport?.TrySetObserverCamera(camera) == true) ownedObserver = camera;
    });

    public void NudgeOwnedObserver(Vector3 move) => MutateOwnedObserver(() =>
    {
        if (ownedViewportClosing || ownedViewport is null || ownedObserver is not { } current) return;
        // A button press is one bounded gesture; momentum must not survive to a later click.
        var navigation = new PortraitOwnedViewportNavigation();
        var neutral = new PortraitObserverInput(Vector3.Zero, Vector2.Zero, true, true, false);
        navigation.Step(ownedViewportAttempt, current, neutral, .05, out _);
        if (navigation.Step(ownedViewportAttempt, current,
                new PortraitObserverInput(move, Vector2.Zero, true, true, false), .05, out var next)
            && ownedViewport.TrySetObserverCamera(next)) ownedObserver = next;
    });

    public void ResetOwnedObserver() => MutateOwnedObserver(() =>
    {
        if (ownedSourceObserver is { } camera && ownedObserver != camera
            && ownedViewport?.TrySetObserverCamera(camera) == true) ownedObserver = camera;
    });
    public void CloseOwnedViewportView() => MutateOwnedObserver(() =>
    {
        StopOwnedViewport("The independent viewport was closed.");
        // Visibility is user intent. Physical cleanup keeps its own operation barrier.
        ownedViewportViewRequested = false;
    }, closingAllowed: true);

    private void MutateOwnedObserver(Action change, bool closingAllowed = false)
    {
        var expectedSession = ownedViewport;
        var expectedAttempt = Interlocked.Read(ref ownedViewportAttempt);
        Mutate(() =>
        {
            // UI requests can wait behind a source replacement/retirement. An old
            // observer gesture must never write or stop the next independent scene.
            if (expectedSession is null || !ReferenceEquals(ownedViewport, expectedSession)
                || Interlocked.Read(ref ownedViewportAttempt) != expectedAttempt) return;
            if (!closingAllowed && (ownedViewportClosing
                || ReadSourceCensusContext() != ownedViewportContext)) return;
            change();
        }, preserveSourceCensus: true);
    }
}
