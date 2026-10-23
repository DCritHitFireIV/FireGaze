using System.Text.Json;
using FireGaze.PortraitPreview;

internal static class StageABoundaryEvidenceCases
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException("FAIL stage A evidence " + message); }

    internal static int Run()
    {
        var failed = 0;
        foreach (var test in new Action[] { CensusMutationEvidenceWithoutCensus, JointResourceEvidenceWithoutCensus })
        {
            try { test(); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {test.Method.Name}: {error.Message}"); }
        }
        Console.WriteLine($"StageABoundaryEvidenceCases: {checks} checks, {failed} failed; actual Feature projections, external native boundary only.");
        return failed == 0 ? 0 : 1;
    }

    // Break caught: omitting the mutation/resource projection hides actual
    // invalidation evidence whenever no eight-slot result has been produced.
    private static void CensusMutationEvidenceWithoutCensus()
    {
        using var b = new CensusFeatureBoundary();
        b.Feature.CensusCommand("native-census", "start");
        var manager = b.Memory.Phase.Roots.RenderTargets;
        b.Factory.Call(PortraitNativeObservedPhase.CounterAdd, b.Memory.Phase.Roots.Renderer, 6);
        b.Factory.Call(PortraitNativeObservedPhase.ResourceCleanup, manager, 0);
        Check(b.Factory.Call(PortraitNativeObservedPhase.ResourceRecreate, manager, 0) == 0xA7,
            "external native recreate result passes through unchanged");
        b.Feature.CensusTick();
        Check(b.Feature.CensusSnapshot is { State: PortraitSourceCensusDiagnosticState.ObservingThread, Result: null },
            "ordinary refresh retains mutation evidence before any SourceRender without arming");
        Check(b.Memory.Memory.Reads == 0 && b.Factory.Creates == 8,
            "pre-source refresh performs zero native slot reads and creates no additional Hooks");
        var reads = b.Memory.Memory.Reads; var creates = b.Factory.Creates;
        using (var json = JsonDocument.Parse(b.Feature.CensusJSON()))
        {
            var root = json.RootElement;
            var counters = root.GetProperty("counters");
            Check(counters.TryGetProperty("CounterInvalidationEpoch", out var epoch) && epoch.GetInt64() == 1,
                "pre-census JSON exposes the actual counter invalidation epoch");
            Check(!counters.GetProperty("CounterInvalidationFailed").GetBoolean(),
                "counter invalidation failure evidence is present without inference");
            var resources = root.GetProperty("resources");
            Check(!resources.GetProperty("ResourceInvalidationFailed").GetBoolean(),
                "resource invalidation failure evidence is present without inference");
            var resource = resources.GetProperty("resource");
            Check(resource.GetProperty("Generation").GetInt64() == 2 && resource.GetProperty("Depth").GetInt32() == 0
                && resource.GetProperty("transition").GetString() == "RecreateReturned"
                && resource.GetProperty("Receiver").GetInt64() == (long)manager
                && resource.GetProperty("NativeReturn").GetByte() == 0xA7 && !resource.GetProperty("Ready").GetBoolean(),
                "resource generation, original byte and scalar receiver survive JSON without granting Ready");
            var safety = root.GetProperty("safety");
            Check(safety.GetProperty("BlocksOperations").GetBoolean() && !safety.GetProperty("PhysicalDrainCompleted").GetBoolean()
                && safety.GetProperty("Resources").GetProperty("Hooks").GetInt32() == 2
                && safety.GetProperty("Resources").GetProperty("ReservationHeld").GetBoolean(),
                "pre-census JSON includes current physical ownership rather than treating recorder state as drain");
            Check(root.GetProperty("result").ValueKind == JsonValueKind.Null && !root.GetProperty("productionAdmission").GetBoolean(),
                "mutation evidence neither fabricates a census nor changes admission");
        }
        Check(b.Memory.Memory.Reads == reads && b.Factory.Creates == creates,
            "serializing detached census evidence performs no extra native reads or Hook creation");
        b.Feature.CensusCommand("native-census", "stop");
        Check(b.Feature.CensusController.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "census batch physically drains");
        using var stopped = JsonDocument.Parse(b.Feature.CensusJSON());
        var stoppedSafety = stopped.RootElement.GetProperty("safety");
        Check(stoppedSafety.GetProperty("PhysicalDrainCompleted").GetBoolean()
            && !stoppedSafety.GetProperty("BlocksOperations").GetBoolean()
            && stoppedSafety.GetProperty("Resources").GetProperty("Hooks").GetInt32() == 0
            && !stoppedSafety.GetProperty("Resources").GetProperty("ReservationHeld").GetBoolean(),
            "post-stop JSON confirms actual physical resource-host retirement");
    }

    // Break caught: projecting resource state only under a successful consumer
    // association loses cleanup/recreate evidence on rejected or missing sources.
    private static void JointResourceEvidenceWithoutCensus()
    {
        using var b = new JointFeatureBoundary();
        b.Feature.JointCommand("native-joint", "start");
        b.Joint.RecreateResource();
        var reads = b.Joint.Memory.Memory.Reads; var creates = b.Joint.Provider.Created.Count;
        using var json = JsonDocument.Parse(b.Feature.JointJSON());
        var root = json.RootElement;
        var host = root.GetProperty("host");
        Check(host.TryGetProperty("resource", out var resource),
            "joint host JSON exposes resource state before a source/census association exists");
        Check(resource.GetProperty("Generation").GetInt64() == 1 && resource.GetProperty("Depth").GetInt32() == 0
            && resource.GetProperty("transition").GetString() == "RecreateReturned"
            && resource.GetProperty("Receiver").GetInt64() == (long)b.Joint.Memory.Phase.Roots.RenderTargets
            && resource.GetProperty("NativeReturn").GetByte() == 0 && !resource.GetProperty("Ready").GetBoolean(),
            "joint projection preserves a zero native result and exact receiver without granting Ready");
        Check(root.GetProperty("census").ValueKind == JsonValueKind.Null && root.GetProperty("consumers").ValueKind == JsonValueKind.Null
            && !root.GetProperty("productionAdmission").GetBoolean(),
            "resource diagnostics remain available independently of successful census or consumer evidence");
        Check(b.Joint.Memory.Memory.Reads == reads && b.Joint.Provider.Created.Count == creates,
            "joint resource JSON performs no native reads or Hook creation");
    }
}
