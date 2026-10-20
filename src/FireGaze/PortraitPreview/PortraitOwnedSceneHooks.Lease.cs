namespace FireGaze.PortraitPreview;

internal enum PortraitOwnedSceneCreationStage { Unbound, Allocated, Constructing, Constructed, SetupPending, Owned }

internal sealed class PortraitOwnedSceneLease : IPortraitOwnedSceneLease
{
    private readonly PortraitOwnedSceneHooks owner;
    private bool invalid, released;
    private int callDepth, callThread;
    private PortraitOwnedSceneCreationCall? creationCall;
    internal PortraitOwnedSceneLease(PortraitOwnedSceneHooks owner, uint slot, PortraitIdentity source, long epoch)
    { this.owner = owner; Slot = slot; Source = source; Epoch = epoch; }
    public uint Slot { get; }
    public PortraitIdentity Source { get; }
    public long Epoch { get; }
    public uint NativeFrame { get; private set; }
    internal nint BoundView { get; private set; }
    internal PortraitOwnedSceneCreationStage CreationStage { get; private set; }
    internal bool WasReleased => released;
    internal bool RetirementStarted { get; private set; }
    internal bool HasOpenOwnedCall => callDepth != 0;
    public bool IsCurrent => !invalid && !released && owner.IsLeaseCurrent(this);
    public bool CanCreate => CreationStage == PortraitOwnedSceneCreationStage.Unbound && BoundView == 0 && IsCurrent;
    public bool TryBindCreatedView(nint view)
    {
        if (view == 0 || view == Source.Model || callDepth != 0 || !CanCreate) return false;
        BoundView = view; CreationStage = PortraitOwnedSceneCreationStage.Allocated; return true;
    }
    public IDisposable? EnterCreationCall(nint view, PortraitOwnedSceneCreationCall operation)
    {
        if (view == 0 || view != BoundView || invalid || released || callDepth != 0
            || operation == PortraitOwnedSceneCreationCall.Construct && CreationStage != PortraitOwnedSceneCreationStage.Allocated
            || operation == PortraitOwnedSceneCreationCall.Setup && CreationStage != PortraitOwnedSceneCreationStage.Constructed
            || operation is not (PortraitOwnedSceneCreationCall.Construct or PortraitOwnedSceneCreationCall.Setup)
            || !owner.IsCreationSlotIdle(this)) return null;
        CreationStage = operation == PortraitOwnedSceneCreationCall.Construct
            ? PortraitOwnedSceneCreationStage.Constructing : PortraitOwnedSceneCreationStage.SetupPending;
        creationCall = operation; callDepth = 1; callThread = Environment.CurrentManagedThreadId;
        return new CallScope(this);
    }
    internal bool IsCreationCall(PortraitOwnedSceneCreationCall operation)
        => callDepth != 0 && callThread == Environment.CurrentManagedThreadId && creationCall == operation;
    public bool ConfirmConstructed(nint view)
    {
        if (view == 0 || view != BoundView || invalid || released
            || CreationStage != PortraitOwnedSceneCreationStage.Constructing
            || !IsCreationCall(PortraitOwnedSceneCreationCall.Construct) || !owner.IsCreationSlotIdle(this)) return false;
        CreationStage = PortraitOwnedSceneCreationStage.Constructed; return true;
    }
    public bool ConfirmSetup(nint view)
    {
        if (view == 0 || view != BoundView || invalid || released
            || CreationStage != PortraitOwnedSceneCreationStage.SetupPending
            || !IsCreationCall(PortraitOwnedSceneCreationCall.Setup) || !owner.IsCreationSlotOwned(this)) return false;
        CreationStage = PortraitOwnedSceneCreationStage.Owned; return true;
    }
    public bool MarkUnconstructedReleased(nint view)
    {
        // Only the exact raw allocation which never entered Ctor can use this
        // detached confirmation. No native slot/view fields may be inspected.
        if (view == 0 || view != BoundView || released || callDepth != 0
            || CreationStage != PortraitOwnedSceneCreationStage.Allocated) return false;
        released = true; return true;
    }
    public bool CanAccess(nint view) => CreationStage == PortraitOwnedSceneCreationStage.Owned
        && creationCall is null && view != 0 && view == BoundView && IsCurrent;
    public bool CanReleaseView(nint view) => CanAccess(view);
    public bool TryBeginRetirement(nint view)
    {
        if (RetirementStarted) return CanReleaseView(view) && IsInOwnedCall;
        if (!CanReleaseView(view) || !IsInOwnedCall) return false;
        RetirementStarted = true; return true;
    }
    public bool MarkReleased(nint view)
    {
        if (view == 0 || view != BoundView || invalid || released || callDepth == 0
            || callThread != Environment.CurrentManagedThreadId || !owner.CanConfirmRetirement(this)) return false;
        released = true; return true;
    }
    public IDisposable? EnterOwnedCall(nint view)
    {
        if (!(view == 0 ? CanCreate : CanAccess(view))
            || callDepth > 0 && callThread != Environment.CurrentManagedThreadId) return null;
        callDepth++; callThread = Environment.CurrentManagedThreadId; return new CallScope(this);
    }
    // The entry scope already proved exact ownership before calling native code.
    // That Original may temporarily expose State5 or half-configured context/
    // camera fields. Reentrant calls for the same bound view use this token;
    // general access and the call's return still recheck stable slot ownership.
    internal bool IsInOwnedCall => callDepth > 0 && callThread == Environment.CurrentManagedThreadId
        && !invalid && !released && owner.IsNativeCallPhaseCurrent(this);
    internal bool IsSelfCall(nint view) => view == BoundView && IsInOwnedCall;
    internal void SetNativeFrame(uint frame) => NativeFrame = frame;
    internal void Invalidate() => invalid = true;
    private sealed class CallScope(PortraitOwnedSceneLease owner) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (owner.callThread != Environment.CurrentManagedThreadId) { owner.Invalidate(); return; }
            owner.callDepth--; if (owner.callDepth == 0) { owner.callThread = 0; owner.creationCall = null; }
        }
    }
}
