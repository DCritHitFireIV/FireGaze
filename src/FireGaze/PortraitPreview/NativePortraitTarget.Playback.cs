using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class NativePortraitTarget
{
    /// <summary>Changes playback only; never restores an earlier composition or seeks its timeline.</summary>
    public bool SetAnimationPaused(PortraitIdentity identity, bool paused)
    {
        try
        {
            if (api is not IPortraitEditorNativeApi native || CaptureEditorState(identity) is not { } before
                || !IsCurrent(identity)) return false;
            if (before.Composition.Paused == paused) return true;
            var view = (CharaViewPortrait*)identity.Model;
            native.SetPaused(view, paused);
            if (!IsCurrent(identity)) return false;
            // Native playback may queue a pause transition. A pending accepted operation is not
            // a failed state restore; history will sample its settled result on a later frame.
            if (view->IsAnimationPauseStatePending) return true;
            return native.IsPaused(view) == paused && IsCurrent(identity);
        }
        catch (Exception)
        {
            LastError = "肖像动作暂时无法播放或暂停，请稍后重试。";
            return false;
        }
    }
}
