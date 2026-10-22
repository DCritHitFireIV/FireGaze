namespace FireGaze.PortraitPreview;

// Detached physical evidence only. Reading it may refresh detached expiry but
// performs no native memory reads and grants no slot or scene admission.
internal sealed record PortraitOwnedSceneJointSafetyState(bool Known, string State, long Attempt,
    bool BlocksOperations, bool PhysicalDrainCompleted, Guid Installation, int Hooks, int InFlight,
    bool ReservationHeld, bool Failed, bool Recording)
{
    public bool ProductionAdmission => false;
}

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitOwnedSceneJointDiagnostic? ownedJoint;

    private void InitializeOwnedJoint(PortraitOwnedSceneJointDiagnostic? diagnostic = null)
    {
        ownedJoint = diagnostic ?? new(() => Plugin.Framework.IsInFrameworkUpdateThread, ReadSourceCensusContext,
            binding => new(new OwnedPassiveEnvironment(CurrentModuleBase()),
                new DalamudPortraitOwnedSceneJointHookFactory(Plugin.Interop), new PortraitNativePhaseHostReservations(),
                binding.Counters, binding.Resources, binding.RequestCurrent,
                mutationReceiver: binding.MutationReceiver, sourceReceiver: binding.SourceReturn),
            () => PortraitOwnedSceneJointPlan.TryCreate(CurrentModuleBase(), out var plan, out _) ? plan : null);
    }

    private bool TryOwnedJointCommand(string[] parts)
    {
        if (parts.Length == 0 || parts[0] != "native-joint") return false;
#if !OWNED_SLOT
        if (!pluginInterface.IsDev) return true;
#endif
        if (parts is ["native-joint", "start"])
            Mutate(() =>
            {
                if (sourceCensus?.BlocksOperations == true || ownedPassive?.BlocksOperations == true)
                    Error("其他原生诊断尚未完成实际退出。");
                else if (ownedJoint is null) Error("联合只读诊断尚未初始化。");
                else if (!ownedJoint.TryStart(out var reason)) Error("联合只读诊断未启动：" + reason);
                else Plugin.Chat.Print("[肖像联合只读诊断] " + ownedJoint.Snapshot.Reason);
            }, preserveSourceCensus: true);
        else if (parts is ["native-joint", "stop"])
            Mutate(() =>
            {
                ownedJoint?.Stop("Stopped explicitly by the user.");
                Plugin.Chat.Print("[肖像联合只读诊断] 已撤销观察，正在等待实际原生回调退出。");
            }, preserveSourceCensus: true);
        else if (parts is ["native-joint", "status"])
            Mutate(() =>
            {
                var state = ownedJoint?.Snapshot;
                Plugin.Chat.Print("[肖像联合只读诊断] " + (state is null ? "Off" : $"{state.State}: {state.Reason}")
                    + (state?.Census is { } census ? $" 八槽={census.Slots.Rows.Count}，仅历史观察。" : ""));
            }, preserveSourceCensus: true);
        else Error("用法：/firegaze portrait native-joint start|stop|status");
        return true;
    }

    private void UpdateOwnedJoint() => ownedJoint?.Update();

    private PortraitOwnedSceneJointSafetyState ReadOwnedJointSafetyState()
    {
        var diagnostic = ownedJoint;
        if (diagnostic is null) return new(true, "Off", 0, false, true, Guid.Empty, 0, 0, false, false, false);
        var state = diagnostic.Snapshot;
        var host = state.Host;
        return new(true, state.State.ToString(), state.Attempt, diagnostic.BlocksOperations,
            diagnostic.DrainPhysicalAsync().IsCompletedSuccessfully, host?.Installation ?? Guid.Empty,
            host?.Hooks ?? 0, host?.InFlight ?? 0, host?.ReservationHeld == true,
            host?.Failed == true || state.State == PortraitOwnedSceneJointDiagnosticState.Faulted, host?.Recording == true);
    }

    private static Task RetainJointDiagnosticUntilDrained(Task previous, PortraitOwnedSceneJointDiagnostic? joint)
        => PortraitDiagnosticOwnerRetention.DrainAsync(joint, null, previous, joint?.DrainPhysicalAsync() ?? Task.CompletedTask);

    private object? OwnedJointDiagnostics()
    {
        var state = ownedJoint?.Snapshot;
        if (state is null) return null;
        static object SourceReturn(PortraitOwnedSceneJointSourceReturn value) => new
        {
            value.Installation, value.SessionEpoch, value.RequestEpoch, value.MutationEpoch, value.NativeFrame, value.Thread, value.Call,
            source = new { editor = (long)value.Source.Editor, state = (long)value.Source.State, view = (long)value.Source.Model,
                value.Source.Generation, value.Source.Character },
        };
        return new
        {
            state = state.State.ToString(), state.Attempt, state.Reason, state.ExpectedThread,
            safety = ReadOwnedJointSafetyState(), productionAdmission = false,
            context = state.Context is { } context ? new { editor = (long)context.Source.Editor, sourceState = (long)context.Source.State,
                view = (long)context.Source.Model, context.Source.Character, context.Source.Generation, context.SessionEpoch } : null,
            host = state.Host is { } host ? new
            {
                host.Installation, host.SessionEpoch, host.RequestEpoch, host.MutationEpoch, host.CounterDepth, host.ResourceDepth,
                host.Recording, host.Failed, host.Hooks, host.InFlight, host.ReservationHeld, host.PhysicalRetired,
                sourceReturns = host.SourceReturns.Select(SourceReturn),
                observation = new { host.Observation.Observing, stop = host.Observation.Stop.ToString(), host.Observation.Capture,
                    host.Observation.OverwrittenRecords, host.Observation.ObservedThreads,
                    records = host.Observation.Records.Select(row => new { row.Sequence, row.Capture, row.Call, row.ParentCall,
                        phase = row.Phase.ToString(), edge = row.Edge.ToString(), receiver = (long)row.Receiver, row.Thread,
                        row.Depth, row.Timestamp, frameOrigin = row.Frame.Origin.ToString(), nativeFrame = row.Frame.Value,
                        sourceView = (long)row.Frame.SourceView, row.FloatArgument, row.CounterSlot, row.NativeReturn }) },
            } : null,
            census = state.Census is { } census ? new
            {
                census.Installation, sourceReturn = SourceReturn(census.SourceReturn), census.Slots.Complete,
                census.Slots.Phase.NativeFrame, census.Slots.Phase.SessionEpoch, census.Slots.Phase.ManagerGeneration,
                productionAdmission = false,
                rows = census.Slots.Rows.Select(row => new { row.Slot, kind = row.Kind.ToString(), character = (long)row.Character,
                    row.ObjectKind, row.ObjectIndex, row.MemoryIndex, camera = (long)row.RegisteredCamera, row.ActiveCount, row.RenderActive,
                    offscreenCamera = (long)row.RetainedOffscreenCamera, viewCamera = (long)row.RetainedViewCamera }),
            } : null,
        };
    }
}
