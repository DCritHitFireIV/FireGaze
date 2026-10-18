namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private readonly PortraitLightDrag lightDrag = new();

    // Published detached values only. UI code never resolves or reads a native editor.
    public PortraitDirectionalLightView? DirectionalLight => session.IsActive && observedEditor is { } snapshot
        && PortraitLightDirection.TryNative(snapshot.Composition.DirectionalVerticalAngle,
            snapshot.Composition.DirectionalHorizontalAngle,out var angles)
        ? new(angles.VerticalDegrees,angles.HorizontalDegrees) : null;

    public void BeginLightDrag(long token) => Mutate(() =>
    {
        if (lightDrag.IsActive || !lightDrag.CanBegin(token)) return;
        nextEditorSample=DateTime.MinValue;
        PollEditing();
        if (!TryCaptureLight(out var identity,out var key,out var current)) return;
        HandleHistoryBatchResult(historyBatch.Flush());
        _=lightDrag.Begin(token,identity,key,PortraitNativeLightAngles.From(current.Composition));
    });

    public void MoveLightDrag(long token, float vertical, float horizontal)
        => Mutate(() => MoveLightDragNow(token,vertical,horizontal));

    public void EndLightDrag(long token, float vertical, float horizontal) => Mutate(() =>
    {
        if (!lightDrag.Owns(token)) return;
        MoveLightDragNow(token,vertical,horizontal);
        if (!lightDrag.Owns(token)) return;
        // Capture the final real state while history still treats this as one held gesture.
        // ForHistory keeps playback from entering a lighting history record.
        nextEditorSample=DateTime.MinValue;
        PollEditing();
        lightDrag.Cancel();
        HandleHistoryBatchResult(historyBatch.Flush());
        PublishHistoryNavigation();
    });

    public void CancelLightDrag(long token) => Mutate(() =>
    {
        if (!lightDrag.Owns(token)) return;
        nextEditorSample=DateTime.MinValue;
        PollEditing();
        CancelLightDrag();
        HandleHistoryBatchResult(historyBatch.Flush());
        PublishHistoryNavigation();
    });

    private bool TryCaptureLight(out PortraitIdentity identity, out PortraitHistoryKey key, out PortraitEditorSnapshot current)
    {
        identity=default;key=default;current=null!;
        if (!session.IsActive || !target.CanBlockSave || !target.TryResolve(out identity,out _,out _)
            || target.CaptureEditorState(identity) is not { } captured || GetHistoryKey(identity,captured) is not { } source) return false;
        current=captured;key=source;
        return true;
    }

    private void MoveLightDragNow(long token, float vertical, float horizontal)
    {
        if (!lightDrag.Owns(token)) return;
        if (!TryCaptureLight(out var identity,out var key,out var current)) { CancelLightDrag();return; }
        var expected=PortraitNativeLightAngles.From(current.Composition);
        if (!lightDrag.Prepare(token,identity,key,expected,vertical,horizontal,out var desired)) return;
        if (!target.SetDirectionalLightAngles(identity,expected,desired)) { CancelLightDrag();return; }
        lightDrag.Accepted(token,desired);
    }

    private void ObserveLightDrag(PortraitIdentity identity, PortraitHistoryKey key, PortraitEditorSnapshot current)
    {
        if (!session.IsActive || !target.CanBlockSave) { CancelLightDrag();return; }
        _=lightDrag.Observe(identity,key,PortraitNativeLightAngles.From(current.Composition));
    }

    private void CancelLightDrag() => lightDrag.Cancel();
}
