namespace FireGaze.PortraitPreview;

internal sealed class PortraitOwnedSceneLease : IPortraitOwnedSceneLease
{
    private readonly PortraitOwnedSceneHooks owner;
    private bool invalid, released;
    private int callDepth, callThread;
    internal PortraitOwnedSceneLease(PortraitOwnedSceneHooks owner, uint slot, PortraitIdentity source, long epoch)
    { this.owner = owner; Slot = slot; Source = source; Epoch = epoch; }
    public uint Slot { get; }
    public PortraitIdentity Source { get; }
    public long Epoch { get; }
    public uint NativeFrame { get; private set; }
    internal nint BoundView { get; private set; }
    internal bool WasReleased => released;
    internal bool RetirementStarted { get; private set; }
    internal bool HasOpenOwnedCall => callDepth != 0;
    public bool IsCurrent => !invalid && !released && owner.IsLeaseCurrent(this);
    public bool CanCreate => BoundView == 0 && IsCurrent;
    public bool TryBindCreatedView(nint view)
    {
        if (view == 0 || view == Source.Model || !CanCreate) return false;
        BoundView = view; return true;
    }
    public bool CanAccess(nint view) => view != 0 && view == BoundView && IsCurrent;
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
    internal bool IsInOwnedCall => callDepth > 0 && callThread == Environment.CurrentManagedThreadId && IsCurrent;
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
            owner.callDepth--; if (owner.callDepth == 0) owner.callThread = 0;
        }
    }
}
