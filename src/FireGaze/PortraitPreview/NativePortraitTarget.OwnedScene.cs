using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class NativePortraitTarget
{
    /// <summary>
    /// Captures complete typed native values for a future independently owned view. Export is
    /// a native call and can reenter other plugins, so resolve again before copying any native
    /// payload. The returned snapshot never borrows editor/model/item memory.
    /// </summary>
    public PortraitOwnedCharaViewSnapshot? CaptureOwnedSceneSource(PortraitIdentity identity)
    {
        if (!TryResolve(out var current, out _, out _) || current != identity) return null;
        var portable = CaptureEditorState(identity);
        if (portable is null || !IsCurrent(identity)) return null;
        var view = (CharaViewPortrait*)identity.Model;
        if (view->IsAnimationPauseStatePending) return null;
        if (!PortraitOwnedCharaViewSnapshot.TryCreate(identity, view->PortraitCharacterData,
                view->ModelData, view->Items, view->FreeCompanyCrestBitfield, portable.Composition,
                out var result, gameBuild: SnapshotGameBuild)) return null;
        return IsCurrent(identity) && !view->IsAnimationPauseStatePending ? result : null;
    }
}
