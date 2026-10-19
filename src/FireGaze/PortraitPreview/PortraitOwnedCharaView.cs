using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Owns one independently allocated native portrait. It has no global registration, no default
/// slot and no automatic constructor side effects. Only an arbitration-issued native-phase
/// lease can create, advance or retire it. Production admission remains disabled until the
/// native environment can prove exclusive context, camera and ClientObject ownership.
/// </summary>
internal sealed unsafe class PortraitOwnedCharaView(IPortraitOwnedCharaViewNativeApi api)
{
    private PortraitOwnedCharaViewSnapshot? snapshot;
    private nint view;
    private uint slot;
    private long epoch;
    private uint lastFrame;
    private bool hasFrame, busy, constructed, setupStarted, released, compositionApplied, retireRequested;
    private nint character, camera, cameraManager;
    private uint generation;
    private int nativeDepth;

    public bool IsReady { get; private set; }
    public bool IsRetired { get; private set; }
    public bool IsQuarantined { get; private set; }
    public string? LastError { get; private set; }

    public bool TryCreate(PortraitOwnedCharaViewSnapshot source, IPortraitOwnedSceneLease? lease)
    {
        if (snapshot is not null || view != 0 || IsRetired || IsQuarantined || source is null || lease is null
            || !lease.IsCurrent || !lease.CanCreate || lease.Slot >= 8 || lease.Epoch <= 0
            || lease.Source != source.Source) return false;
        snapshot = source;
        slot = lease.Slot;
        epoch = lease.Epoch;
        try
        {
            using (var scope = lease.EnterOwnedCall(0))
            {
                if (scope is null || !lease.IsCurrent || !lease.CanCreate) return Fail("The native creation phase expired.");
                view = api.Allocate();
            }
            if (view == 0) return Fail("The UI memory space could not allocate the portrait.");
            if (!lease.TryBindCreatedView(view))
            {
                // This allocation has never entered Ctor/Setup and therefore owns no native
                // slot, child model, texture or camera. Free only its own raw UI allocation.
                api.FreeUnconstructed(view);
                view = 0;
                return Fail("The native creation permit expired before binding the allocation.");
            }
            nint result = 0;
            using (var scope = lease.EnterCreationCall(view, PortraitOwnedSceneCreationCall.Construct))
            {
                if (scope is null) return FreeUnconstructed(lease);
                result = api.Construct(view);
                constructed = true;
                if (result != view) return Quarantine("The native portrait constructor returned another allocation.");
                if (!lease.ConfirmConstructed(view)) return Quarantine("Portrait construction lost its native permit.");
            }
            var data = source.CreateSetupData();
            using (var scope = lease.EnterCreationCall(view, PortraitOwnedSceneCreationCall.Setup))
            {
                if (scope is null) return Quarantine("Portrait setup lost its native permit.");
                setupStarted = true;
                try { api.Setup(view, slot, data); }
                catch
                {
                    // A throwing Setup is eligible only for cleanup, and only
                    // if the exact native slot really belongs to this view.
                    lease.ConfirmSetup(view);
                    throw;
                }
                if (!lease.ConfirmSetup(view)) return Quarantine("Portrait setup did not confirm exact native slot ownership.");
            }
            if (!Owns(lease)) return Quarantine("The native scene was replaced during setup.");
            source.CopyAppearanceTo(ref *(CharaViewPortrait*)view);
            return Owns(lease);
        }
        catch (Exception ex)
        {
            LastError = "Native portrait creation failed: " + ex.Message;
            // A constructor which did not return has an uncertain native object lifetime.
            // Never guess whether a half-constructed texture needs a virtual destructor.
            if (!constructed && view != 0) return Quarantine(LastError);
            if (view != 0 && !TryRetire(lease)) return Quarantine(LastError);
            return false;
        }
    }

    private bool FreeUnconstructed(IPortraitOwnedSceneLease lease)
    {
        var allocation = view;
        api.FreeUnconstructed(allocation);
        view = 0;
        IsRetired = true;
        if (!lease.MarkUnconstructedReleased(allocation))
            return Fail("The raw portrait allocation was freed but arbitration could not confirm release.");
        return Fail("The creation permit expired before entering the native constructor.");
    }

    /// <summary>Called only from the source's post-original Render phase, using native FrameCount.</summary>
    public bool Advance(IPortraitOwnedSceneLease lease, uint nativeFrame)
    {
        if (busy || retireRequested || IsRetired || IsQuarantined || view == 0 || nativeFrame != lease.NativeFrame) return false;
        if (!Owns(lease)) return Quarantine("The native scene ownership changed.");
        if (hasFrame && lastFrame == nativeFrame) return true;
        busy = true;
        IsReady = false;
        try
        {
            lastFrame = nativeFrame; hasFrame = true;
            // Native Render includes Update. Calling Update separately would advance animation,
            // pause transitions and native light/camera writes twice in the same game frame.
            if (!Call(lease, () => api.Render(view, nativeFrame))) return Stop(lease,"Native Render lost its scene or received a retirement request.");
            if (!TryReadCharacter(lease, out var actor))
            {
                if (!Owns(lease)) return Quarantine("The native scene was replaced while loading.");
                if (character != 0) throw new InvalidOperationException("The portrait character allocation changed.");
                return true;
            }
            if (!compositionApplied)
            {
                if (!ApplyComposition(lease, actor)) return Stop(lease,"Scene ownership or character changed while applying composition.");
                compositionApplied = true;
            }
            if (!TryReadCharacter(lease, out _) || ((CharaViewPortrait*)view)->IsAnimationPauseStatePending) return true;
            bool paused = false;
            if (!Call(lease, () => paused = api.IsPaused(view))) return Stop(lease,"The pause check lost scene ownership.");
            IsReady = paused == snapshot!.Composition.Paused;
            return true;
        }
        catch (Exception ex)
        {
            LastError = "Native portrait advance failed: " + ex.Message;
            if (!TryRetire(lease)) Quarantine(LastError);
            return false;
        }
        finally { busy = false; }
    }

    public void RequestRetire() { retireRequested = true; IsReady = false; }

    /// <summary>
    /// Lends the ready view only synchronously inside the current native phase. Consumers must
    /// finish native texture queries/copying in the callback and retain no native pointers.
    /// This method does not change the observer or the source editor's camera.
    /// </summary>
    public bool UseReadyView(IPortraitOwnedSceneLease lease, Action<nint> readView)
    {
        if (!IsReady || busy || retireRequested || !hasFrame || lastFrame != lease.NativeFrame || !Owns(lease)) return false;
        busy = true;
        try
        {
            if (!TryReadCharacter(lease,out _) || ((CharaViewPortrait*)view)->IsAnimationPauseStatePending) return false;
            return Call(lease,() => readView(view)) || Stop(lease,"Native ownership changed during ready-view capture.");
        }
        catch (Exception ex) { return Stop(lease,"Ready-view capture failed: " + ex.Message); }
        finally { busy = false; }
    }

    public bool TryRetire(IPortraitOwnedSceneLease lease)
    {
        IsReady = false;
        retireRequested = true;
        if (IsRetired) return true;
        // A callback reached through the view's own native Render/setter cannot destroy that
        // view while the original native frame still has its `this` pointer on the stack.
        if (nativeDepth != 0) return Fail("Native retirement is pending the current call's return.");
        if (IsQuarantined || view == 0 || !Matches(lease) || !lease.CanReleaseView(view))
            return Quarantine("Retirement has no current native ownership permit.");
        try
        {
            var retiring = view;
            // Keep the native call permit through Release + virtual Dtor. Dtor(1) frees the
            // UI-space allocation, but does not call Release itself. MarkReleased runs last.
            using var scope = lease.EnterOwnedCall(retiring);
            if (scope is null || !lease.CanReleaseView(retiring) || !lease.TryBeginRetirement(retiring))
                return Quarantine("Retirement phase expired before native ownership was recorded.");
            if (setupStarted && !released)
            {
                api.Release(retiring);
                released = true;
                if (!lease.CanReleaseView(retiring)) return Quarantine("Native Release lost the slot permit.");
            }
            if (constructed) api.Destroy(retiring, 1);
            else api.FreeUnconstructed(retiring);
            // The pointer may already have been freed; never dereference it after Dtor.
            view = 0;
            IsRetired = true;
            if (!lease.MarkReleased(retiring))
            {
                LastError = "The native allocation retired but arbitration could not confirm release.";
                return false;
            }
            return true;
        }
        catch (Exception ex) { return Quarantine("Native retirement did not complete: " + ex.Message); }
    }

    private bool Matches(IPortraitOwnedSceneLease lease) => snapshot is not null && lease.Slot == slot
        && lease.Epoch == epoch && lease.Source == snapshot.Source && lease.IsCurrent;
    private bool Owns(IPortraitOwnedSceneLease lease) => view != 0 && Matches(lease) && lease.CanAccess(view);
    private bool Call(IPortraitOwnedSceneLease lease, Action action)
    {
        if (!Owns(lease)) return false;
        using var scope = lease.EnterOwnedCall(view);
        if (scope is null || !Owns(lease)) return false;
        if (character != 0 && !BoundCharacterMatches(lease) || retireRequested) return false;
        nativeDepth++;
        try { action(); }
        finally { nativeDepth--; }
        return Owns(lease) && !retireRequested && (character == 0 || BoundCharacterMatches(lease));
    }
    private bool BoundCharacterMatches(IPortraitOwnedSceneLease lease)
    {
        nint found;
        nativeDepth++;
        try { found = api.GetCharacter(view); }
        finally { nativeDepth--; }
        if (!Owns(lease) || found != character || found == 0) return false;
        var actor = (Character*)found;
        var current = (CharaViewPortrait*)view;
        return *(uint*)((byte*)actor + 0x74) == generation && actor->DrawData.OwnerObject == actor
            && (nint)current->Camera == camera && current->CameraManager == cameraManager;
    }
    private bool TryReadCharacter(IPortraitOwnedSceneLease lease, out nint actor)
    {
        actor = 0;
        if (!Owns(lease)) return false;
        var current = (CharaViewPortrait*)view;
        if (current->State != 6 || !current->CharaViewPortraitCharacterLoaded
            || current->ClientObjectIndex != slot || current->ClientObjectId != slot + 0xF0
            || current->Camera == null || current->CameraManager == 0) return false;
        nint found = 0;
        if (!Call(lease, () => found = api.GetCharacter(view))) return false;
        if (found == 0 || found == (nint)snapshot!.Source.Character || found == api.WorldCharacter || !Owns(lease)) return false;
        var candidate = (Character*)found;
        var foundGeneration = *(uint*)((byte*)candidate + 0x74);
        if (foundGeneration == 0 || candidate->DrawData.OwnerObject != candidate) return false;
        if (character != 0 && (character != found || generation != foundGeneration || camera != (nint)current->Camera
            || cameraManager != current->CameraManager)) return false;
        character = found; generation = foundGeneration; camera = (nint)current->Camera; cameraManager = current->CameraManager;
        actor = found;
        return true;
    }
    private bool ApplyComposition(IPortraitOwnedSceneLease lease, nint actor)
    {
        var c = snapshot!.Composition;
        var current = (CharaViewPortrait*)view;
        current->PoseClassJob = c.PoseClassJob;
        if (!Call(lease, () => api.SetPoseTimed(view, c.BannerTimeline, c.AnimationProgress > 0 ? c.AnimationProgress + .001f : 0))) return false;
        if (!Call(lease, () => api.SetExpression(view, c.Expression))) return false;
        if (!Call(lease, () => api.SetHeadDirection(view, c.HeadDirection.X, c.HeadDirection.Y))) return false;
        if (!Call(lease, () => api.SetEyeDirection(view, c.EyeDirection.X, c.EyeDirection.Y))) return false;
        if (!TryReadCharacter(lease, out var live) || live != actor) return false;
        ref var follow = ref ((Character*)actor)->LookAt.BannerCameraFollowFlag;
        const LookAtContainer.BannerCameraFollowFlags mask = LookAtContainer.BannerCameraFollowFlags.Head | LookAtContainer.BannerCameraFollowFlags.Eyes;
        follow = (follow & ~mask) | (c.HeadFacingCamera ? LookAtContainer.BannerCameraFollowFlags.Head : 0)
            | (c.EyesFacingCamera ? LookAtContainer.BannerCameraFollowFlags.Eyes : 0);
        if (!Call(lease, () => api.SetDirectionalColor(view,c.DirectionalRed,c.DirectionalGreen,c.DirectionalBlue))) return false;
        if (!Call(lease, () => api.SetDirectionalBrightness(view,c.DirectionalBrightness))) return false;
        if (!Call(lease, () => api.SetDirectionalAngle(view,c.DirectionalVerticalAngle,c.DirectionalHorizontalAngle))) return false;
        if (!Call(lease, () => api.SetAmbientColor(view,c.AmbientRed,c.AmbientGreen,c.AmbientBlue))) return false;
        if (!Call(lease, () => api.SetAmbientBrightness(view,c.AmbientBrightness))) return false;
        if (!Call(lease, () => api.SetBackground(view,c.Background))) return false;
        if (!Call(lease, () => api.SetCameraZoom(view,c.CameraZoom))) return false;
        current->CameraPosition = new(c.CameraPosition.X,c.CameraPosition.Y,c.CameraPosition.Z,c.CameraPosition.W);
        current->CameraTarget = new(c.CameraTarget.X,c.CameraTarget.Y,c.CameraTarget.Z,c.CameraTarget.W);
        current->CameraYaw=c.CameraYaw;current->CameraPitch=c.CameraPitch;current->CameraDistance=c.CameraDistance;
        current->CameraZoomNormalized=c.CameraZoomNormalized;current->ImageRotation=c.ImageRotation;
        if (!Call(lease, () => api.ApplyCamera(view))) return false;
        if (!Call(lease, () => api.SetPaused(view,c.Paused))) return false;
        return Call(lease, () => api.SetCharacterVisible(view,c.CharacterVisible));
    }
    private bool Fail(string message) { LastError = message; return false; }
    private bool Stop(IPortraitOwnedSceneLease lease,string message)
    {
        LastError = message;
        if (Owns(lease) && TryRetire(lease)) return false;
        return Quarantine(message);
    }
    private bool Quarantine(string message) { IsReady = false; IsQuarantined = true; LastError = message; return false; }
}
