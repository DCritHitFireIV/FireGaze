using FireGaze.PortraitPreview;

// Exercise the real three-host controller: an allowed SDK pair is insufficient
// unless all three independently resolved plans identify the same installation.
internal static unsafe class CensusSdkIdentityCases
{
    internal static int Run()
    {
        var checks = 0;
        long moduleKey = 0xA100000000;
        void Check(bool valid, string name)
        { checks++; if (!valid) throw new InvalidOperationException("FAIL: " + name); }
        foreach (var mode in new[] { "current", "frozen-counter", "frozen-resource", "mixed-pair" })
        {
            using var memory = new Fixture(); memory.SetIdle(6);
            var factory = new SourceReturnCases.Factory();
            var module = (nint)(moduleKey += 0x10000000);
            var context = new PortraitSourceCensusContext(memory.Phase.Source, 1, 2, 1);
            PortraitNativePhaseHostResolve Resolve(PortraitNativePhaseTargetSet set)
                => (Func<nint, bool> reserved, out PortraitNativePhaseResolvedPlan? plan, out string reason) =>
                {
                    var valid = SourceReturnCases.Resolve(set)(reserved, out plan, out reason);
                    if (!valid || plan is null) return false;
                    plan = plan with { ModuleBase = module,
                        Targets = PortraitNativeCounterIngressContract.For(set).Select(contract =>
                            new PortraitNativePhaseBoundTarget(contract.Phase, module + (nint)contract.RVA, contract.Delegate)).ToArray() };
                    var frozen = mode == "frozen-counter" && set == PortraitNativePhaseTargetSet.CounterIngress
                        || mode == "frozen-resource" && set == PortraitNativePhaseTargetSet.ResourceIngress;
                    if (!frozen) plan = plan with {
                        OnDiskSDKSHA256 = "5726E301EE5456D526F524E9F88659F38A8717CB527FE4A86D83D1ADE0AB9798",
                        SDKMVID = mode == "mixed-pair" && set == PortraitNativePhaseTargetSet.ResourceIngress
                            ? Guid.Parse("c72f17a8-6ed1-4a30-a097-74137dc08f9e")
                            : Guid.Parse("18eb3871-c94c-4c54-846b-2ee6a94a336d") };
                    return true;
                };
            var controller = new PortraitSourceCensusDiagnostic(() => true, () => context, factory, memory.Memory,
                timestamp: () => 0, timestampFrequency: 1000, thread: PortraitSourceCensusDiagnostic.CurrentThread,
                sourceResolve: Resolve(PortraitNativePhaseTargetSet.PhaseObservation),
                counterResolve: Resolve(PortraitNativePhaseTargetSet.CounterIngress),
                resourceResolve: Resolve(PortraitNativePhaseTargetSet.ResourceIngress));
            try
            {
                var started = controller.TryStart(out var reason);
                if (mode == "current")
                {
                    Check(started && factory.Creates == 8, "three matching audited current SDK plans start one read-only census: " + reason);
                    factory.Call(PortraitNativeObservedPhase.SourceCharaViewRender, (nint)memory.Source, 0);
                    controller.Update();
                    Check(controller.Snapshot.State == PortraitSourceCensusDiagnosticState.Armed && memory.Memory.Reads == 0,
                        "current SDK census waits for a future qualified SourceRender return");
                    factory.Call(PortraitNativeObservedPhase.SourceCharaViewRender, (nint)memory.Source, 1);
                    controller.Update();
                    Check(controller.Snapshot.Result is { Complete: true, Rows.Count: 8, ProductionAdmission: false },
                        "current SDK read-only census keeps complete detached rows without production admission");
                }
                else Check(!started && controller.Snapshot.Result is null && memory.Memory.Reads == 0,
                    "mixed SDK plans cannot read the native table or publish a result: " + mode);
            }
            finally
            {
                controller.Stop("SDK identity fixture close");
                Check(controller.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)),
                    "SDK identity fixture confirms all three hosts retired before freeing memory: " + mode);
            }
        }
        Console.WriteLine($"CensusSdkIdentityCases: {checks} checks passed; real three-host controller, self-allocated native fixture only.");
        return 0;
    }
}
