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

// Only the concrete same-source environment can suspend native reads for a proved
// own counter and bootstrap a fresh token after its actual Original returns.
internal interface IPortraitOwnedSceneActiveMutationEnvironment
{
    bool TryQualifyActiveSource(PortraitIdentity source, uint nativeFrame, long requestEpoch, uint targetSlot, out nint renderer);
    IDisposable? BeginOwnedCounterMutation();
    IDisposable? ResumeOwnedCounterPhase(PortraitIdentity source, uint nativeFrame, long requestEpoch, nint renderer);
}
