namespace FireGaze.PortraitPreview;

internal enum PortraitConsumerInitialCoverage { Unknown }
internal enum PortraitConsumerIntervalOrder { Before, After, OrderingUnknown }
internal enum PortraitConsumerObservationIssue
{
    InitialConsumersNotEnumerated, EvidenceUnavailable, ContextMismatch, HostChanged,
    ManifestInvalid, EventLogEmpty, DroppedEvents, SequenceGap, UnknownTarget,
    InvalidEvent, OriginalFailed, ThreadChanged, OrderingUnknown, ReinitializationUnknown,
    CensusIncomplete, SlotConflict, ResourceIdentityUnknown, SourcePhaseUnavailable,
    SourceRenderNotAppended, SourceRenderAmbiguous,
}

// The expected managed request identity. Passing it creates no callback/read authority.
internal readonly record struct PortraitConsumerObservationContext(Guid Installation, long SessionEpoch,
    long RequestEpoch, PortraitIdentity Source);

internal sealed record PortraitConsumerObservationInput(PortraitConsumerObservationContext Context,
    PortraitOwnedSceneJointHostSnapshot? Before, PortraitOwnedSceneJointHostSnapshot? After,
    PortraitOwnedSceneJointSlotObservation? Slots, PortraitOwnedScenePassiveNativeEvents? NativeEvents,
    IReadOnlyList<PortraitOwnedSceneNativeTarget> Manifest);

// Sequence labels identify rows only; interval relations never use completion sequence as time.
internal readonly record struct PortraitConsumerIntervalRelation(long LeftSequence, long RightSequence,
    PortraitConsumerIntervalOrder Order);
internal sealed record PortraitConsumerOpaqueReceiver(long Address, int ObservedInitializations,
    IReadOnlyList<PortraitOwnedScenePassiveNativeEvent> Calls);

// Historical same-source evidence only. It is not a live consumer, owner, idle slot, lease or bootstrap.
internal sealed record PortraitConsumerSourceObservation(PortraitConsumerObservationContext Context,
    PortraitOwnedSceneJointSourceReturn SourceReturn, PortraitOwnedSceneSlotPhase Phase,
    PortraitOwnedSceneSlotCensusRow SourceSlot, PortraitNativeResourceState Resource,
    PortraitOwnedScenePassiveNativeEvent Render);

internal sealed record PortraitConsumerObservationSnapshot(PortraitConsumerObservationContext Context, int ManifestTargetCount,
    IReadOnlyList<PortraitConsumerOpaqueReceiver> OpaqueReceivers,
    IReadOnlyList<PortraitConsumerIntervalRelation> IntervalRelations,
    IReadOnlyList<PortraitConsumerObservationIssue> Issues,
    PortraitConsumerSourceObservation? SourceObservation)
{
    public PortraitConsumerInitialCoverage InitialCoverage => PortraitConsumerInitialCoverage.Unknown;
    public bool ProductionAdmission => false;
    // No active-instance count or synthetic allocation/lifecycle epoch is exposed.
}

internal static class PortraitConsumerObservation
{
    internal static PortraitConsumerObservationSnapshot Collect(PortraitConsumerObservationInput input)
    {
        var issues = new HashSet<PortraitConsumerObservationIssue>
            { PortraitConsumerObservationIssue.InitialConsumersNotEnumerated };
        var manifest = input.Manifest.ToArray();
        var events = input.NativeEvents?.Events.ToArray() ?? [];
        var receivers = events.GroupBy(row => row.Receiver).Select(group => new PortraitConsumerOpaqueReceiver(
            group.Key, group.Count(row => row.TargetKind == PortraitOwnedSceneNativeKind.Initialize),
            Array.AsReadOnly(group.ToArray()))).ToArray();
        var relations = new List<PortraitConsumerIntervalRelation>();
        var expected = input.Context;
        // Validate available host identity even while the first census has not been published.
        if (expected.Installation == Guid.Empty || expected.SessionEpoch <= 0 || expected.RequestEpoch <= 0
            || !expected.Source.IsValid || expected.Source.Generation == 0
            || input.Before is { } initial && !MatchesRequest(initial, expected)
            || input.After is { } final && !MatchesRequest(final, expected))
            issues.Add(PortraitConsumerObservationIssue.ContextMismatch);
        if (manifest.Length == 0 || manifest.Any(row => row.Address <= 0 || !Enum.IsDefined(row.Kind))
            || manifest.Distinct().Count() != manifest.Length)
            issues.Add(PortraitConsumerObservationIssue.ManifestInvalid);

        if (input.NativeEvents is not { } nativeEvents)
            issues.Add(PortraitConsumerObservationIssue.EvidenceUnavailable);
        else
        {
            if (nativeEvents.Installation != input.Context.Installation)
                issues.Add(PortraitConsumerObservationIssue.ContextMismatch);
            if (nativeEvents.Dropped != 0) issues.Add(PortraitConsumerObservationIssue.DroppedEvents);
            if (events.Length == 0) issues.Add(PortraitConsumerObservationIssue.EventLogEmpty);
            if (nativeEvents.Capacity is < 1 or > 256 || events.Length > nativeEvents.Capacity)
                issues.Add(PortraitConsumerObservationIssue.InvalidEvent);
            // Sequence is a publication-loss check only. It is assigned on completion.
            if (!events.Select(row => row.Sequence).Order().SequenceEqual(
                Enumerable.Range(1, events.Length).Select(value => (long)value)))
                issues.Add(PortraitConsumerObservationIssue.SequenceGap);
        }

        var validIntervals = events.All(row => row.EnteredAt >= 0 && row.ReturnedAt >= row.EnteredAt);
        foreach (var row in events)
        {
            if (!manifest.Contains(new(row.TargetKind, (nint)row.TargetAddress)))
                issues.Add(PortraitConsumerObservationIssue.UnknownTarget);
            if (row.Receiver <= 0 || row.TargetAddress <= 0 || row.ArgumentCount is < 0 or > 5
                || row.EnteredAt < 0 || row.ReturnedAt < row.EnteredAt)
                issues.Add(PortraitConsumerObservationIssue.InvalidEvent);
            if (!row.OriginalReturned) issues.Add(PortraitConsumerObservationIssue.OriginalFailed);
            if (!ValidThread(row.EntryThread) || row.EntryThread != row.ReturnThread)
                issues.Add(PortraitConsumerObservationIssue.ThreadChanged);
        }
        // No lifecycle reconstruction, including for an address that is initialized again.
        if (receivers.Any(receiver => receiver.ObservedInitializations > 1
            || receiver.Address == (long)input.Context.Source.Model && receiver.ObservedInitializations > 0))
            issues.Add(PortraitConsumerObservationIssue.ReinitializationUnknown);
        if (validIntervals && events.Length <= 256)
            for (var left = 0; left < events.Length; left++)
                for (var right = left + 1; right < events.Length; right++)
                {
                    var a = events[left]; var b = events[right];
                    var order = a.ReturnedAt < b.EnteredAt ? PortraitConsumerIntervalOrder.Before
                        : b.ReturnedAt < a.EnteredAt ? PortraitConsumerIntervalOrder.After
                        : PortraitConsumerIntervalOrder.OrderingUnknown;
                    relations.Add(new(a.Sequence, b.Sequence, order));
                    if (order == PortraitConsumerIntervalOrder.OrderingUnknown)
                        issues.Add(PortraitConsumerObservationIssue.OrderingUnknown);
                }

        PortraitConsumerSourceObservation? associated = null;
        if (input.Before is not { } before || input.After is not { } after || input.Slots is not { } slots)
            issues.Add(PortraitConsumerObservationIssue.EvidenceUnavailable);
        else
        {
            var context = input.Context;
            var sourceReturn = slots.SourceReturn;
            var census = slots.Slots;
            var phase = census.Phase;
            // These are detached historical identities, never a replacement for the private callback marker.
            if (context.Installation == Guid.Empty || context.SessionEpoch <= 0 || context.RequestEpoch <= 0
                || !context.Source.IsValid || context.Source.Generation == 0
                || before.Installation != context.Installation || after.Installation != context.Installation
                || before.SessionEpoch != context.SessionEpoch || after.SessionEpoch != context.SessionEpoch
                || before.RequestEpoch != context.RequestEpoch || after.RequestEpoch != context.RequestEpoch
                || slots.Installation != context.Installation || sourceReturn.Installation != context.Installation
                || sourceReturn.SessionEpoch != context.SessionEpoch || sourceReturn.RequestEpoch != context.RequestEpoch
                || sourceReturn.Source != context.Source || phase.Source != context.Source
                || phase.SessionEpoch != context.SessionEpoch || sourceReturn.NativeFrame != phase.NativeFrame
                || sourceReturn.MutationEpoch != before.MutationEpoch || sourceReturn.MutationEpoch != after.MutationEpoch
                || sourceReturn.Call <= 0 || !ValidThread(sourceReturn.Thread) || phase.ThreadID != sourceReturn.Thread.ManagedID
                || !before.SourceReturns.Contains(sourceReturn) || !after.SourceReturns.Contains(sourceReturn))
                issues.Add(PortraitConsumerObservationIssue.ContextMismatch);
            if (!SameHost(before, after) || before.Failed || after.Failed
                || before.CounterDepth != 0 || after.CounterDepth != 0
                || before.ResourceDepth != 0 || after.ResourceDepth != 0 || before.MutationEpoch < 0)
                issues.Add(PortraitConsumerObservationIssue.HostChanged);
            if (!census.Complete || !phase.InNativePhase || !phase.BuildValidated || !phase.Roots.IsValid
                || phase.ManagerGeneration <= 0 || phase.PhaseToken <= 0)
                issues.Add(PortraitConsumerObservationIssue.CensusIncomplete);

            var rows = census.Rows.ToArray();
            var sourceRows = rows.Where(row => row.Kind == PortraitOwnedSceneSlotCensusKind.Source).ToArray();
            if (rows.Length != 8 || !rows.Select(row => row.Slot).Order().SequenceEqual(Enumerable.Range(0, 8).Select(i => (uint)i))
                || rows.Any(row => !Enum.IsDefined(row.Kind) || row.ActiveCount < 0 || row.Kind is PortraitOwnedSceneSlotCensusKind.IncompleteOrChanged
                    or PortraitOwnedSceneSlotCensusKind.OccupiedUnknown)
                || sourceRows.Length != 1 || sourceRows.Any(row => row.Character != (nint)context.Source.Character
                    || row.RegisteredCamera == 0 || row.ObjectKind == 0 || row.ActiveCount <= 0)
                || rows.Any(row => row.Kind == PortraitOwnedSceneSlotCensusKind.ProvedIdle
                    && (row.Character != 0 || row.RegisteredCamera != 0 || row.ObjectKind != 0 || row.ActiveCount != 0
                        || row.RenderActive || row.RetainedOffscreenCamera != 0 || row.RetainedViewCamera != 0)))
                issues.Add(PortraitConsumerObservationIssue.SlotConflict);

            if (before.Resource.Generation <= 0 || before.Resource.Depth != 0
                || before.Resource.Receiver != phase.Roots.RenderTargets
                || before.Resource.Transition is not (PortraitNativeResourceTransition.CleanupReturned
                    or PortraitNativeResourceTransition.RecreateReturned))
                issues.Add(PortraitConsumerObservationIssue.ResourceIdentityUnknown);

            if (events.Any(row => row.EntryThread != sourceReturn.Thread || row.ReturnThread != sourceReturn.Thread))
                issues.Add(PortraitConsumerObservationIssue.ThreadChanged);
            var sourceRecords = before.Observation.Records.Where(row => row.Capture == before.Observation.Capture
                && row.Call == sourceReturn.Call && row.Phase == PortraitNativeObservedPhase.SourceCharaViewRender).ToArray();
            var entered = sourceRecords.Where(row => row.Edge == PortraitNativeObservedEdge.Enter).ToArray();
            var returned = sourceRecords.Where(row => row.Edge == PortraitNativeObservedEdge.Return).ToArray();
            if (before.Observation.Capture <= 0 || before.Observation.OverwrittenRecords != 0
                || before.Observation.Stop is PortraitNativeObservationStop.ThreadLimit or PortraitNativeObservationStop.DepthLimit
                    or PortraitNativeObservationStop.ObserverFailure or PortraitNativeObservationStop.InvalidNesting
                || before.Observation.ObservedThreads != 1 || entered.Length != 1 || returned.Length != 1
                || sourceRecords.Length != 2 || sourceRecords.Any(row => row.Receiver != context.Source.Model
                    || row.Thread != sourceReturn.Thread || row.Frame.SourceView != context.Source.Model
                    || row.Frame.Origin != PortraitNativeFrameOrigin.SourceRenderArgument || row.Frame.Value != sourceReturn.NativeFrame)
                || entered.Length == 1 && returned.Length == 1 && entered[0].Timestamp > returned[0].Timestamp)
                issues.Add(PortraitConsumerObservationIssue.SourcePhaseUnavailable);
            else
            {
                var candidates = events.Where(row => row.TargetKind == PortraitOwnedSceneNativeKind.Render
                    && row.Receiver == (long)context.Source.Model && row.ArgumentCount == 1 && row.Argument0 == sourceReturn.NativeFrame
                    && row.EnteredAt <= entered[0].Timestamp && row.ReturnedAt >= returned[0].Timestamp).ToArray();
                if (candidates.Length == 0) issues.Add(PortraitConsumerObservationIssue.SourceRenderNotAppended);
                else if (candidates.Length != 1) issues.Add(PortraitConsumerObservationIssue.SourceRenderAmbiguous);
                else if (issues.Count == 1 && sourceRows.Length == 1)
                    associated = new(context, sourceReturn, phase, sourceRows[0], before.Resource, candidates[0]);
            }
        }
        // Never publish a prior request/installation's opaque addresses as this request's observations.
        var contextMismatch = issues.Contains(PortraitConsumerObservationIssue.ContextMismatch)
            || input.Before is null || input.After is null;
        return new(input.Context, manifest.Length, Array.AsReadOnly(contextMismatch ? [] : receivers),
            Array.AsReadOnly(contextMismatch ? [] : relations.ToArray()),
            Array.AsReadOnly(issues.Order().ToArray()), associated);
    }

    private static bool ValidThread(PortraitNativeThreadStamp thread) => thread.NativeID > 0 && thread.ManagedID > 0;
    private static bool MatchesRequest(PortraitOwnedSceneJointHostSnapshot host, PortraitConsumerObservationContext expected)
        => host.Installation == expected.Installation && host.SessionEpoch == expected.SessionEpoch
            && host.RequestEpoch == expected.RequestEpoch;
    private static bool SameHost(PortraitOwnedSceneJointHostSnapshot a, PortraitOwnedSceneJointHostSnapshot b)
        => a.Installation == b.Installation && a.SessionEpoch == b.SessionEpoch && a.RequestEpoch == b.RequestEpoch
            && a.MutationEpoch == b.MutationEpoch && a.CounterDepth == b.CounterDepth && a.ResourceDepth == b.ResourceDepth
            && a.Recording == b.Recording && a.Failed == b.Failed && a.Resource == b.Resource
            && a.Hooks == b.Hooks && a.InFlight == b.InFlight && a.ReservationHeld == b.ReservationHeld
            && a.PhysicalRetired == b.PhysicalRetired && a.SourceReturns.SequenceEqual(b.SourceReturns)
            && a.Observation.Observing == b.Observation.Observing && a.Observation.Stop == b.Observation.Stop
            && a.Observation.Capture == b.Observation.Capture && a.Observation.OverwrittenRecords == b.Observation.OverwrittenRecords
            && a.Observation.ObservedThreads == b.Observation.ObservedThreads
            && a.Observation.Records.SequenceEqual(b.Observation.Records);
}
