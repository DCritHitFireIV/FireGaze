#if CENSUS_FEATURE
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal sealed unsafe class CensusFeatureBoundary : IDisposable
{
    internal readonly Fixture Memory = new();
    internal readonly SourceReturnCases.Factory Factory = new();
    internal readonly PortraitPreviewFeature Feature;
    internal CensusFeatureBoundary()
    {
        Memory.SetIdle(6);
        Feature = new(Memory.Phase.Source);
        var controller = new PortraitSourceCensusDiagnostic(() => true, Feature.CensusContext, Factory, Memory.Memory,
            timestamp: () => 0, timestampFrequency: 1000, thread: PortraitSourceCensusDiagnostic.CurrentThread,
            sourceResolve: SourceReturnCases.Resolve(PortraitNativePhaseTargetSet.PhaseObservation),
            counterResolve: SourceReturnCases.Resolve(PortraitNativePhaseTargetSet.CounterIngress),
            resourceResolve: SourceReturnCases.Resolve(PortraitNativePhaseTargetSet.ResourceIngress));
        Feature.CensusInitialize(controller);
    }
    internal void Source(uint frame) => Factory.Call(PortraitNativeObservedPhase.SourceCharaViewRender, (nint)Memory.Source, frame);
    public void Dispose() { Feature.CensusDispose(); Feature.CensusDrain.Wait(TimeSpan.FromSeconds(5)); Memory.Dispose(); }
}
namespace FireGaze
{
    internal static class Plugin
    {
        internal static readonly CensusFramework Framework = new();
        internal static readonly CensusAddonLifecycle AddonLifecycle = new();
        internal static readonly CensusClientState ClientState = new();
        internal static readonly CensusChat Chat = new();
        internal static IGameInteropProvider Interop => throw new InvalidOperationException("Official provider is supplied at the external test boundary.");
    }
    internal sealed class CensusFramework { internal bool IsInFrameworkUpdateThread => true; }
    internal sealed class CensusChat { internal void Print(string text) { } }
    internal sealed class CensusClientState
    {
        internal event IClientState.LogoutDelegate? Logout;
        internal event Action<uint>? TerritoryChanged;
        internal void RaiseLogout() => Logout?.Invoke(0, 0);
        internal void RaiseTerritory() => TerritoryChanged?.Invoke(0);
    }
    internal sealed class CensusAddonLifecycle
    {
        private readonly List<Action<AddonEvent, AddonArgs>> callbacks = [];
        internal int Listeners => callbacks.Count;
        internal void RegisterListener(AddonEvent type, string name, Action<AddonEvent, AddonArgs> action) => callbacks.Add(action);
        internal void UnregisterListener(AddonEvent type, string name, Action<AddonEvent, AddonArgs> action) => callbacks.Remove(action);
        internal void FinalizeEditor() { foreach (var callback in callbacks.ToArray()) callback(AddonEvent.PreFinalize, null!); }
    }
}
namespace FireGaze.PortraitPreview
{
    internal sealed unsafe partial class PortraitPreviewFeature
    {
        private readonly CensusTarget target;
        private readonly PortraitPreviewSession session;
        private readonly CensusCaptureIsolation captureIsolation = new();
        private readonly CensusIdle photos = new(), window = new(), lightDrag = new();
        private readonly CensusIdle? liveImageService = null, lightingFitCapture = null, nativeLiveController = null;
        private bool NativeLiveEnabled => false;
        private bool LightingFitMutationBlocked => false;
        internal PortraitPreviewFeature(PortraitIdentity source)
        {
            target = new(source); session = new(target); session.Open(source);
        }
        private static CensusOwner? CurrentCharacter() => new(1, 2);
        private static void Mutate(Action action, bool preserveSourceCensus = false) => action();
        private static void Error(string error) => throw new InvalidOperationException(error);
        internal PortraitSourceCensusContext? CensusContext() => ReadSourceCensusContext();
        internal void CensusInitialize(PortraitSourceCensusDiagnostic value) => InitializeSourceCensus(value);
        internal bool CensusCommand(params string[] parts) => TrySourceCensusCommand(parts);
        internal void CensusTick() => UpdateSourceCensus();
        internal void CensusFinalize() => Plugin.AddonLifecycle.FinalizeEditor();
        internal void CensusLogout() => Plugin.ClientState.RaiseLogout();
        internal void CensusTerritory() => Plugin.ClientState.RaiseTerritory();
        internal bool CensusSubscribed => Plugin.AddonLifecycle.Listeners > 0;
        internal void CensusMutation() => StopSourceCensusForMutation();
        internal void CensusDispose() => DisposeSourceCensus();
        internal Task CensusDrain => sourceCensusPhysicalDrain;
        internal bool CensusActive => sourceCensus?.IsActive == true;
        internal PortraitSourceCensusDiagnosticSnapshot? CensusSnapshot => sourceCensus?.Snapshot;
        internal PortraitSourceCensusDiagnostic CensusController => sourceCensus!;
        internal void OwnedInitialize(PortraitOwnedScenePassiveDiagnostic value) => InitializeOwnedPassive(value);
        internal bool OwnedCommand(params string[] parts) => TryOwnedPassiveCommand(parts);
        internal void OwnedTick() => UpdateOwnedPassive();
        internal PortraitOwnedScenePassiveSafetyState OwnedSafety => ReadOwnedPassiveSafetyState();
        internal string OwnedJSON() => System.Text.Json.JsonSerializer.Serialize(OwnedPassiveDiagnostics());
        internal string CensusJSON() => System.Text.Json.JsonSerializer.Serialize(SourceCensusDiagnostics());
    }
    internal sealed class CensusTarget(PortraitIdentity source) : IPortraitPreviewTarget
    {
        internal bool CanBlockSave => true;
        internal bool TryResolve(out PortraitIdentity identity, out object? bounds, out object? type)
        { identity = source; bounds = type = null; return true; }
        public bool IsCurrent(PortraitIdentity identity) => identity == source;
        public PortraitLook Capture(PortraitIdentity identity) => new(new PortraitPiece[13], true, true, false);
        public bool Apply(PortraitIdentity identity, PortraitLook look) => true;
    }
    internal sealed record CensusOwner(ulong AccountID, ulong ContentID);
    internal sealed class CensusCaptureIsolation { internal string Mode => "off"; }
    internal sealed class CensusIdle
    {
        internal bool IsPhysicallyIdle => true;
        internal bool IsBusy => false;
        internal bool IsActive => false;
        internal bool IsSourceCensusIdle => true;
    }
}
#endif
