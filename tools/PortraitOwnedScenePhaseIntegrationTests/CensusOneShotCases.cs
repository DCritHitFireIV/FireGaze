using FireGaze.PortraitPreview;

// Uses the production diagnostic/Host/gateway/census/reader composition. Only
// official Hook creation/Original and native copies cross the allocated fixture.
internal static unsafe class CensusOneShotCases
{
    internal static int Run()
    {
        using var memory = new Fixture();
        memory.SetIdle(6);
        var factory = new SourceReturnCases.Factory();
        var context = new PortraitSourceCensusContext(memory.Phase.Source, 1, 2, 1);
        var controller = new PortraitSourceCensusDiagnostic(() => true, () => context, factory, memory.Memory,
            timestamp: () => 0, timestampFrequency: 1000, thread: PortraitSourceCensusDiagnostic.CurrentThread,
            sourceResolve: SourceReturnCases.Resolve(PortraitNativePhaseTargetSet.PhaseObservation),
            counterResolve: SourceReturnCases.Resolve(PortraitNativePhaseTargetSet.CounterIngress),
            resourceResolve: SourceReturnCases.Resolve(PortraitNativePhaseTargetSet.ResourceIngress));
        try
        {
            if (!controller.TryStart(out var reason)) throw new InvalidOperationException("FAIL: one-shot diagnostic start: " + reason);
            Source(0);
            controller.Update();
            if (controller.Snapshot.State != PortraitSourceCensusDiagnosticState.Armed || memory.Memory.Reads != 0)
                throw new InvalidOperationException("FAIL: one-shot diagnostic needs passive evidence before its future return.");
            Source(1);
            var firstReads = memory.Memory.Reads;
            if (firstReads == 0) throw new InvalidOperationException("FAIL: first qualified return must actually sample the native table.");
            Source(2); // Deliberately no Feature/controller Update between these actual returns.
            if (memory.Memory.Reads != firstReads)
                throw new InvalidOperationException("FAIL: successful one-shot publication must consume its request before another source return can read.");
            controller.Update();
            if (controller.Snapshot.Result is not { Complete: true, Rows.Count: 8, ProductionAdmission: false, Phase.NativeFrame: 1 })
                throw new InvalidOperationException("FAIL: one-shot diagnostic must retain its first complete historical frame.");
            if (!controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("FAIL: one-shot diagnostic must physically retire after its one result.");
            Console.WriteLine("CensusOneShotCases: second actual return before Update performs zero extra native reads and first frame is retained.");
            return 0;
        }
        finally
        {
            controller.Stop("one-shot fixture close");
            if (!controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("FAIL: one-shot fixture must confirm native retirement before freeing allocated SDK memory.");
        }

        void Source(uint frame) => factory.Call(PortraitNativeObservedPhase.SourceCharaViewRender, (nint)memory.Source, frame);
    }
}
