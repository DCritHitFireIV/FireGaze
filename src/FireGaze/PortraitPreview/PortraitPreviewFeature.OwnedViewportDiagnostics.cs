using System.Text.Json;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitOwnedViewportSafetyState(bool Known,string State,bool BlocksOperations,bool PhysicalDrainCompleted,
    bool ProductionAdmission,long Attempt,Guid Installation,int Hooks,int InFlight,bool ReservationHeld,bool Recording,bool Failed,
    int ImageCopies,int ImageFrames,int ImageBorrowers,bool ImageResourcesClean,bool ComResourcesClean,bool ReadbackPending);

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitOwnedViewportSafetyState ReadOwnedViewportSafetyState()
    {
        var scene=ownedViewport;
        if(scene is null)return new(true,"Off",false,true,false,0,Guid.Empty,0,0,false,false,false,0,0,0,true,true,false);
        var host=ownedViewportBroker?.Snapshot;
        var drained=ownedViewportClosing&&ownedViewportDrain.IsCompletedSuccessfully;
        return new(true,drained?"Off":ownedViewportClosing?"Retiring":scene.Controller.State.ToString(),
            OwnedViewportBlocksOperations,drained,false,ownedViewportAttempt,host?.Installation??Guid.Empty,
            host?.Hooks??0,host?.InFlight??0,host?.ReservationHeld==true,host?.Recording==true,
            host?.Failed==true||scene.Controller.State==PortraitOwnedSceneControllerState.Faulted,
            scene.Images.InFlight,scene.Images.FrameCount,scene.Images.Borrowers,scene.ImageResourcesClean,
            ownedViewportCom?.IsPhysicallyClean??true,ownedViewportProof?.Pending==true);
    }
    private object OwnedViewportDiagnostics() => new
    {
        safety=ReadOwnedViewportSafetyState(),
        state = ownedViewport?.Controller.State.ToString()??"Off", status = OwnedViewportStatus,
        productionAdmission = false, experiment = true, closing = ownedViewportClosing,
        physicalDrainCompleted = ownedViewportClosing && ownedViewportDrain.IsCompletedSuccessfully,
        inFlight=ownedViewport?.Images.InFlight??0, frameCount=ownedViewport?.Images.FrameCount??0, borrowers=ownedViewport?.Images.Borrowers??0,
        imageResourcesClean=ownedViewport?.ImageResourcesClean??true,error = ownedViewport?.LastError, observer = ownedObserver,
        host = OwnedViewportHostDiagnostics(ownedViewportBroker?.Snapshot),
        firstFrame=JsonSerializer.SerializeToElement(ownedViewportProof?.Snapshot??new PortraitOwnedSceneFirstFrameSnapshot("None"),
            new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}),
    };

    // IPC exposes detached scalar identities. System.Text.Json intentionally refuses IntPtr,
    // including zero-valued receivers in an unused or already retired host snapshot.
    private static object? OwnedViewportHostDiagnostics(PortraitOwnedSceneJointHostSnapshot? host)
        => host is null ? null : new
        {
            installation = host.Installation, sessionEpoch = host.SessionEpoch, requestEpoch = host.RequestEpoch,
            mutationEpoch = host.MutationEpoch, counterDepth = host.CounterDepth, resourceDepth = host.ResourceDepth,
            recording = host.Recording, failed = host.Failed, hooks = host.Hooks, inFlight = host.InFlight,
            reservationHeld = host.ReservationHeld, physicalRetired = host.PhysicalRetired,
            productionAdmission = host.ProductionAdmission,
            resource = new
            {
                generation = host.Resource.Generation, depth = host.Resource.Depth,
                transition = host.Resource.Transition.ToString(), receiver = (long)host.Resource.Receiver,
                nativeReturn = host.Resource.NativeReturn, ready = host.Resource.Ready,
            },
            sourceReturns = host.SourceReturns.Select(row => new
            {
                installation = row.Installation, sessionEpoch = row.SessionEpoch, requestEpoch = row.RequestEpoch,
                mutationEpoch = row.MutationEpoch, nativeFrame = row.NativeFrame, thread = row.Thread, call = row.Call,
                source = new { editor = (long)row.Source.Editor, state = (long)row.Source.State,
                    model = (long)row.Source.Model, generation = row.Source.Generation, character = row.Source.Character },
            }),
            observation = new
            {
                observing = host.Observation.Observing, stop = host.Observation.Stop.ToString(), capture = host.Observation.Capture,
                overwrittenRecords = host.Observation.OverwrittenRecords, observedThreads = host.Observation.ObservedThreads,
                records = host.Observation.Records.Select(row => new
                {
                    sequence = row.Sequence, capture = row.Capture, call = row.Call, parentCall = row.ParentCall,
                    phase = row.Phase.ToString(), edge = row.Edge.ToString(), receiver = (long)row.Receiver,
                    thread = row.Thread, depth = row.Depth, timestamp = row.Timestamp,
                    frame = new { origin = row.Frame.Origin.ToString(), value = row.Frame.Value, sourceView = (long)row.Frame.SourceView },
                    floatArgument = row.FloatArgument, counterSlot = row.CounterSlot, nativeReturn = row.NativeReturn,
                }),
            },
        };
}
