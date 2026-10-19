namespace FireGaze.PortraitPreview;

/// <summary>
/// A native-phase permit, not an assertion that a numbered UI slot is reserved.
/// The production implementation is sealed and issued by the installed native
/// arbitration hooks. A permit is unusable outside its source Render phase.
/// </summary>
internal interface IPortraitOwnedSceneLease
{
    uint Slot { get; }
    PortraitIdentity Source { get; }
    long Epoch { get; }
    uint NativeFrame { get; }
    bool IsCurrent { get; }
    bool CanCreate { get; }
    bool TryBindCreatedView(nint view);
    bool CanAccess(nint view);
    // A view must be proven while still live before Release invalidates its fields.
    // A consumer which has not implemented the retirement protocol fails closed.
    bool TryBeginRetirement(nint view) => false;
    bool CanReleaseView(nint view);
    bool MarkReleased(nint view);
    IDisposable? EnterOwnedCall(nint view);
}
