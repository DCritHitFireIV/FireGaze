using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Reflection;

internal static unsafe class OwnedPassiveFeatureCases
{
    private static int checks;
    private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
    internal static int Run()
    {
        foreach (var revoke in new[] { "finalize", "mutation", "logout", "territory", "stop", "dispose" })
        {
            using var boundary = new CensusFeatureBoundary();
            var feature = boundary.Feature;
            var provider = new OwnedPhysicalCases.Provider();
            var controller = new PortraitOwnedScenePassiveDiagnostic(() => true, feature.CensusContext, feature.CensusController,
                () => new(new PassiveEnvironment(feature.CensusSnapshot!.Source!.Plan!.ModuleBase)),
                new DalamudPortraitOwnedSceneHookFactory(provider, new OwnedPhysicalCases.Proof()));
            feature.OwnedInitialize(controller);
            Check(feature.OwnedCommand("native-owned", "status") && provider.Created.Count == 0 && !feature.SourceCensusBlocksOperations,
                "actual Feature status is pure and never installs");
            feature.CensusCommand("native-census", "start"); boundary.Source(0); feature.CensusTick(); boundary.Source(1); feature.CensusTick();
            Check(feature.CensusController.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "actual old Feature census drained");
            Check(feature.OwnedCommand("native-owned", "start") && feature.SourceCensusBlocksOperations,
                "actual Feature accepts explicit handoff without invalidating historical source epoch");
            feature.OwnedTick();
            Check(provider.Created.Count == 39 && controller.Snapshot.State == PortraitOwnedScenePassiveDiagnosticState.WaitingForSourceReturn,
                "actual Feature installs bounded owner through its Update partial");
            var renderAddress = feature.CensusSnapshot!.Source!.Plan!.ModuleBase + 0x9CD4D0;
            var renderHook = provider.Created.Single(value => value.Address == renderAddress);
            var render = (CharaView.Delegates.Render)Delegate.CreateDelegate(typeof(CharaView.Delegates.Render), renderHook.Detour.Target, renderHook.Detour.Method);
            render((CharaView*)boundary.Memory.Source, 17);
            var nativeEventsProperty = typeof(PortraitOwnedScenePassiveDiagnostic).GetProperty("NativeEvents", BindingFlags.Instance | BindingFlags.NonPublic);
            var nativeEvents = nativeEventsProperty?.GetValue(controller);
            var eventItems = nativeEvents?.GetType().GetProperty("Events")?.GetValue(nativeEvents) as System.Collections.IEnumerable;
            var admission = nativeEvents?.GetType().GetProperty("ProductionAdmission")?.GetValue(nativeEvents);
            Check(nativeEvents is not null && eventItems?.Cast<object>().Count() == 1 && admission is false,
                "actual passive diagnostic exposes detached native consumer evidence after a typed source callback");
            var ownedJson = boundary.Feature.OwnedJSON();
            Check(ownedJson.Contains("\"nativeEvents\"") && ownedJson.Contains("\"target\":\"Render\"")
                && ownedJson.Contains("\"receiver\":") && ownedJson.Contains("\"arguments\":[17,0,0,0,0]"),
                "actual Feature diagnostic JSON serializes target, receiver and scalar native consumer arguments");
            var creates = boundary.Factory.Creates;
            try { feature.CensusCommand("native-census", "start"); } catch (InvalidOperationException) { }
            Check(boundary.Factory.Creates == creates, "active owned Feature blocks a new overlapping native census");
            switch (revoke)
            {
                case "finalize": feature.CensusFinalize(); break;
                case "mutation": feature.CensusMutation(); break;
                case "logout": feature.CensusLogout(); break;
                case "territory": feature.CensusTerritory(); break;
                case "stop": feature.OwnedCommand("native-owned", "stop"); break;
                case "dispose": feature.CensusDispose(); break;
            }
            Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && !feature.SourceCensusBlocksOperations
                && provider.Created.All(hook => hook.IsDisposed), "actual Feature " + revoke + " revokes and physically retires every owned hook");
            Check(feature.OwnedSafety is { Known: true, State: "Off", PhysicalDrainCompleted: true, Hooks: 0, InFlight: 0, ReservationHeld: false },
                "actual Feature independent owned safety reports actual physical retirement");
        }
        Console.WriteLine($"OwnedPassiveFeatureCases: {checks} actual command/lifecycle checks passed."); return 0;
    }
    private sealed class PassiveEnvironment(nint module) : IPortraitOwnedSceneHookEnvironment
    {
        public bool IsFrameworkThread => true;
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool TryValidateInstallation(out string reason) { reason = "No production scene admission."; return false; }
        public bool IsSourceCurrent(PortraitIdentity value) => false;
        public bool IsSlotIdle(uint slot) => false;
        public bool IsSlotOwnedBy(uint slot, nint view) => false;
        public bool TryReadViewSlot(nint view, out uint slot) { slot = uint.MaxValue; return false; }
    }
}
