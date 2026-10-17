using FireGaze.LocalPreview.Interaction;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    private TrialSpecialHotbarModel? specialHotbarModel;
    private TrialSpecialHotbarView? specialHotbarView;
    private GposeEmoteService? gposeEmotes;
    private long presentationFrame;

    private void InitializePresentationExtras()
    {
        if (runtime is null) return;
        specialHotbarModel = new(new TrialSpecialHotbarCatalog(Data.Excel));
        if (textures is not null && keys is not null)
        {
            specialHotbarView = new(textures,keys,GameGUI,Log,QueueSpecialInput,Framework,AddonLifecycle,Interop);
            pluginInterface.UiBuilder.Draw += DrawSpecialHotbar;
        }
        runtime.SpecialActionInput = RouteNativeSpecialAction;
        gposeEmotes = new(Framework,ClientState,Objects,Data,Interop,Log,
            () => { runtime?.ClearAll(false); PublishSpecialBar(); },
            () => !disposed && enabled,
            address => runtime?.OwnsDisplayActor(address) == true,
            activePreviewEmote: () => runtime?.GetActivePreviewEmote());
        runtime.ExternalEmoteAccepted += gposeEmotes.RecordNative;
        runtime.PreviewEmoteStarted += gposeEmotes.RecordPreview;
    }

    private void UpdatePresentationExtras()
    {
        specialHotbarModel?.BeginFrame(++presentationFrame);
        PublishSpecialBar();
        specialHotbarView?.PollKeyboard(!disposed && enabled && coexistence.Allowed && ClientState.IsLoggedIn);
    }

    private void PublishSpecialBar()
    {
        if (specialHotbarModel is null) return;
        PreviewEffect? chosen=null;
        if (!disposed && enabled && runtime is { } current)
            foreach(var effect in current.ActiveEffects)
                if (effect.Slot is PreviewSlot.Mount or PreviewSlot.Accessory) { chosen=effect; break; }
        if (chosen is { } selected && runtime is { } actorRuntime)
            specialHotbarModel.SetPreview(selected.Slot==PreviewSlot.Mount,selected.Id,actorRuntime.Revision,actorRuntime.FlightPoseActive);
        else specialHotbarModel.Clear();
        specialHotbarView?.Publish(specialHotbarModel.Current);
    }

    private void DrawSpecialHotbar()
    {
        if (!disposed) specialHotbarView?.Draw();
    }

    private bool ActivateSpecialSlot(int slot,long revision)
    {
        if (disposed || !enabled || !coexistence.Allowed || runtime is null || specialHotbarModel is null) return false;
        PublishSpecialBar();
        return specialHotbarModel.ActivateSlot(slot,revision,runtime.TryPlaySpecialAction,ExitSpecialBar,ToggleFlightPose);
    }

    private bool ToggleFlightPose()
    {
        if (runtime?.TryToggleFlightPose() != true)
        {
            lastMessage = "当前无法切换此坐骑的飞行姿态，请稍后重试或选择其他坐骑。";
            Chat.PrintError("[本地试用] " + lastMessage);
            return false;
        }
        lastMessage = runtime.FlightPoseActive ? "已切换为本地飞行姿态" : "已恢复本地地面姿态";
        PublishSpecialBar();
        return true;
    }

    private bool QueueSpecialInput(int slot,long revision)
    {
        if (disposed) return false;
        if (Framework.IsInFrameworkUpdateThread) return ActivateSpecialSlot(slot,revision);
        _ = Framework.RunOnFrameworkThread(() =>
        {
            try { return ActivateSpecialSlot(slot,revision); }
            catch (Exception ex) { Log.Warning(ex,"[LocalPreview] 特殊栏输入未能执行"); return false; }
        });
        return true;
    }

    private bool RouteNativeSpecialAction(uint action)
    {
        if (disposed || !enabled || runtime is null || specialHotbarModel is null) return false;
        PublishSpecialBar();
        var source=runtime.GenuineCollections;
        if (specialHotbarModel.Current is not { } current
            || !specialHotbarModel.TryMapNativeAction(action,source.Mount,source.Accessory,out var slot)) return false;
        return ActivateSpecialSlot(slot,current.Revision);
    }

    private unsafe void ExitSpecialBar()
    {
        if (runtime is null || !Framework.IsInFrameworkUpdateThread) return;
        NativeCollectionExitSnapshot? Observe()
        {
            if (disposed || !enabled || !coexistence.Allowed || !ClientState.IsLoggedIn
                || Objects.LocalPlayer is not { } local) return null;
            var actor=(FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)local.Address;
            return NativeCollectionExit.Capture(actor,ClientState.TerritoryType,(nint)ActionManager.Instance());
        }
        var source=Observe();
        runtime.ClearAll(false);
        PublishSpecialBar();
        if (source is not { } entry) return;
        // The native client owns genuine removal, including its server validation.
        // A newly observed native state always defeats this short-lived exit intent.
        NativeCollectionExit.Execute(entry,Observe,
            () => ((ActionManager*)entry.Manager)->UseAction(ActionType.GeneralAction,23),
            id => ((ActionManager*)entry.Manager)->UseAction(ActionType.Ornament,id));
    }

    private void DisposePresentationExtras()
    {
        Cleanup(() => pluginInterface.UiBuilder.Draw-=DrawSpecialHotbar,"特殊栏绘制");
        if (runtime is not null)
        {
            runtime.SpecialActionInput=null;
            if (gposeEmotes is not null)
            {
                runtime.ExternalEmoteAccepted-=gposeEmotes.RecordNative;
                runtime.PreviewEmoteStarted-=gposeEmotes.RecordPreview;
            }
        }
        DisposeComponent(gposeEmotes,"拍照动作记录");
        DisposeComponent(specialHotbarView,"试用特殊栏");
        specialHotbarModel?.Clear();
        gposeEmotes=null;specialHotbarView=null;specialHotbarModel=null;
    }
}
