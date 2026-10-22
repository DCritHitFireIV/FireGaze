using Dalamud.Interface.Textures.TextureWraps;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Holds the active scene, official RT copies and draw borrowers through one physical close.
/// Construction performs no native work. It is ready for Feature integration only after the
/// normal native environment can qualify production admission and the current active phase.
/// </summary>
internal sealed class PortraitOwnedSceneSession : IDisposable
{
    private readonly IPortraitOwnedSceneSessionBroker broker;
    private readonly PortraitOwnedSceneRenderTargetAdapter renderTargets;
    private Task? drain;
    private bool disposed;
    internal PortraitOwnedSceneController Controller { get; }
    internal PortraitOwnedSceneImageSource<IDalamudTextureWrap> Images => renderTargets.Images;
    internal bool ImageResourcesClean => renderTargets.IsPhysicallyClean;
    internal string? LastError => Controller.LastError ?? broker.LastError;

    internal PortraitOwnedSceneSession(Func<bool> framework,
        Func<IPortraitOwnedSceneHookEnvironment> createEnvironment, IPortraitOwnedSceneHookFactory factory,
        Func<IPortraitOwnedSceneRuntime> createRuntime, PortraitOwnedSceneRenderTargetAdapter renderTargets,
        PortraitNativePhaseHostReservations? reservations = null)
        : this((after, revoked) => new PortraitOwnedSceneControllerBroker(framework, createEnvironment,
            factory, reservations, after, revoked), createRuntime, renderTargets) { }

    internal PortraitOwnedSceneSession(
        Func<Action<IPortraitOwnedSceneLease>, Action<string>, IPortraitOwnedSceneSessionBroker> createBroker,
        Func<IPortraitOwnedSceneRuntime> createRuntime, PortraitOwnedSceneRenderTargetAdapter renderTargets)
    {
        this.renderTargets = renderTargets ?? throw new ArgumentNullException(nameof(renderTargets));
        broker = createBroker(CopyReady, RevokeSource) ?? throw new InvalidOperationException("Owned session broker factory returned null.");
        Controller = new(broker, createRuntime);
    }
    private void CopyReady(IPortraitOwnedSceneLease lease) => renderTargets.TryCopy(Controller, lease);
    private void RevokeSource(string reason)
    {
        renderTargets.RevokeResourceEpoch();
        Controller.RevokeNativeSource(reason);
    }
    internal bool TryStart(PortraitOwnedCharaViewSnapshot source, uint slot)
    {
        if (disposed) return false;
        broker.Update(); return Controller.TryStart(source, slot);
    }
    internal bool TrySetObserverCamera(PortraitOwnedSceneObserverCameraSnapshot camera)
    {
        if (disposed || !Controller.TrySetObserverCamera(camera)) return false;
        renderTargets.RevokeResourceEpoch();
        
        return true;
    }
    internal void Stop(string reason = "Owned scene stopped.")
    {
        renderTargets.RevokeResourceEpoch(); // Revoke new/late images before any native retirement.
        Controller.Stop(reason); broker.Update();
    }
    // The same active resource ingress must revoke this before its native Original.
    // This method performs managed invalidation only and grants no active/native permission.
    internal void RevokeResourceEpoch() => renderTargets.RevokeResourceEpoch();
    internal void Update() { Controller.Update(); broker.Update(); }
    public void Dispose()
    {
        if (disposed) { Update(); return; }
        disposed = true;
        renderTargets.RevokeResourceEpoch();
        Controller.Dispose();
        renderTargets.Dispose();
        broker.Dispose();
        // Scene retirement uses a future qualified native phase. Neither provider tasks
        // nor retained draw wraps are cancelled or forgotten when their scene closes.
        drain = PortraitCaptureRetirement.CombineOwned(this, broker.DrainPhysicalAsync(), renderTargets.DrainPhysicalAsync());
    }
    internal Task DrainPhysicalAsync() { Dispose(); return drain!; }
}
