using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace FireGaze.PortraitPreview;

internal unsafe delegate void PortraitNativeManagerUpdateCandidate(RenderManager* manager, float deltaTime);
internal unsafe delegate void PortraitNativeSceneCameraUpdate(SceneCameraManager* manager);

internal sealed unsafe class PortraitNativePhaseObservationWrappers
{
    private readonly PortraitNativePhaseObservation observation;

    public PortraitNativePhaseObservationWrappers(PortraitNativePhaseObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        this.observation = observation;
    }

    public RenderManager.Delegates.Render ManagerRender(RenderManager.Delegates.Render original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return manager => {
            var call = observation.Begin(PortraitNativeObservedPhase.ManagerRender, (nint)manager);
            var returned = false;
            try { original(manager); returned = true; }
            finally { observation.End(call, returned); }
        };
    }

    public PortraitNativeManagerUpdateCandidate ManagerUpdateCandidate(PortraitNativeManagerUpdateCandidate original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return (manager, deltaTime) => {
            var call = observation.Begin(PortraitNativeObservedPhase.ManagerUpdateCandidate, (nint)manager, floatArgument: deltaTime);
            var returned = false;
            try { original(manager, deltaTime); returned = true; }
            finally { observation.End(call, returned); }
        };
    }

    // Current audited build's complete 0x421D60 body consumes only RCX as input;
    // SDK has no generated Update delegate for this type. This is not a cross-build ABI claim.
    public PortraitNativeSceneCameraUpdate SceneCameraUpdate(PortraitNativeSceneCameraUpdate original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return manager => {
            var call = observation.Begin(PortraitNativeObservedPhase.SceneCameraUpdate, (nint)manager);
            var returned = false;
            try { original(manager); returned = true; }
            finally { observation.End(call, returned); }
        };
    }

    public CharaView.Delegates.Render SourceRender(CharaView.Delegates.Render original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return (view, frame) => {
            var call = observation.Begin(PortraitNativeObservedPhase.SourceCharaViewRender, (nint)view, frame);
            var returned = false;
            try { original(view, frame); returned = true; }
            finally { observation.End(call, returned); }
        };
    }
}
