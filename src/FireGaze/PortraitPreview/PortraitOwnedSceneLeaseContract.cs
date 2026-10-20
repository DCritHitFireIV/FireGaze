namespace FireGaze.PortraitPreview;

internal enum PortraitOwnedSceneCreationCall { Construct, Setup }

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
    // Detached completion proof only; it never grants access to the released view.
    bool NativeReleaseConfirmed => false;
    bool ConfirmUnusedCreation() => false;
    bool TryBindCreatedView(nint view);
    // Creation owns an allocation before it owns a native slot. These permits
    // never authorize Render, general setters, or Release on an idle slot.
    IDisposable? EnterCreationCall(nint view, PortraitOwnedSceneCreationCall operation) => null;
    bool ConfirmConstructed(nint view) => false;
    bool ConfirmSetup(nint view) => false;
    bool MarkUnconstructedReleased(nint view) => false;
    bool CanAccess(nint view);
    // A view must be proven while still live before Release invalidates its fields.
    // A consumer which has not implemented the retirement protocol fails closed.
    bool TryBeginRetirement(nint view) => false;
    bool CanReleaseView(nint view);
    bool MarkReleased(nint view);
    IDisposable? EnterOwnedCall(nint view);
}
