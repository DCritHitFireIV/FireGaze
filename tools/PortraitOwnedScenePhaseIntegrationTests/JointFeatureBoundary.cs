#if JOINT_FEATURE && JOINT_DIAGNOSTIC
using FireGaze.PortraitPreview;

internal sealed unsafe class JointFeatureBoundary : IDisposable
{
    internal readonly JointDiagnosticBoundary Joint = new();
    internal readonly SourceReturnCases.Factory OldFactory = new();
    internal readonly PortraitPreviewFeature Feature;
    internal JointFeatureBoundary()
    {
        Feature = new(Joint.Memory.Phase.Source);
        var census = new PortraitSourceCensusDiagnostic(() => true, Feature.CensusContext, OldFactory, Joint.Memory.Memory,
            timestamp: () => 0, timestampFrequency: 1000, thread: PortraitSourceCensusDiagnostic.CurrentThread,
            sourceResolve: SourceReturnCases.Resolve(PortraitNativePhaseTargetSet.PhaseObservation),
            counterResolve: SourceReturnCases.Resolve(PortraitNativePhaseTargetSet.CounterIngress),
            resourceResolve: SourceReturnCases.Resolve(PortraitNativePhaseTargetSet.ResourceIngress));
        Feature.CensusInitialize(census);
        Joint.ReadContext = Feature.CensusContext;
        Feature.JointInitialize(Joint.Controller);
    }
    internal void OldSource(uint frame) => OldFactory.Call(PortraitNativeObservedPhase.SourceCharaViewRender,
        (nint)Joint.Memory.Source, frame);
    internal PortraitOwnedScenePassiveDiagnostic InitializeOwned()
    {
        var controller = new PortraitOwnedScenePassiveDiagnostic(() => true, Feature.CensusContext, Feature.CensusController,
            () => new(new PassiveEnvironment(Joint.Plan.ModuleBase)),
            new DalamudPortraitOwnedSceneHookFactory(new OwnedPhysicalCases.Provider(), new OwnedPhysicalCases.Proof()));
        Feature.OwnedInitialize(controller);
        return controller;
    }
    public void Dispose()
    {
        Feature.CensusDispose();
        Feature.CensusDrain.Wait(TimeSpan.FromSeconds(5));
        Joint.Dispose();
    }
    private sealed class PassiveEnvironment(nint module) : IPortraitOwnedSceneHookEnvironment
    {
        public bool IsFrameworkThread => true;
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool TryValidateInstallation(out string reason) { reason = "production admission denied"; return false; }
        public bool IsSourceCurrent(PortraitIdentity source) => false;
        public bool IsSlotIdle(uint slot) => false;
        public bool IsSlotOwnedBy(uint slot, nint view) => false;
        public bool TryReadViewSlot(nint view, out uint slot) { slot = uint.MaxValue; return false; }
    }
}

namespace FireGaze.PortraitPreview
{
    internal sealed unsafe partial class PortraitPreviewFeature
    {
        internal void JointInitialize(PortraitOwnedSceneJointDiagnostic value) => InitializeOwnedJoint(value);
        internal bool JointCommand(params string[] parts) => TryOwnedJointCommand(parts);
        internal PortraitOwnedSceneJointSafetyState JointSafety => ReadOwnedJointSafetyState();
        internal string JointJSON() => System.Text.Json.JsonSerializer.Serialize(OwnedJointDiagnostics());
    }
}
#endif
