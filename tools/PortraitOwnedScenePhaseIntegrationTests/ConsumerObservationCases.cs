using FireGaze.PortraitPreview;

// Detached fixtures exercise the production collector linked by this project.
// Breaks caught: missing identity/health checks, fake chronological ordering,
// mutable output aliasing, or treating a partial passive append as complete.
internal static class ConsumerObservationCases
{
    internal static int Run()
    {
        var failures = new List<string>();
        var checks = 0;
        void Check(string name, Action test)
        {
            checks++;
            try { test(); }
            catch (Exception ex) { failures.Add(name + ": " + ex.Message); }
        }
        void Require(bool value, string message) { if (!value) throw new Exception(message); }
        void Issue(PortraitConsumerObservationInput input, PortraitConsumerObservationIssue issue)
        {
            var result = PortraitConsumerObservation.Collect(input);
            Require(result.Issues.Contains(issue), "Missing " + issue);
            Require(result.SourceObservation is null, "Uncertain input acquired a source association");
            Require(!result.ProductionAdmission && result.InitialCoverage == PortraitConsumerInitialCoverage.Unknown,
                "Observation gained production/initial coverage authority");
        }

        Check("same detached source evidence associates historically; bootstrap remains unknown", () =>
        {
            var f = new ConsumerObservationFixture(); var result = PortraitConsumerObservation.Collect(f.Input);
            Require(result.SourceObservation is not null, "No matching source observation");
            Require(result.SourceObservation!.Render == f.Render && result.SourceObservation.SourceSlot == f.Rows[0], "Wrong association");
            Require(!result.ProductionAdmission && result.InitialCoverage == PortraitConsumerInitialCoverage.Unknown, "Unsafe authority");
            Require(result.Issues.SequenceEqual([PortraitConsumerObservationIssue.InitialConsumersNotEnumerated]), "Unexpected positive issues");
        });
        Check("39 targets are not 39 receiver instances", () =>
        {
            var f = new ConsumerObservationFixture(); var result = PortraitConsumerObservation.Collect(f.Input);
            Require(result.ManifestTargetCount == 39 && result.OpaqueReceivers.Count == 1, "Targets counted as instances");
            Require(result.OpaqueReceivers[0].Address == f.Source.Model, "Wrong opaque receiver");
        });
        Check("zero events never imply no consumers", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.WithEvents([]), PortraitConsumerObservationIssue.EventLogEmpty);
        });
        Check("dropped native events", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.Input with { NativeEvents = f.Events with { Dropped = 1 } }, PortraitConsumerObservationIssue.DroppedEvents);
        });
        Check("completion publication hole", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.WithEvents([f.Render with { Sequence = 2 }]), PortraitConsumerObservationIssue.SequenceGap);
        });
        Check("duplicate completion sequence", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.WithEvents([f.Render, f.Render]), PortraitConsumerObservationIssue.SequenceGap);
        });
        Check("unknown target kind/address", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.WithEvents([f.Render with { TargetAddress = 0xdead }]), PortraitConsumerObservationIssue.UnknownTarget);
        });
        Check("original failure", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.WithEvents([f.Render with { OriginalReturned = false }]), PortraitConsumerObservationIssue.OriginalFailed);
        });
        Check("thread changes across Original", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.WithEvents([f.Render with { ReturnThread = new(9, 9) }]), PortraitConsumerObservationIssue.ThreadChanged);
        });
        Check("other event on foreign thread", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.WithEvents([f.Render, f.Render with { Sequence = 2, Receiver = 900,
                EnteredAt = 220, ReturnedAt = 230, EntryThread = new(9, 9), ReturnThread = new(9, 9) }]), PortraitConsumerObservationIssue.ThreadChanged);
        });
        Check("late hot-reload installation", () =>
        {
            var f = new ConsumerObservationFixture(); var input = f.Input with { NativeEvents = f.Events with { Installation = Guid.NewGuid() } };
            Issue(input, PortraitConsumerObservationIssue.ContextMismatch);
            Require(PortraitConsumerObservation.Collect(input).OpaqueReceivers.Count == 0, "Late installation leaked receiver observations");
        });
        Check("late session", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.Input with { Context = f.Context with { SessionEpoch = 2 } }, PortraitConsumerObservationIssue.ContextMismatch);
        });
        Check("late request", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.Input with { Context = f.Context with { RequestEpoch = 3 } }, PortraitConsumerObservationIssue.ContextMismatch);
        });
        Check("source pointer ABA generation", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.Input with { Context = f.Context with { Source = f.Source with { Generation = 2 } } }, PortraitConsumerObservationIssue.ContextMismatch);
        });
        Check("counter/mutation epoch changes during collection", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.Input with { After = f.Host with { MutationEpoch = 11 } }, PortraitConsumerObservationIssue.HostChanged);
        });
        Check("counter Original still in flight", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.Input with { Before = f.Host with { CounterDepth = 1 }, After = f.Host with { CounterDepth = 1 } }, PortraitConsumerObservationIssue.HostChanged);
        });
        Check("same resource address new generation", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.Input with { After = f.Host with { Resource = f.Resource with { Generation = 4 } } }, PortraitConsumerObservationIssue.HostChanged);
        });
        Check("resource identity has never been observed", () =>
        {
            var f = new ConsumerObservationFixture(); var h = f.Host with { Resource = default };
            Issue(f.Input with { Before = h, After = h }, PortraitConsumerObservationIssue.ResourceIdentityUnknown);
        });
        Check("resource receiver differs from census root", () =>
        {
            var f = new ConsumerObservationFixture(); var h = f.Host with { Resource = f.Resource with { Receiver = 0x444 } };
            Issue(f.Input with { Before = h, After = h }, PortraitConsumerObservationIssue.ResourceIdentityUnknown);
        });
        Check("slot duplicate/missing conflict", () =>
        {
            var f = new ConsumerObservationFixture(); var rows = f.Rows.ToArray(); rows[1] = rows[0];
            Issue(f.Input with { Slots = f.Slots with { Slots = f.Slots.Slots with { Rows = rows } } }, PortraitConsumerObservationIssue.SlotConflict);
        });
        Check("source character differs from slot", () =>
        {
            var f = new ConsumerObservationFixture(); var rows = f.Rows.ToArray(); rows[0] = rows[0] with { Character = 0x999 };
            Issue(f.Input with { Slots = f.Slots with { Slots = f.Slots.Slots with { Rows = rows } } }, PortraitConsumerObservationIssue.SlotConflict);
        });
        Check("occupied unknown is not safely idle", () =>
        {
            var f = new ConsumerObservationFixture(); var rows = f.Rows.ToArray(); rows[1] = rows[1] with { Kind = PortraitOwnedSceneSlotCensusKind.OccupiedUnknown, ActiveCount = 1 };
            Issue(f.Input with { Slots = f.Slots with { Slots = f.Slots.Slots with { Rows = rows } } }, PortraitConsumerObservationIssue.SlotConflict);
        });
        Check("incomplete census", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.Input with { Slots = f.Slots with { Slots = f.Slots.Slots with { Complete = false } } }, PortraitConsumerObservationIssue.CensusIncomplete);
        });
        Check("unqualified manager generation", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.Input with { Slots = f.Slots with { Slots = f.Slots.Slots with { Phase = f.Phase with { ManagerGeneration = 0 } } } }, PortraitConsumerObservationIssue.CensusIncomplete);
        });
        Check("source Return not in same host snapshot", () =>
        {
            var f = new ConsumerObservationFixture(); var h = f.Host with { SourceReturns = [] };
            Issue(f.Input with { Before = h, After = h }, PortraitConsumerObservationIssue.ContextMismatch);
        });
        Check("source render outer passive append has not happened", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.WithEvents([f.Render with { Receiver = 900 }]), PortraitConsumerObservationIssue.SourceRenderNotAppended);
        });
        Check("phase enter without phase Return is incomplete", () =>
        {
            var f = new ConsumerObservationFixture(); var h = f.Host with { Observation = f.Observation with { Records = [f.Enter] } };
            Issue(f.Input with { Before = h, After = h }, PortraitConsumerObservationIssue.SourcePhaseUnavailable);
        });
        Check("nested callbacks: completion Sequence cannot invent entry order", () =>
        {
            var f = new ConsumerObservationFixture(); var inner = f.Render with { Sequence = 1, TargetKind = PortraitOwnedSceneNativeKind.Update,
                TargetAddress = 0x1010, Receiver = 900, EnteredAt = 120, ReturnedAt = 150 };
            var result = PortraitConsumerObservation.Collect(f.WithEvents([inner, f.Render with { Sequence = 2 }]));
            Require(result.IntervalRelations.Single().Order == PortraitConsumerIntervalOrder.OrderingUnknown, "Nested calls were sorted as a lifecycle");
            Require(result.Issues.Contains(PortraitConsumerObservationIssue.OrderingUnknown), "Missing interval ambiguity");
            Require(result.SourceObservation is null, "Ambiguous chronology acquired an association");
        });
        Check("strictly separated timestamps order without using Sequence", () =>
        {
            var f = new ConsumerObservationFixture(); var later = f.Render with { Sequence = 1, TargetKind = PortraitOwnedSceneNativeKind.Update,
                TargetAddress = 0x1010, Receiver = 900, EnteredAt = 220, ReturnedAt = 230 };
            var result = PortraitConsumerObservation.Collect(f.WithEvents([later, f.Render with { Sequence = 2 }]));
            Require(result.IntervalRelations.Single().Order == PortraitConsumerIntervalOrder.After, "Completion Sequence became temporal order");
        });
        Check("equal timestamp boundaries remain ordering unknown", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.WithEvents([f.Render, f.Render with { Sequence = 2, Receiver = 900, EnteredAt = 210, ReturnedAt = 220 }]), PortraitConsumerObservationIssue.OrderingUnknown);
        });
        Check("same receiver reinitialization does not synthesize allocation epoch", () =>
        {
            var f = new ConsumerObservationFixture(); var first = f.Render with { TargetKind = PortraitOwnedSceneNativeKind.Initialize, TargetAddress = 0x1011,
                EnteredAt = 10, ReturnedAt = 20 };
            Issue(f.WithEvents([first, first with { Sequence = 2, EnteredAt = 30, ReturnedAt = 40 }, f.Render with { Sequence = 3 }]),
                PortraitConsumerObservationIssue.ReinitializationUnknown);
        });
        Check("invalid interval", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.WithEvents([f.Render with { EnteredAt = 211 }]), PortraitConsumerObservationIssue.InvalidEvent);
        });
        Check("one observed source Initialize cannot prove allocation continuity", () =>
        {
            var f = new ConsumerObservationFixture(); var init = f.Render with { TargetKind = PortraitOwnedSceneNativeKind.Initialize,
                TargetAddress = 0x1011, EnteredAt = 10, ReturnedAt = 20 };
            Issue(f.WithEvents([init, f.Render with { Sequence = 2 }]), PortraitConsumerObservationIssue.ReinitializationUnknown);
        });
        Check("idle row retaining render/camera state is a conflict", () =>
        {
            var f = new ConsumerObservationFixture(); var rows = f.Rows.ToArray(); rows[1] = rows[1] with { RetainedViewCamera = 900 };
            Issue(f.Input with { Slots = f.Slots with { Slots = f.Slots.Slots with { Rows = rows } } }, PortraitConsumerObservationIssue.SlotConflict);
        });
        Check("unknown slot kind", () =>
        {
            var f = new ConsumerObservationFixture(); var rows = f.Rows.ToArray(); rows[1] = rows[1] with { Kind = (PortraitOwnedSceneSlotCensusKind)99 };
            Issue(f.Input with { Slots = f.Slots with { Slots = f.Slots.Slots with { Rows = rows } } }, PortraitConsumerObservationIssue.SlotConflict);
        });
        Check("failed phase recorder never gains a historical source association", () =>
        {
            var f = new ConsumerObservationFixture(); var h = f.Host with { Observation = f.Observation with { Stop = PortraitNativeObservationStop.ObserverFailure } };
            Issue(f.Input with { Before = h, After = h }, PortraitConsumerObservationIssue.SourcePhaseUnavailable);
        });
        Check("ambiguous duplicate source wrapper", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.WithEvents([f.Render, f.Render with { Sequence = 2 }]), PortraitConsumerObservationIssue.SourceRenderAmbiguous);
        });
        Check("null native events at install startup", () =>
        {
            var f = new ConsumerObservationFixture(); Issue(f.Input with { NativeEvents = null }, PortraitConsumerObservationIssue.EvidenceUnavailable);
        });
        Check("missing census cannot hide a late session", () =>
        {
            var f = new ConsumerObservationFixture(); var input = f.Input with { Slots = null, Context = f.Context with { SessionEpoch = 2 } };
            Issue(input, PortraitConsumerObservationIssue.ContextMismatch);
            Require(PortraitConsumerObservation.Collect(input).OpaqueReceivers.Count == 0, "Unbound old session leaked opaque rows");
        });
        Check("no host identity cannot label opaque events as current request", () =>
        {
            var f = new ConsumerObservationFixture(); var input = f.Input with { Before = null };
            Issue(input, PortraitConsumerObservationIssue.EvidenceUnavailable);
            Require(PortraitConsumerObservation.Collect(input).OpaqueReceivers.Count == 0, "Missing host still labelled current events");
        });
        Check("output detaches mutable input lists", () =>
        {
            var f = new ConsumerObservationFixture(); var events = new List<PortraitOwnedScenePassiveNativeEvent> { f.Render };
            var result = PortraitConsumerObservation.Collect(f.WithEvents(events)); events.Clear();
            Require(result.OpaqueReceivers.Single().Calls.Count == 1 && result.SourceObservation is not null, "Output retained mutable list");
        });
        Check("later passive append can be re-read without retaining stale output", () =>
        {
            var f = new ConsumerObservationFixture(); var pending = PortraitConsumerObservation.Collect(f.WithEvents([]));
            var later = PortraitConsumerObservation.Collect(f.Input);
            Require(pending.SourceObservation is null && later.SourceObservation is not null, "Passive append was not independently re-evaluated");
            Require(pending.OpaqueReceivers.Count == 0, "Earlier result was mutated by a later collection");
        });

        foreach (var failure in failures) Console.Error.WriteLine("FAIL " + failure);
        Console.WriteLine($"ConsumerObservationCases: {checks} checks; {failures.Count} failures.");
        return failures.Count == 0 ? 0 : 1;

    }
}

internal sealed class ConsumerObservationFixture
{
    internal readonly Guid Installation = Guid.Parse("11111111-2222-3333-4444-555555555555");
    internal readonly PortraitIdentity Source = new(100, 200, 300, 1, 400);
    internal readonly PortraitNativeThreadStamp Thread = new(1, 2);
    internal PortraitConsumerObservationContext Context => new(Installation, 1, 2, Source);
    internal PortraitNativeResourceState Resource => new(3, 0, PortraitNativeResourceTransition.RecreateReturned, 0x40, 1);
    internal PortraitOwnedSceneSlotPhase Phase => new(Source, new(0x10, 0x20, 0x30, 0x40, 0x50), 1, 6, 88, 4, 2, true, true);
    internal PortraitOwnedSceneJointSourceReturn SourceReturn => new(Installation, Source, 1, 2, 10, 88, Thread, 7);
    internal PortraitNativeFrameEvidence Frame => new(PortraitNativeFrameOrigin.SourceRenderArgument, 88, Source.Model);
    internal PortraitNativePhaseRecord Enter => new(1, 9, 7, 0, PortraitNativeObservedPhase.SourceCharaViewRender,
        PortraitNativeObservedEdge.Enter, Source.Model, Thread, 1, 110, Frame, null);
    internal PortraitNativePhaseRecord Return => Enter with { Sequence = 2, Edge = PortraitNativeObservedEdge.Return, Timestamp = 200 };
    internal PortraitNativePhaseObservationSnapshot Observation => new(true, PortraitNativeObservationStop.Disabled, 9, 0, 1, [Enter, Return]);
    internal PortraitOwnedSceneJointHostSnapshot Host => new(Installation, 1, 2, 10, 0, 0, true, false, Resource,
        [SourceReturn], Observation, 46, 1, true, false);
    internal PortraitOwnedSceneSlotCensusRow[] Rows => Enumerable.Range(0, 8).Select(i => i == 0
        ? new PortraitOwnedSceneSlotCensusRow(0, PortraitOwnedSceneSlotCensusKind.Source, 400, 1, 0, 1, 500, 1, true, 0, 0)
        : new PortraitOwnedSceneSlotCensusRow((uint)i, PortraitOwnedSceneSlotCensusKind.ProvedIdle, 0, 0, 0, 0, 0, 0, false, 0, 0)).ToArray();
    internal PortraitOwnedSceneJointSlotObservation Slots => new(Installation, SourceReturn, new(Phase, true, Rows));
    internal PortraitOwnedScenePassiveNativeEvent Render => new(1, PortraitOwnedSceneNativeKind.Render, 0x1001, 300,
        1, 88, 0, 0, 0, 0, 0, true, Thread, Thread, 100, 210);
    internal PortraitOwnedScenePassiveNativeEvents Events => new(Installation, 256, 0, [Render]);
    internal PortraitOwnedSceneNativeTarget[] Manifest => Enumerable.Range(0, 39).Select(i => i == 0
        ? new PortraitOwnedSceneNativeTarget(PortraitOwnedSceneNativeKind.Render, 0x1001)
        : new PortraitOwnedSceneNativeTarget(i == 2 ? PortraitOwnedSceneNativeKind.Initialize : PortraitOwnedSceneNativeKind.Update, 0x100f + i)).ToArray();
    internal PortraitConsumerObservationInput Input => new(Context, Host, Host, Slots, Events, Manifest);
    internal PortraitConsumerObservationInput WithEvents(IReadOnlyList<PortraitOwnedScenePassiveNativeEvent> events)
        => Input with { NativeEvents = Events with { Events = events } };
}
