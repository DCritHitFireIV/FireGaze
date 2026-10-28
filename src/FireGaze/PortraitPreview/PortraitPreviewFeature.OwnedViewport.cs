using System.Numerics;
using System.Text.Json;
using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitOwnedSceneSession? ownedViewport;
    private PortraitOwnedSceneExperimentBroker? ownedViewportBroker;
    private PortraitOwnedSceneRenderTargetComReader? ownedViewportCom;
    private PortraitSourceCensusContext? ownedViewportContext;
    private PortraitOwnedSceneObserverCameraSnapshot? ownedObserver, ownedSourceObserver;
    private bool ownedViewportClosing, ownedViewportViewRequested;
    private Task ownedViewportDrain = Task.CompletedTask;
    private long ownedViewportAttempt;
    private PortraitOwnedSceneFirstFrameProof? ownedViewportProof;
    public bool OwnedViewportVisible => ownedViewportViewRequested;
    public bool OwnedViewportExperimentActive => OwnedViewportBlocksOperations;
    private bool OwnedViewportBlocksOperations => ownedViewport is not null
        && (!ownedViewportClosing || !ownedViewportDrain.IsCompletedSuccessfully);
    public string OwnedViewportStatus => ownedViewport is null ? "独立人物预览未启动。"
        : ownedViewportClosing ? ownedViewportDrain.IsCompletedSuccessfully ? "实验已结束，人物与图像资源已退出。" : "正在退出独立人物预览…"
        : ownedViewport.Controller.State == PortraitOwnedSceneControllerState.Ready ? "独立人物预览（实验，2 秒）"
        : ownedViewport.LastError ?? "正在加载独立人物…";
    public PortraitOwnedSceneObserverCameraSnapshot? OwnedObserver => ownedObserver;
    public PortraitOwnedSceneTextureLifetime<IDalamudTextureWrap>.Borrow? TryBorrowOwnedViewport()
        => !ownedViewportClosing && ownedViewport?.Controller.State == PortraitOwnedSceneControllerState.Ready
            ? ownedViewport.Images.TryBorrow() : null;

    private bool TryOwnedViewportCommand(string[] parts)
    {
        if (parts.Length == 0 || parts[0] != "native-scene") return false;
        // The independent scene remains wired for later development, but is not
        // a release entry point. Keep the command consumed and silent for users.
        if (!pluginInterface.IsDev) return true;
        if (parts.Length is 3 or 4 && parts[1] == "start" && uint.TryParse(parts[2], out var slot) && slot < 8
            && (parts.Length==3 || parts[3] is "source" or "front" or "side" or "back"))
            Mutate(() => StartOwnedViewportExperiment(slot,parts.Length==4?parts[3]:"source"), preserveSourceCensus: true);
        else if (parts is ["native-scene", "stop"])
            Mutate(() => StopOwnedViewport("Stopped explicitly by the user."), preserveSourceCensus: true);
        else if (parts is ["native-scene", "status"])
            Plugin.Chat.Print("[独立人物实验] " + OwnedViewportStatus);
        else Error("用法：/firegaze portrait native-scene start <0-7> [source|front|side|back]|stop|status");
        return true;
    }
    private void StartOwnedViewportExperiment(uint slot,string preset)
    {
        if (OwnedViewportBlocksOperations || SourceCensusBlocksOperations
            || ReadSourceCensusContext() is not { IsValid: true } context
            || target.CaptureOwnedSceneSource(context.Source) is not { } snapshot)
        { Error("请先打开受保护的肖像预览，并等待其他图像操作完全结束。"); return; }
        var module = CurrentModuleBase();
        if (!PortraitOwnedSceneJointPlan.TryCreate(module, out var plan, out var reason) || plan is null
            || !PortraitOwnedSceneHooks.TryValidateCurrentExperiment(out reason))
        { Error("独立人物实验未启动：" + reason); return; }
        ownedObserver = ownedSourceObserver = ObserverFrom(snapshot.Composition);
        if (preset!="source" && ownedObserver is null)
        { Error("源相机超出当前独立观察范围，无法使用观察预设。");return; }
        var attempt=checked(++ownedViewportAttempt);
        ownedViewportContext = context; ownedViewportClosing = false; ownedViewportViewRequested = true;
        PortraitOwnedSceneJointHost? host = null;
        PortraitOwnedSceneSlotNativePhaseSource? phases = null;
        PortraitOwnedSceneRenderTargetAdapter? adapter = null;
        PortraitOwnedSceneExperimentBroker? broker = null;
        var native = new PortraitOwnedSceneSlotNativeApi(() => phases?.IsReadScopeActive == true);
        phases = new(native, () => Plugin.Framework.IsInFrameworkUpdateThread,
            () => host?.IsActiveDispatchCurrent == true, () => context.SessionEpoch);
        var slots = new PortraitOwnedSceneSlotReader(native, phases);
        bool ActiveWork() => broker?.CanCopy == true;
        ownedViewportCom = PortraitOwnedSceneRenderTargetComNativeApi.ForUI(native, pluginInterface.UiBuilder, ActiveWork);
        var read = new PortraitOwnedSceneRenderTargetNativeApi(native, phases, slots, ActiveWork, ownedViewportCom);
        adapter = new(read, new PortraitOwnedSceneRenderTargetTextures(Plugin.Textures), ActiveWork);
        ownedViewport = new((after, revoked) =>
        {
            host = new(new PortraitOwnedSceneHookEnvironment(native, phases,
                    () => Plugin.Framework.IsInFrameworkUpdateThread, module, PortraitOwnedSceneHooks.TryValidateCurrentExperiment),
                new DalamudPortraitOwnedSceneJointHookFactory(Plugin.Interop), new(), phases.BeginCounterMutation,
                phases.BeginResourceMutation,
                () => broker?.RequestIdentityCurrent == true
                    && Interlocked.Read(ref sourceCensusSessionEpoch) == context.SessionEpoch,
                mutationReceiver: _ => adapter.RevokeResourceEpoch(), sourceRevoked: revoked);
            broker = new(() => Plugin.Framework.IsInFrameworkUpdateThread, host, plan, context.Source,
                context.SessionEpoch, attempt, PortraitSourceCensusDiagnostic.CurrentThread(), TimeSpan.FromSeconds(2),
                () => { var accepted = PortraitOwnedSceneHooks.TryValidateCurrentExperiment(out var failure); return (accepted, failure); },
                after, revoked, StopOwnedViewport,
                () => adapter.Images.InFlight == 0 && !adapter.Images.HasCurrentFrame);
            return ownedViewportBroker = broker;
        }, () => new PortraitOwnedCharaView(new PortraitOwnedCharaViewNativeApi(
            () => Plugin.Objects.LocalPlayer is { } local ? local.Address : 0)), adapter, lease =>
        {
            if (host is null || ownedViewport?.Controller.ImageOwner is not { } owner) return null;
            var state = host.Snapshot(); var identity = owner.Source;
            return new(state.Installation, state.SessionEpoch, state.RequestEpoch, lease.NativeFrame,
                new((long)identity.Editor, (long)identity.State, (long)identity.Model, identity.Generation, identity.Character),
                new(owner.Session, owner.Revision, owner.Slot, owner.LeaseEpoch));
        });
        ownedViewportProof=new(ownedViewport.Images,
            texture=>PortraitOwnedSceneFirstFrameProof.ReadOfficialAsync(Plugin.TextureReadback,texture),
            ()=>!Volatile.Read(ref ownedViewportClosing)&&broker?.Snapshot.Recording==true);
        if (!ownedViewport.TryStart(snapshot, slot))
        { reason = ownedViewport.LastError ?? "The bounded joint request was rejected."; StopOwnedViewport(reason); Error("独立人物实验未启动：" + reason); return; }
        if(preset!="source" && ownedObserver is { } initial)
        {
            var yaw=preset=="front"?0:preset=="side"?MathF.PI/2:MathF.PI;
            var offset=new Vector3(MathF.Sin(yaw)*MathF.Cos(initial.Pitch),-MathF.Sin(initial.Pitch),MathF.Cos(yaw)*MathF.Cos(initial.Pitch))*initial.Distance;
            var requested=initial with {Position=initial.Target+offset,Yaw=yaw};
            if(ownedViewport.TrySetObserverCamera(requested))ownedObserver=requested;
        }
        window.OpenLightingSpace();
        Plugin.Chat.Print("[独立人物实验] 已排队，等待真实渲染回调；实验将在 2 秒内停止。");
    }
    private static PortraitOwnedSceneObserverCameraSnapshot? ObserverFrom(PortraitComposition value)
    {
        return PortraitOwnedSceneObserverCameraSnapshot.TryFromSource(
            new(value.CameraPosition.X,value.CameraPosition.Y,value.CameraPosition.Z),
            new(value.CameraTarget.X,value.CameraTarget.Y,value.CameraTarget.Z),out var camera)?camera:null;
    }
    private void UpdateOwnedViewport()
    {
        if (ownedViewport is null) return;
        if (!ownedViewportClosing && ReadSourceCensusContext() != ownedViewportContext)
        {
            ownedViewportBroker?.Revoke("The source, owner or protected editor session changed.");
            StopOwnedViewport("The independent portrait source changed.");
        }
        ownedViewport.Update();
    }

}
