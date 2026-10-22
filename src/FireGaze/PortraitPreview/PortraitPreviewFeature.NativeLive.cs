using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace FireGaze.PortraitPreview;

/// <summary>Acquire-only view of the editor target; starts once per preview lease.</summary>
internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitNativeLiveNodeSource? nativeLiveSource;
    private PortraitNativeLiveController? nativeLiveController;
    private bool nativeLiveRequested;
    private Task nativeLivePhysicalDrain = Task.CompletedTask;

    public bool NativeLiveEnabled => nativeLiveRequested && nativeLiveController?.IsEnabled == true;

    private void InitializeNativeLive()
    {
        nativeLiveSource = new(Plugin.Framework, Plugin.GameGUI, Plugin.Objects, Plugin.AddonLifecycle, ReadNativeLiveEditor);
        nativeLiveController = new(nativeLiveSource, ReadNativeLiveContext);
    }

    private PortraitIdentity? ReadNativeLiveEditor()
        => Plugin.Framework.IsInFrameworkUpdateThread && session.IsActive
            && target.TryResolve(out var identity, out _, out _) && session.BlockSave(identity) ? identity : null;

    private PortraitNativeLiveContext ReadNativeLiveContext()
    {
        var editor = ReadNativeLiveEditor() ?? default;
        var player = Plugin.Objects.LocalPlayer;
        var character = player is null ? null : (Character*)player.Address;
        var owner = character is null ? default : new PortraitCaptureOwner(character->AccountId, character->ContentId);
        var CapturesDrained = !photos.IsBusy && liveImageService?.IsBusy != true
            && lightingFitCapture?.IsBusy != true;
        return new(editor, owner, editor.IsValid && session.BlockSave(editor), target.CanBlockSave,
            captureIsolation.Mode, CapturesDrained, LightingFitMutationBlocked);
    }

    public void SetNativeLiveEnabled(bool enabled) => Mutate(() =>
    {
        if (!enabled)
        {
            StopNativeLive(PortraitNativeLiveStopReason.Disabled);
            return;
        }
        if (SourceCensusBlocksOperations || !session.IsActive || !target.TryResolve(out var identity, out _, out _)
            || nativeLiveController?.TryEnable(identity) != true)
        {
            nativeLiveRequested = false;
            Error("铭牌画面尚未准备好，请稍后重试。");
            return;
        }
        nativeLiveRequested = true;
    });

    public PortraitNativeLiveBorrow? TryBorrowNativeLive()
        => NativeLiveEnabled && nativeLiveController!.TryBorrow(out var borrow) ? borrow : null;

    private void UpdateNativeLive()
    {
        if (!session.IsActive)
        {
            if (nativeLiveRequested || nativeLiveController?.IsEnabled == true)
                StopNativeLive(PortraitNativeLiveStopReason.UserClosed);
            return;
        }
        if (target.TryResolve(out var identity, out _, out _) && !SourceCensusBlocksOperations
            && nativeLiveController?.TryStartSessionOnce(session.Revision, identity) == true)
            nativeLiveRequested = true;
        if (!nativeLiveRequested) return;
        nativeLiveController?.Update();
        if (nativeLiveController?.IsEnabled != true) nativeLiveRequested = false;
    }

    private void StopNativeLive(PortraitNativeLiveStopReason reason)
    {
        nativeLiveRequested = false;
        nativeLiveController?.Stop(reason);
    }

    private void DisposeNativeLive()
    {
        nativeLiveRequested = false;
        nativeLiveController?.Dispose();
        nativeLivePhysicalDrain = nativeLiveController?.DrainPhysicalAsync() ?? Task.CompletedTask;
        nativeLiveController = null;
        nativeLiveSource?.Dispose();
        nativeLiveSource = null;
    }
}
