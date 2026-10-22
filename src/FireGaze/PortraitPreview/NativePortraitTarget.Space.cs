using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal unsafe interface IPortraitSpaceNativeApi
{
    void SyncSpaceControls(AgentBannerEditorState* state, bool directionChanged, short vertical, short horizontal);
}

internal sealed unsafe partial class NativePortraitTarget
{
    /// <summary>Compare the complete detached source, then write only orbit-camera and main-light direction.</summary>
    public bool SetLightingSpace(PortraitIdentity identity, PortraitEditorSnapshot expected, PortraitComposition desired)
    {
        if (!api.IsFrameworkThread || api is not IPortraitEditorNativeApi native
            || CaptureEditorState(identity) is not { } current
            || desired is null
            || !PortraitLightingSpaceBinding.MatchesSnapshot(expected, current)) return false;

        // Ignore non-spatial payload fields and validate the merged candidate. This keeps the
        // native boundary narrow even when a caller carries unrelated stale UI values.
        var mergedCandidate = PortraitLightingSpaceBinding.MergeInto(current.Composition, desired);
        if (!PortraitLightingSpace.TryCreateNativeCandidate(current.Composition, mergedCandidate, out var nativeCandidate)) return false;

        var before = current.Composition;
        var merged = PortraitLightingSpaceBinding.MergeInto(before, nativeCandidate);
        var cameraChanged = before.CameraPosition != merged.CameraPosition || before.CameraYaw != merged.CameraYaw
            || before.CameraPitch != merged.CameraPitch || before.CameraDistance != merged.CameraDistance;
        var directionChanged = before.DirectionalVerticalAngle != merged.DirectionalVerticalAngle
            || before.DirectionalHorizontalAngle != merged.DirectionalHorizontalAngle;
        if (!cameraChanged && !directionChanged) return true;
        try
        {
            if (!IsCurrent(identity)) return false;
            var view = (CharaViewPortrait*)identity.Model;
            var accepted = current;
            if (cameraChanged)
            {
                view->CameraPosition = new(merged.CameraPosition.X, merged.CameraPosition.Y, merged.CameraPosition.Z, merged.CameraPosition.W);
                view->CameraYaw = merged.CameraYaw;
                view->CameraPitch = merged.CameraPitch;
                view->CameraDistance = merged.CameraDistance;
                native.ApplyCamera(view);
                accepted = accepted with { Composition = accepted.Composition with
                {
                    CameraPosition = merged.CameraPosition, CameraYaw = merged.CameraYaw,
                    CameraPitch = merged.CameraPitch, CameraDistance = merged.CameraDistance,
                } };
                if (CaptureEditorState(identity) is not { } afterCamera
                    || !PortraitLightingSpaceBinding.MatchesSnapshot(accepted, afterCamera)) return false;
                accepted = afterCamera;
            }
            if (directionChanged)
            {
                if (!IsCurrent(identity)) return false;
                native.SetDirectionalAngle(view, merged.DirectionalVerticalAngle, merged.DirectionalHorizontalAngle);
                accepted = accepted with { Composition = accepted.Composition with
                {
                    DirectionalVerticalAngle = merged.DirectionalVerticalAngle,
                    DirectionalHorizontalAngle = merged.DirectionalHorizontalAngle,
                } };
                if (CaptureEditorState(identity) is not { } afterDirection
                    || !PortraitLightingSpaceBinding.MatchesSnapshot(accepted, afterDirection)) return false;
                accepted = afterDirection;
            }
            if (!IsCurrent(identity)) return false;
            // BannerEntry is an inline working copy; persistent BannerModule records are untouched.
            var state = (AgentBannerEditorState*)identity.State;
            if (cameraChanged)
            {
                state->BannerEntry.CameraPosition = new((Half)merged.CameraPosition.X,
                    (Half)merged.CameraPosition.Y, (Half)merged.CameraPosition.Z, (Half)merged.CameraPosition.W);
                state->BannerEntry.CameraTarget = new((Half)merged.CameraTarget.X,
                    (Half)merged.CameraTarget.Y, (Half)merged.CameraTarget.Z, (Half)merged.CameraTarget.W);
            }
            if (directionChanged)
            {
                state->BannerEntry.DirectionalLightingVerticalAngle = merged.DirectionalVerticalAngle;
                state->BannerEntry.DirectionalLightingHorizontalAngle = merged.DirectionalHorizontalAngle;
            }
            // Do not call Atk slider setters from inside a spatial native transaction. Those
            // callbacks can synchronously rebuild BannerEditor while the camera/light write is
            // still active; the prior AppHang has no stack proving its cause. The detached export is refreshed on
            // the next framework sample; the spatial write itself remains the only native mutation.
            return IsCurrent(identity);
        }
        catch (Exception exception)
        {
            // Native callbacks may already have changed or replaced the editor. Do not replay
            // an old full snapshot or continue writing the next owned field after a failure.
            LastError = "空间灯光暂时无法调整：" + exception.Message;
            return false;
        }
    }

    private sealed partial class LiveApi : IPortraitSpaceNativeApi
    {
        public void SyncSpaceControls(AgentBannerEditorState* state, bool directionChanged, short vertical, short horizontal)
        {
            // Retained as an explicit no-op interface for older fixtures. Slider updates are
            // deliberately deferred until a later ordinary editor sample.
        }
    }
}
