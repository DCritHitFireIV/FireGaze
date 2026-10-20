#if !JOINT_FEATURE || !JOINT_DIAGNOSTIC
internal static class JointFeatureCases
{
    internal static int Run()
    {
        Console.Error.WriteLine("FAIL: the production Feature joint diagnostic command/lifecycle partial is not connected.");
        return 1;
    }
}
#else
using FireGaze.PortraitPreview;

internal static class JointFeatureCases
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException("FAIL joint Feature " + message); }
    internal static int Run()
    {
        using (var b = new JointFeatureBoundary())
        {
            Check(b.Feature.JointCommand("native-joint", "status") && b.Joint.Provider.Created.Count == 0
                && b.Joint.Memory.Memory.Reads == 0, "status installs no hooks and performs no slot reads");
            b.Feature.CensusCommand("native-census", "start");
            var blocked = false;
            try { b.Feature.JointCommand("native-joint", "start"); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked && b.Joint.Provider.Created.Count == 0, "active old census blocks joint start before native Create");
            b.Feature.CensusCommand("native-census", "stop");
            Check(b.Feature.CensusController.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "old census drains before next attempt");
            Check(b.Feature.JointCommand("native-joint", "start") && b.Joint.Provider.Created.Count == 46
                && b.Feature.SourceCensusBlocksOperations, "actual command installs one passive joint batch and blocks editing");
            Check(b.Feature.JointSafety is { Known: true, BlocksOperations: true, Hooks: 46, ReservationHeld: true,
                PhysicalDrainCompleted: false, ProductionAdmission: false }, "detached safety exposes actual joint physical ownership");
            var oldCreates = b.OldFactory.Creates;
            blocked = false;
            try { b.Feature.CensusCommand("native-census", "start"); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked && b.OldFactory.Creates == oldCreates, "joint owner blocks an overlapping old census before Create");
            var reads = b.Joint.Memory.Memory.Reads;
            b.Feature.JointCommand("native-joint", "status");
            Check(b.Joint.Memory.Memory.Reads == reads && b.Joint.Provider.Created.Count == 46,
                "active status projects detached state without sampling native slots");
            b.Joint.Source(0);
            var json = b.Feature.JointJSON();
            Check(json.Contains("\"productionAdmission\":false", StringComparison.Ordinal)
                && json.Contains("\"rows\":[", StringComparison.Ordinal)
                && b.Joint.Controller.Snapshot.Census is { Slots: { Complete: true, Rows.Count: 8 } },
                "actual Feature JSON exposes eight historical same-installation rows with admission denied");
            b.Feature.CensusTick();
            Check(b.Joint.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5))
                && !b.Feature.SourceCensusBlocksOperations, "actual Feature Update retires a completed joint observation");
        }
        using (var b = new JointFeatureBoundary())
        {
            b.Feature.CensusCommand("native-census", "start"); b.OldSource(0); b.Feature.CensusTick();
            b.OldSource(1); b.Feature.CensusTick();
            Check(b.Feature.CensusController.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5))
                && b.Feature.CensusSnapshot?.Result is { Complete: true, Rows.Count: 8 },
                "completed old census supplies the historical prerequisite for the old owned command");
            var owned = b.InitializeOwned();
            b.Feature.JointCommand("native-joint", "start");
            var blocked = false;
            try { b.Feature.OwnedCommand("native-owned", "start"); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked && !owned.BlocksOperations, "joint owner blocks old owned handoff before it accepts a request");
        }
        foreach (var revoke in new[] { "finalize", "mutation", "logout", "territory", "stop", "dispose" })
        {
            using var b = new JointFeatureBoundary();
            b.Feature.JointCommand("native-joint", "start");
            switch (revoke)
            {
                case "finalize": b.Feature.CensusFinalize(); break;
                case "mutation": b.Feature.CensusMutation(); break;
                case "logout": b.Feature.CensusLogout(); break;
                case "territory": b.Feature.CensusTerritory(); break;
                case "stop": b.Feature.JointCommand("native-joint", "stop"); break;
                case "dispose": b.Feature.CensusDispose(); break;
            }
            b.Feature.CensusTick();
            Check(b.Joint.Controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5))
                && !b.Feature.SourceCensusBlocksOperations && b.Joint.Provider.Created.All(hook => hook.IsDisposed),
                revoke + " revokes and physically retires the exact joint owner");
            var reads = b.Joint.Memory.Memory.Reads;
            b.Joint.Source(1);
            Check(b.Joint.Memory.Memory.Reads == reads && b.Joint.Controller.Snapshot.Census is null,
                revoke + " prevents a late native return from publishing or reading slots");
            Check(b.Feature.JointSafety is { BlocksOperations: false, PhysicalDrainCompleted: true,
                Hooks: 0, InFlight: 0, ReservationHeld: false, ProductionAdmission: false },
                revoke + " leaves detached safety with confirmed physical drain");
            if (revoke == "dispose") Check(b.Feature.CensusDrain.IsCompletedSuccessfully,
                "Feature disposal includes the joint owner in its retained diagnostic drain");
        }
        Console.WriteLine($"JointFeatureCases: {checks} actual command, JSON, safety and lifecycle checks passed.");
        return 0;
    }
}
#endif
