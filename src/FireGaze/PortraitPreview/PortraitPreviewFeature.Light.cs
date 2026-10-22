namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private readonly PortraitLightDrag lightDrag = new();
    private PortraitLightInputTrace? lightInputTrace;
    private long lightInputToken;

    // Published detached values only. UI code never resolves or reads a native editor.
    public PortraitDirectionalLightView? DirectionalLight => session.IsActive && observedEditor is { } snapshot
        && PortraitLightDirection.TryNative(snapshot.Composition.DirectionalVerticalAngle,
            snapshot.Composition.DirectionalHorizontalAngle,out var angles)
        ? new(angles.VerticalDegrees,angles.HorizontalDegrees) : null;

    public void BeginLightDrag(long token) => Mutate(() =>
    {
        if (!CancelLightingFitForEdit()) return;
        if (lightDrag.IsActive || !lightDrag.CanBegin(token)) return;
        var trace=new PortraitLightInputTrace(message=>Plugin.Log.Information(message));
        trace.Enter(PortraitLightTraceStage.GestureBegin,()=>
        {
            nextEditorSample=DateTime.MinValue;
            PortraitLightInputTrace.Run(PortraitLightTraceStage.HistoryPoll,PollEditing);
            if (!TryCaptureLight(out var identity,out var key,out var current)) return;
            PortraitLightInputTrace.Run(PortraitLightTraceStage.HistoryFlush,()=>HandleHistoryBatchResult(historyBatch.Flush()));
            if (lightDrag.Begin(token,identity,key,PortraitNativeLightAngles.From(current.Composition)))
            { lightInputTrace=trace;lightInputToken=token; }
        });
    });

    public void MoveLightDrag(long token, float vertical, float horizontal)
        => Mutate(() => TraceLightInput(token,PortraitLightTraceStage.GestureMove,()=>MoveLightDragNow(token,vertical,horizontal)));

    public void EndLightDrag(long token, float vertical, float horizontal) => Mutate(() => TraceLightInput(token,PortraitLightTraceStage.GestureEnd,()=>
    {
        if (!lightDrag.Owns(token)) return;
        MoveLightDragNow(token,vertical,horizontal);
        if (!lightDrag.Owns(token)) return;
        // Capture the final real state while history still treats this as one held gesture.
        // ForHistory keeps playback from entering a lighting history record.
        nextEditorSample=DateTime.MinValue;
        PortraitLightInputTrace.Run(PortraitLightTraceStage.HistoryPoll,PollEditing);
        lightDrag.Cancel();
        PortraitLightInputTrace.Run(PortraitLightTraceStage.HistoryFlush,()=>HandleHistoryBatchResult(historyBatch.Flush()));
        PublishHistoryNavigation();
    }));

    public void CancelLightDrag(long token) => Mutate(() => TraceLightInput(token,PortraitLightTraceStage.GestureCancel,()=>
    {
        if (!lightDrag.Owns(token)) return;
        nextEditorSample=DateTime.MinValue;
        PortraitLightInputTrace.Run(PortraitLightTraceStage.HistoryPoll,PollEditing);
        CancelLightDrag();
        PortraitLightInputTrace.Run(PortraitLightTraceStage.HistoryFlush,()=>HandleHistoryBatchResult(historyBatch.Flush()));
        PublishHistoryNavigation();
    }));

    private void TraceLightInput(long token,PortraitLightTraceStage phase,Action operation)
    {
        if (lightDrag.Owns(token) && lightInputToken==token && lightInputTrace is { } trace) trace.Enter(phase,operation);
        else operation();
    }

    private bool TryCaptureLight(out PortraitIdentity identity, out PortraitHistoryKey key, out PortraitEditorSnapshot current)
    {
        identity=default;key=default;current=null!;
        using var phase=PortraitLightInputTrace.Measure(PortraitLightTraceStage.CaptureState);
        if (!session.IsActive || !target.CanBlockSave || !target.TryResolve(out identity,out _,out _)
            || target.CaptureEditorState(identity) is not { } captured || GetHistoryKey(identity,captured) is not { } source)
        { phase.Complete();return false; }
        current=captured;key=source;
        phase.Complete();
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
