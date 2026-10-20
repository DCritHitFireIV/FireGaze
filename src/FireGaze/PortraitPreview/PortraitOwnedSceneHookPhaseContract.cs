namespace FireGaze.PortraitPreview;

// Queueing is detached. Only the dispatcher's observed native edge can enter a
// scope; normal Framework calls and detached phase records cannot grant one.
internal interface IPortraitOwnedSceneHookPhaseEnvironment
{
    bool QueueRequest(PortraitIdentity source, uint slot, long requestEpoch);
    IDisposable? EnterSourcePhase(PortraitIdentity source, uint nativeFrame, long requestEpoch);
    IDisposable? EnterRetirementPhase(PortraitIdentity source, uint nativeFrame, long requestEpoch);
    void EndRequest(long requestEpoch);
}
