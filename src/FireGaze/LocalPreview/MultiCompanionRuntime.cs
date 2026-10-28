using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Core;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using SheetCompanion = Lumina.Excel.Sheets.Companion;

namespace FireGaze.LocalPreview;

/// <summary>Framework-owned independent companions. Never replaces or hides the player.</summary>
internal sealed unsafe partial class MultiCompanionRuntime : ICompanionActorBackend, IDisposable
{
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly ICondition conditions;
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly CompanionDisplaySession session;
    private readonly Dictionary<long, (long Generation, NativeCompanionDisplayActor Actor)> actors = [];
    private readonly Dictionary<long, CompanionFollowRequest> followRequests = [];
    private CompanionFollowExecutionSession? followExecution;
    private long followSequence;
    private readonly CompanionBehaviorCoordinator behavior = new();
    private readonly CompanionBatchInteractionQueue batchInteractions = new();
    private readonly MultiCompanionTrialPolicy trialPolicy = new();
    private nint ownerAddress;
    private ulong ownerID;
    private uint ownerGeneration;
    private uint territory;
    private long sceneEpoch = 1;
    private long lastTimestamp = Stopwatch.GetTimestamp();
    private long frameworkFrame;
    private long cancellationRevision;
    private bool disposed;
    private bool allowed = true;
    private bool mapTransitionObserved;
    // A targeted player emote is consumed by the behavior coordinator on the
    // next Framework frame. The lease and generation are retained together so
    // a late native callback can never beckon a replacement actor.
    private long? pendingInteractionLeaseID;
    private long pendingInteractionGeneration;
    private readonly Dictionary<uint, ushort> pendingModelByCompanion = [];
    private (long LeaseID, uint CompanionId, ushort ModelID)[] pendingMapRebind = [];
    private int maximumCompanions = 30;
    // A batch summon is intentionally spread over Framework frames. Native actor
    // setup can touch object tables and draw resources, so admitting the whole
    // catalog in one frame would turn a valid "召唤全部" action into a hitch.
    private const int NativeCreationBudgetPerFrame = 2;
    private Vector3 previousOwnerPosition;
    private static readonly CompanionBehaviorCapabilities NativeFollow = new();
    private long lastTargetProbeTimestamp;
    private bool hasTargetProbe;
    public CompanionTargetProbeBatch LastTargetProbe { get; private set; } = new(0, 0, DateTimeOffset.MinValue, "not-requested", []);

    public MultiCompanionRuntime(IFramework framework, IClientState client, ICondition conditions, IDataManager data, IPluginLog log)
    {
        this.framework = framework; this.client = client; this.conditions = conditions; this.data = data; this.log = log;
        session = new(30, this, NativeCreationBudgetPerFrame);
        batchBeckonCatalogMatches = BatchBeckonCatalogMatches;
        batchBeckonFocused = ReadBatchBeckonFocus;
    }

    public CompanionDisplaySnapshot Snapshot => session.Snapshot;
    public uint? SelectedCompanionID => session.SelectedCompanionID;
    public CompanionDisplayLease? SelectedLease => session.SelectedLease;
    public bool HasWorldActors => actors.Values.Any(x => !x.Actor.IsPhysicallyRetired);
    public long Revision => session.Snapshot.Epoch;
    public bool MultiCompanionEnabled => trialPolicy.Enabled;
    public int MaximumCompanions => maximumCompanions;
    public int CapacityPendingCount => session.CapacityPendingCount;
    public int DeferredCreationCount => session.DeferredCreationCount;
    public int PendingBatchInteractionCount => batchInteractions.PendingCount + (deferredBatchBeckon.HasValue ? 1 : 0);
    /// <summary>Changes once when an accepted local companion group is explicitly terminated.</summary>
    public long CancellationRevision => cancellationRevision;
    /// <summary>Map restoration is still holding the prior requested group.</summary>
    public bool HasPendingMapRebind => pendingMapRebind.Length != 0;

    /// <summary>Lowering the request limit preserves pets that are already in the world.</summary>
    public void SetMaximumCompanions(int maximum)
    {
        if (maximum < 0) return;
        session.SetMaximumActors(maximum);
        maximumCompanions = maximum;
    }

    /// <summary>The pet collection owns this explicit opt-in.</summary>
    public void SetMultiCompanionEnabled(bool enabled)
    {
        if (trialPolicy.Enabled == enabled) return;
        trialPolicy.SetEnabled(enabled);
        if (!enabled)
        {
            pendingMapRebind = [];
            pendingModelByCompanion.Clear();
            RecallAll(CompanionDisplayRetireReason.UserCancelled);
        }
    }
    public bool OwnsChild(nint address)
        => address != 0 && actors.Values.Any(x => !x.Actor.IsDisposed && (nint)x.Actor.Companion == address);

    /// <summary>
    /// Admission boundary for targeted player interactions. This method is deliberately
    /// stricter than OwnsChild: the lease must still be Active, its generation must match
    /// the published session and the exact native child must still be owned by its host.
    /// No native action is issued here; the behavior coordinator consumes the detached
    /// request on the following Framework frame. Unknown native perch capabilities remain
    /// disabled until an independently qualified adapter supplies them.
    /// </summary>
    internal bool TryAdmitInteraction(nint childAddress, uint emote)
    {
        if (!framework.IsInFrameworkUpdateThread || disposed || childAddress == 0 || emote == 0 || emote > ushort.MaxValue)
            return false;
        if (!CanRun(out var source) || NativeMountActive(source) || ownerAddress != (nint)source
            || !BindingCurrent(source)) return false;

        foreach (var lease in session.Snapshot.Leases)
        {
            if (lease.State != CompanionDisplayState.Active
                || !actors.TryGetValue(lease.LeaseId, out var entry)
                || entry.Generation != lease.Generation || entry.Actor.IsDisposed || !entry.Actor.IsReady)
                continue;
            var child = entry.Actor.Companion;
            if (child == null || (nint)child != childAddress || !entry.Actor.IsCurrentCompanion(child)) continue;

            pendingInteractionLeaseID = lease.LeaseId;
            pendingInteractionGeneration = lease.Generation;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Queue the first batch action (招手) for exact active leases. No native pointer is
    /// retained; Update releases at most one valid request per Framework frame.
    /// </summary>
    public int QueueBeckon(IEnumerable<CompanionDisplayLease> leases)
    {
        if (!framework.IsInFrameworkUpdateThread || disposed || !trialPolicy.Enabled
            || batchBeckonAdapter is null || !batchBeckonFocused() || !CanRun(out _)) return 0;
        ArgumentNullException.ThrowIfNull(leases);
        var count = 0;
        foreach (var lease in leases)
        {
            if (lease.State != CompanionDisplayState.Active
                || !actors.TryGetValue(lease.LeaseId, out var entry)
                || entry.Generation != lease.Generation || entry.Actor.IsDisposed
                || !entry.Actor.IsReady || entry.Actor.Companion == null
                || !entry.Actor.IsCurrentCompanion(entry.Actor.Companion)) continue;
            if (batchInteractions.EnqueueBeckon(sceneEpoch, lease.LeaseId, lease.Generation)) count++;
        }
        return count;
    }

    public object[] NativeObservations()
        => actors.Select(x =>
        {
            var actor = x.Value.Actor;
            var child = actor.Companion;
            var target = TargetSystem.Instance();
            return (object)new { lease = x.Key, generation = x.Value.Generation, companionID = actor.CompanionId,
                modelID = actor.ExpectedModelId,
                ownerAddress = (ulong)actor.OwnerAddress, childAddress = (ulong)actor.ChildAddress,
                ownerIndex = actor.OwnerObjectIndex, childIndex = actor.CompanionObjectIndex,
                childID = child == null ? 0UL : child->GetGameObjectId().Id,
                selected = child != null && target != null && target->Target == (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)child,
                x = child == null ? 0 : child->Position.X, y = child == null ? 0 : child->Position.Y,
                z = child == null ? 0 : child->Position.Z,
                emoteOwnerIndex = child == null ? ushort.MaxValue : child->EmoteOwnerObjectIndex,
                emoteFromOwner = child != null && child->IsEmoteFromOwner,
                baseOverride = child == null ? (ushort)0 : child->Timeline.BaseOverride,
                timelineID = child == null ? (ushort)0 : child->Timeline.TimelineSequencer.TimelineIds[0],
                ready = actor.IsReady, nativeHidden = actor.IsNativeHidden, phase = actor.DiagnosticPhase,
                failureReason = actor.FailureReason, lifecycle = actor.LifecycleEvidence,
                retiring = actor.IsDisposed, retired = actor.IsPhysicallyRetired };
        }).ToArray();

    private readonly Queue<object> retiredNativeObservations = new();
    public object[] RetiredNativeObservations() => retiredNativeObservations.ToArray();
    public object? LastFailedNativeObservation { get; private set; }

    private void RetainRetiredObservation(CompanionDisplayLease lease, NativeCompanionDisplayActor actor)
    {
        // The session may have marked Failed immediately before requesting teardown.
        // Retain that current detached result rather than the pre-observation lease.
        var current = session.Snapshot.Leases.FirstOrDefault(x => x.LeaseId == lease.LeaseId && x.Generation == lease.Generation);
        if (current.LeaseId != 0) lease = current;
        if (retiredNativeObservations.Count >= 16) retiredNativeObservations.Dequeue();
        var observation = new
        {
            lease = lease.LeaseId, generation = lease.Generation, companionID = actor.CompanionId,
            state = lease.State, phase = actor.DiagnosticPhase, failureReason = actor.FailureReason,
            sessionFailureReason = lease.FailureReason, lifecycle = actor.LifecycleEvidence,
        };
        retiredNativeObservations.Enqueue(observation);
        if (lease.State == CompanionDisplayState.Failed || actor.FailureReason.Length != 0)
            LastFailedNativeObservation = observation;
    }

    /// <summary>Explicit development command only. Diagnostics reads LastTargetProbe without native calls.</summary>
    public string ProbeTargets(uint? companionID = null)
    {
        if (!framework.IsInFrameworkUpdateThread || disposed) return "unavailable";
        var now = Stopwatch.GetTimestamp();
        if (hasTargetProbe && Stopwatch.GetElapsedTime(lastTargetProbeTimestamp, now) < TimeSpan.FromSeconds(1))
            return "rate-limited";
        hasTargetProbe = true; lastTargetProbeTimestamp = now;
        var epoch = sceneEpoch;
        var results = new List<NativeCompanionTargetProbeResult>();
        var state = "captured";
        if (!CanRun(out var source) || (nint)source != ownerAddress
            || *(uint*)((byte*)source + 0x74) != ownerGeneration || territory != client.TerritoryType)
            state = "source-unavailable";
        else
        {
            var sourceAddress = (nint)source;
            var sourceGeneration = ownerGeneration;
            var sourceEntityID = source->EntityId;
            var sourceTerritory = territory;
            // A single explicit request has at most 16 children and one ray per child.
            var leases = session.Snapshot.Leases.Where(x => x.State == CompanionDisplayState.Active
                && (!companionID.HasValue || x.CompanionId == companionID.Value)).OrderBy(x => x.LeaseId).Take(16).ToArray();
            foreach (var lease in leases)
            {
                if (!actors.TryGetValue(lease.LeaseId, out var entry) || entry.Generation != lease.Generation)
                {
                    results.Add(new(lease.LeaseId, lease.Generation, false, "actor-unavailable", null));
                    continue;
                }
                var actor = entry.Actor;
                var child = actor.Companion;
                results.Add(NativeCompanionTargetProbe.Capture(child, (NativeCharacter*)actor.OwnerAddress,
                    lease.LeaseId, lease.Generation, () => ProbeLeaseCurrent(lease, actor, (nint)child, epoch,
                        sourceAddress, sourceGeneration, sourceEntityID, sourceTerritory)));
            }
            if (results.Count == 0) state = "no-active-companion";
        }
        LastTargetProbe = new(LastTargetProbe.Sequence + 1, epoch, DateTimeOffset.UtcNow, state, results.AsReadOnly());
        return state;
    }

    private bool ProbeLeaseCurrent(CompanionDisplayLease lease, NativeCompanionDisplayActor actor, nint child,
        long epoch, nint sourceAddress, uint sourceGeneration, uint sourceEntityID, uint sourceTerritory)
    {
        if (!CanRun(out var source) || sceneEpoch != epoch || (nint)source != sourceAddress || ownerAddress != sourceAddress
            || source->EntityId != sourceEntityID || *(uint*)((byte*)source + 0x74) != sourceGeneration
            || territory != sourceTerritory || client.TerritoryType != sourceTerritory || actor.IsDisposed
            || !actors.TryGetValue(lease.LeaseId, out var entry) || entry.Generation != lease.Generation
            || !ReferenceEquals(entry.Actor, actor)) return false;
        return session.Snapshot.Leases.Any(x => x.LeaseId == lease.LeaseId && x.Generation == lease.Generation
            && x.State == CompanionDisplayState.Active) && actor.IsCurrentCompanion((FFXIVClientStructs.FFXIV.Client.Game.Character.Companion*)child);
    }

    public bool TryRequest(uint id, out string reason)
        => TryRequest(id, allowDuplicate: false, out reason);

    /// <summary>
    /// Retries one exact failed lease after its native actor has physically retired.
    /// This only admits the next-generation Pending lease; it never allocates here.
    /// </summary>
    public bool TryRetryLease(long leaseID, long generation, out CompanionDisplayLease replacement, out string reason)
    {
        replacement = default;
        reason = "";
        if (!framework.IsInFrameworkUpdateThread || disposed || leaseID <= 0 || generation <= 0)
        { reason = "请在游戏更新完成后重试。"; return false; }
        if (!trialPolicy.CanRequest)
        { reason = "请先在“我的宠物”里勾选“启用多宠物试用”。"; return false; }
        if (pendingMapRebind.Length != 0)
        { reason = "正在完成上一批宠物的切换，请稍候重试。"; return false; }
        var current = session.Snapshot.Leases.FirstOrDefault(x => x.LeaseId == leaseID);
        if (current.LeaseId == 0 || current.Generation != generation)
        { reason = "这只宠物的请求已过期，请从当前列表重新选择。"; return false; }
        if (current.State != CompanionDisplayState.Failed)
        { reason = "这只宠物当前不需要重试。"; return false; }
        if (!CanRun(out var source) || !BindingCurrent(source))
        { reason = "请等角色加载完成，结束当前活动后再重试。"; return false; }
        if (NativeMountActive(source))
        {
            RecallAll(CompanionDisplayRetireReason.UserCancelled);
            reason = CompanionMountPolicy.PlayerFacingReason;
            return false;
        }
        if (!session.RetryLease(leaseID, generation))
        { reason = "正在回收这只宠物上一次的模型，请稍候重试。"; return false; }
        replacement = session.Snapshot.Leases.FirstOrDefault(x => x.LeaseId == leaseID);
        if (replacement.LeaseId == 0 || replacement.Generation == generation
            || replacement.State != CompanionDisplayState.Pending)
        {
            replacement = default;
            reason = "这只宠物还在恢复，请稍候重试。";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Requests one companion. The normal directory path is idempotent; callers in the
    /// explicit same-kind interaction mode pass <paramref name="allowDuplicate"/> to
    /// allocate another independent lease for the same companion row.
    /// </summary>
    public bool TryRequest(uint id, bool allowDuplicate, out string reason)
        => TryRequest(id, knownModelID: null, allowDuplicate, restoreExisting: false, out reason);

    /// <summary>
    /// Requests one companion and returns the exact detached lease admitted by the
    /// request. The lease identity is required by the UI when same-species mode has
    /// multiple instances with the same catalog ID.
    /// </summary>
    public bool TryRequest(uint id, bool allowDuplicate, out CompanionDisplayLease lease, out string reason)
    {
        var accepted = TryRequest(id, allowDuplicate, out reason);
        lease = accepted
            ? session.Snapshot.Leases
                .Where(x => x.CompanionId == id)
                .OrderByDescending(x => x.LeaseId)
                .FirstOrDefault()
            : default;
        if (accepted && lease.LeaseId == 0)
        {
            reason = "正在准备这只宠物，请稍候再试。";
            return false;
        }
        return accepted;
    }

    private bool TryRequest(uint id, ushort? knownModelID, out string reason)
        => TryRequest(id, knownModelID, allowDuplicate: false, restoreExisting: false, out reason);

    private bool TryRequest(uint id, ushort? knownModelID, bool allowDuplicate, bool restoreExisting, out string reason)
    {
        reason = "";
        if (!trialPolicy.CanRequest)
        {
            reason = "请先在“我的宠物”里勾选“启用多宠物试用”。";
            return false;
        }
        // A new request must not bind the new source before the old group has
        // drained and its queued restoration has run. Also retain this boundary
        // when the player individually cancels every queued pet during teardown.
        if (pendingMapRebind.Length != 0 || ownerAddress == 0 && HasWorldActors)
        {
            reason = "正在完成上一批宠物的切换，请稍候再召唤。";
            return false;
        }
        if (!CanRun(out var source)) { reason = "请等角色加载完成，结束当前活动后再召唤宠物。"; return false; }
        if (NativeMountActive(source))
        {
            // Independent actors must never be driven while the native player mount
            // owns the rider pose and movement.  Retire any existing actors before
            // rejecting the request so a user cannot enter the mixed state that
            // causes rider/companion jitter on some clients.
            RecallAll(CompanionDisplayRetireReason.UserCancelled);
            reason = CompanionMountPolicy.PlayerFacingReason;
            return false;
        }
        if (id is 0 or > (uint)short.MaxValue)
        { reason = "当前客户端无法显示这只宠物。"; return false; }
        var current = session.Snapshot;
        var existing = current.Leases.Where(x => x.CompanionId == id).ToArray();
        // Keep the ordinary directory path idempotent and report its specific reason
        // before capacity. Explicit duplicate mode still consumes one global slot per
        // requested instance, including instances that are waiting for native creation.
        if (!allowDuplicate)
        {
            var existingLease = existing.FirstOrDefault();
            if (existingLease.LeaseId != 0 && existingLease.State != CompanionDisplayState.Failed)
            {
                reason = existingLease.State switch
                {
                    CompanionDisplayState.Retiring => "这只宠物正在收回，请稍候。",
                    CompanionDisplayState.NativeHidden => "这只宠物暂时不可见，已保留召唤，正在等待恢复。",
                    _ => "这只宠物已经在身边或正在召唤。",
                };
                return false;
            }
        }
        else if (existing.Any(x => x.State == CompanionDisplayState.Retiring))
        {
            // A retiring duplicate still owns its native allocation. Waiting for that
            // exact teardown avoids creating a replacement actor in the same slot.
            reason = "这只宠物正在收回，请稍候。";
            return false;
        }
        if (!restoreExisting && maximumCompanions > 0 && current.Leases.Count >= maximumCompanions)
        { reason = $"当前最多同时试用 {maximumCompanions} 只宠物，请先收回几只。"; return false; }
        var prior = existing.FirstOrDefault();
        if (knownModelID is not { } modelID)
        {
            if (!data.GetExcelSheet<SheetCompanion>().TryGetRow(id, out var row)
                || row.Model.RowId is 0 or > ushort.MaxValue)
            { reason = "当前客户端无法显示这只宠物。"; return false; }
            modelID = (ushort)row.Model.RowId;
        }
        else if (modelID == 0)
        { reason = "当前客户端无法显示这只宠物。"; return false; }
        if (ownerAddress != 0 && !BindingCurrent(source))
        {
            RecallAll(CompanionDisplayRetireReason.Replaced);
            if (HasWorldActors) { reason = "正在回收上一次的宠物，请稍候。"; return false; }
        }
        ownerAddress = (nint)source; ownerID = source->GetGameObjectId();
        ownerGeneration = *(uint*)((byte*)source + 0x74); territory = client.TerritoryType;
        if (prior.LeaseId != 0)
        {
            if (prior.State == CompanionDisplayState.Failed)
            {
                if (session.Retry(id)) return true;
                reason = "正在回收这只宠物上一次的模型，请稍候重试。"; return false;
            }
        }
        CompanionDisplayAdmission admission;
        if (restoreExisting && maximumCompanions > 0 && current.Leases.Count >= maximumCompanions)
        {
            // Lowering the quota does not cancel an already accepted group. Borrow
            // one slot only across this pure managed admission: no backend, native
            // callback or publication runs before the configured quota is restored.
            session.SetMaximumActors(current.Leases.Count + 1);
            try { admission = session.Request(id, allowDuplicate); }
            finally { session.SetMaximumActors(maximumCompanions); }
        }
        else admission = session.Request(id, allowDuplicate);
        if (admission.Accepted)
        {
            pendingModelByCompanion[id] = modelID;
            return true;
        }
        reason = maximumCompanions == 0 ? "暂时无法召唤这只宠物，请稍后重试。"
            : $"当前最多同时试用 {maximumCompanions} 只宠物，请先收回几只。";
        return false;
    }

    public void Update(bool mayRun)
    {
        if (!framework.IsInFrameworkUpdateThread || disposed) return;
        frameworkFrame++;
        allowed = mayRun;
        if (!mayRun)
        {
            InvalidateFollowExecution();
            CancelInteractionDemo();
            RevokeDevelopmentIdleFollow("runtime-suspended");
            InvalidateFollowDiagnostics();
            CancelBatchBeckon("runtime-suspended");
        }
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(lastTimestamp, now); lastTimestamp = now;
        if (!client.IsLoggedIn || conditions[ConditionFlag.LoggingOut])
        {
            ClearPendingInteraction();
            CancelBatchBeckon("runtime-condition-changed");
            pendingMapRebind = [];
            RecallAll(CompanionDisplayRetireReason.Logout);
            return;
        }
        if (conditions[ConditionFlag.BoundByDuty])
        {
            ClearPendingInteraction();
            CancelBatchBeckon("runtime-condition-changed");
            pendingMapRebind = [];
            RecallAll(CompanionDisplayRetireReason.TerritoryChange);
            return;
        }
        // Combat is a terminal boundary for local companions. It is distinct from
        // temporary movement, map loading and other CanRun gates, which must not
        // leave the user with a stale group that silently resumes later.
        if (conditions[ConditionFlag.InCombat])
        {
            RecallAll(CompanionDisplayRetireReason.Combat);
            return;
        }
        // Keep the leases through ordinary map loading. Native actors are not
        // touched while the client is between areas; this prevents a transient
        // territory event from cancelling the user's group.
        if (conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51])
        {
            InvalidateFollowExecution();
            CancelInteractionDemo();
            RevokeDevelopmentIdleFollow("map-loading");
            ClearPendingInteraction();
            CancelBatchBeckon("map-loading");
            InvalidateFollowDiagnostics();
            mapTransitionObserved = true;
            return;
        }
        if (!CanRun(out var source))
        {
            InvalidateFollowExecution();
            CancelInteractionDemo();
            RevokeDevelopmentIdleFollow("player-state-unavailable");
            ClearPendingInteraction();
            CancelBatchBeckon("player-state-unavailable");
            InvalidateFollowDiagnostics();
            return;
        }
        var sourceBindingChanged = ownerAddress != 0 && !BindingCurrent(source, includeTerritory: false);
        var territoryChanged = ownerAddress != 0 && territory != client.TerritoryType;
        if (ownerAddress != 0 && (sourceBindingChanged || territoryChanged))
        {
            // A BetweenAreas boundary can leave the local-player address intact
            // while the territory changes.  Treat that as the same source lease
            // transition as a replacement: the old child hosts must retire before
            // they are recreated for the new territory.
            if ((sourceBindingChanged || territoryChanged)
                && trialPolicy.ShouldRebindAfterSourceChange(mapTransitionObserved, enteringDuty: false))
            {
                BeginMapRebind();
                return;
            }

            if (sourceBindingChanged)
            {
                pendingMapRebind = [];
                RecallAll(CompanionDisplayRetireReason.Replaced);
                return;
            }
        }

        if (ownerAddress == 0 && pendingMapRebind.Length != 0)
        {
            // The old hidden hosts must be physically retired before any new source
            // can own replacement actors.  Retiring leases remain visible in the
            // session until the backend acknowledges that boundary.
            session.Update(elapsed, frameworkFrame);
            if (HasWorldActors) return;
            if (NativeMountActive(source))
            {
                // The old group drained, but a native mount started before
                // restoration. Publish terminal cancellation to the UI.
                RecallAll(CompanionDisplayRetireReason.UserCancelled);
                return;
            }

            ownerAddress = (nint)source;
            ownerID = source->GetGameObjectId();
            ownerGeneration = *(uint*)((byte*)source + 0x74);
            territory = client.TerritoryType;
            var restore = pendingMapRebind;
            pendingMapRebind = [];
            mapTransitionObserved = false;
            foreach (var request in restore)
                TryRequest(request.CompanionId, request.ModelID == 0 ? null : request.ModelID,
                    allowDuplicate: true, restoreExisting: true, out _);
        }

        // Loading is over and the exact source lease is still current. Advance
        // its territory before Observe rechecks the full source+territory binding.
        territory = client.TerritoryType;
        mapTransitionObserved = false;
        if (NativeMountActive(source))
        {
            // A native mount and independently driven companions cannot share the
            // rider movement lane.  Keep this check in the per-frame path so a
            // player mounting after summon is cleaned up without another click.
            RecallAll(CompanionDisplayRetireReason.UserCancelled);
            return;
        }
        var leases = session.Snapshot.Leases;
        var ownerPosition = (Vector3)source->Position;
        long? beckonLease = TakePendingInteraction(leases);
        UpdateBatchBeckon(leases);
        // A native interaction callback may synchronously recall the entire group.
        // Do not construct a new follow context from the now-cleared source lease.
        if (ownerID == 0 || !BindingCurrent(source))
        {
            session.DrainRetired();
            return;
        }
        // Only leases with an owned native actor participate in formation and crowd
        // separation. A catalog-sized summon can contain hundreds of capacity-waiting
        // requests; feeding those unallocated rows to the planner would both place
        // ghosts at the owner position and turn separation into an O(n²) frame spike.
        var inputs = leases.Where(x => (x.State is CompanionDisplayState.Active or CompanionDisplayState.Pending or CompanionDisplayState.NativeHidden)
                && actors.TryGetValue(x.LeaseId, out var entry)
                && entry.Generation == x.Generation
                && !entry.Actor.IsPhysicallyRetired)
            .Select(x => new CompanionBehaviorActor(x.LeaseId, x.Generation, x.CompanionId,
                ReadPosition(x, ownerPosition), NativeFollow)).ToArray();
        var frame = new CompanionBehaviorFrame(sceneEpoch, (float)Math.Min(elapsed.TotalSeconds, .25), ownerPosition,
            source->Rotation, Vector3.DistanceSquared(ownerPosition, previousOwnerPosition) > .0001f,
            conditions[ConditionFlag.Swimming] || conditions[ConditionFlag.Diving], null, beckonLease);
        previousOwnerPosition = ownerPosition;
        followRequests.Clear();
        var intents = behavior.Update(frame, inputs);
        // Formation goals describe the native child, whose Y may already include
        // a shoulder, hover or species offset. Feeding that output back into its
        // hidden host adds the offset again every frame. Only the horizontal goal
        // drives the host; its ground height follows the genuine source.
        var followNow = Stopwatch.GetElapsedTime(0, now);
        var context = new CompanionExecutionContext(sceneEpoch, ownerID, ownerGeneration);
        if (followExecution is null) followExecution = new(context);
        else if (followExecution.Context != context) followExecution.ResetContext(context);
        var followPlans = new List<CompanionExecutionPlan>(intents.Count);
        foreach (var intent in intents)
        {
            // Preserve the complete intent. The executor acknowledges host transform
            // separately while an unqualified native reset remains refused.
            var key = new CompanionExecutionKey(sceneEpoch, ownerID, ownerGeneration, intent.LeaseId, intent.Generation);
            if (intent.Mode is not (CompanionBehaviorMode.Follow or CompanionBehaviorMode.Idle))
            {
                followExecution.Forget(key);
                continue;
            }
            // Keep the interpolated proposal and the stable formation goal distinct.
            // The bounded local crowd correction is applied in goal space; the host
            // still receives the genuine source ground height and the native child
            // remains untouched by this request chain.
            var plan = CompanionExecutionPlan.FromIntent(key, intent,
                intent.SeparationCorrection, intent.MaximumSeparation, ++followSequence,
                followNow + TimeSpan.FromSeconds(1));
            followPlans.Add(plan);
        }
        ReconcileFollowGoalCorrections(followPlans);
        foreach (var plan in followPlans)
        {
            var goal = plan.CorrectedGoal;
            var request = new CompanionFollowRequest(plan.Key, plan.RequestSequence,
                new(goal.X, ownerPosition.Y, goal.Z), source->Rotation,
                plan.ResetMotion, plan.Deadline);
            if (followExecution.TryQueue(request, followNow, out _)) followRequests[plan.Key.LeaseID] = request;
        }
        session.Update(elapsed, frameworkFrame);
        UpdateInteractionDemo();
        UpdateDevelopmentIdleFollow();
        if (FollowDiagnostics.Enabled) PublishFollowDiagnostics(intents, Stopwatch.GetElapsedTime(0, now));
    }

    private static void ReconcileFollowGoalCorrections(List<CompanionExecutionPlan> plans)
    {
        // Proposal-space separation can point inward after formation order reverses.
        // Work with the already-clamped final goals; withdraw conflicting corrections
        // without modifying the stable slot, reclamping, or claiming native clearance.
        // Each changed pass removes at least one correction, so fallback is monotonic.
        bool changed;
        do
        {
            changed = false;
            for (var i = 0; i < plans.Count; i++)
            for (var j = i + 1; j < plans.Count; j++)
            {
                var a = plans[i];
                var b = plans[j];
                var stableDelta = a.FormationGoal - b.FormationGoal;
                stableDelta.Y = 0;
                var correctedDelta = a.CorrectedGoal - b.CorrectedGoal;
                correctedDelta.Y = 0;
                var envelope = MathF.Min(a.MaximumSeparation, b.MaximumSeparation);
                var minimumSquared = MathF.Min(stableDelta.LengthSquared(), envelope * envelope);
                if (correctedDelta.LengthSquared() + .0001f >= minimumSquared) continue;
                if (a.SeparationCorrection != Vector3.Zero)
                {
                    plans[i] = a with { SeparationCorrection = Vector3.Zero };
                    changed = true;
                }
                if (b.SeparationCorrection != Vector3.Zero)
                {
                    plans[j] = b with { SeparationCorrection = Vector3.Zero };
                    changed = true;
                }
            }
        } while (changed);
    }

    private Vector3 ReadPosition(CompanionDisplayLease lease, Vector3 fallback)
        => actors.TryGetValue(lease.LeaseId, out var entry) && entry.Generation == lease.Generation && entry.Actor.Companion != null
            ? (Vector3)entry.Actor.Companion->Position : fallback;

    public bool Recall(uint id)
    {
        if (!framework.IsInFrameworkUpdateThread || disposed) return false;
        if (followDiagnosticCompanion == id || FollowBatchSelectsCompanion(id)) InvalidateFollowDiagnostics();
        var queued = pendingMapRebind.Length;
        pendingMapRebind = pendingMapRebind.Where(x => x.CompanionId != id).ToArray();
        foreach (var lease in session.Snapshot.Leases.Where(x => x.CompanionId == id))
            CancelBatchBeckonLease(lease);
        pendingModelByCompanion.Remove(id);
        var result = session.Recall(id);
        session.DrainRetired();
        return result || pendingMapRebind.Length != queued;
    }

    /// <summary>Recalls one exact duplicate instance without touching its siblings.</summary>
    public bool RecallLease(long leaseID, long? generation = null)
    {
        if (!framework.IsInFrameworkUpdateThread || disposed || leaseID <= 0) return false;
        var current = session.Snapshot.Leases.FirstOrDefault(x => x.LeaseId == leaseID
            && (!generation.HasValue || x.Generation == generation.Value));
        if (current.LeaseId == 0) return false;
        if (FollowBatchSelectsLease(current.LeaseId, current.Generation)) InvalidateFollowDiagnostics();
        var queued = pendingMapRebind.Length;
        pendingMapRebind = pendingMapRebind.Where(x => x.LeaseID != leaseID).ToArray();
        CancelBatchBeckonLease(current);
        var result = session.RecallLease(current.LeaseId, current.Generation);
        session.DrainRetired();
        // Unallocated FIFO requests still hold this source binding. Clearing it
        // after the last physical recall would make Update return before creation.
        if (!HasWorldActors && pendingMapRebind.Length == 0
            && !session.Snapshot.Leases.Any(x => x.State == CompanionDisplayState.Pending))
        {
            ownerAddress = 0; ownerID = 0; ownerGeneration = 0;
        }
        return result || pendingMapRebind.Length != queued;
    }

    public void RecallAll(CompanionDisplayRetireReason reason)
    {
        if (!framework.IsInFrameworkUpdateThread) return;
        CancelInteractionDemo();
        var hadAcceptedGroup = pendingMapRebind.Length != 0
            || session.Snapshot.Leases.Any(x => x.State is CompanionDisplayState.Pending
                or CompanionDisplayState.Active or CompanionDisplayState.NativeHidden
                or CompanionDisplayState.Failed);
        if (hadAcceptedGroup) cancellationRevision = checked(cancellationRevision + 1);
        RevokeDevelopmentIdleFollow("all-companions-retiring");
        ClearPendingInteraction();
        CancelBatchBeckon(reason.ToString());
        InvalidateFollowDiagnostics();
        pendingMapRebind = [];
        pendingModelByCompanion.Clear();
        session.RecallAll(reason); session.DrainRetired();
        followRequests.Clear(); followExecution = null; sceneEpoch++;
        batchBeckonAdapter?.ResetScene(sceneEpoch);
        if (!HasWorldActors) { ownerAddress = 0; ownerID = 0; ownerGeneration = 0; }
    }

    private void BeginMapRebind()
    {
        CancelInteractionDemo();
        RevokeDevelopmentIdleFollow("map-rebind");
        CancelBatchBeckon("map-rebind");
        InvalidateFollowDiagnostics();
        pendingMapRebind = session.Snapshot.Leases
            .Where(x => x.State is CompanionDisplayState.Active or CompanionDisplayState.Pending or CompanionDisplayState.NativeHidden)
            .Select(x => (x.LeaseId, x.CompanionId,
                actors.TryGetValue(x.LeaseId, out var entry) && entry.Generation == x.Generation
                    ? entry.Actor.ExpectedModelId : (ushort)0))
            .ToArray();
        pendingModelByCompanion.Clear();
        foreach (var request in pendingMapRebind)
            if (request.ModelID != 0) pendingModelByCompanion[request.CompanionId] = request.ModelID;
        session.RecallAll(CompanionDisplayRetireReason.TerritoryChange);
        session.DrainRetired();
        followRequests.Clear(); followExecution = null;
        sceneEpoch++;
        batchBeckonAdapter?.ResetScene(sceneEpoch);
        ownerAddress = 0;
        ownerID = 0;
        ownerGeneration = 0;
        territory = 0;
    }

    public bool Select(uint id) => !disposed && framework.IsInFrameworkUpdateThread && session.Select(id);

    public bool SelectLease(long leaseID, long generation)
        => !disposed && framework.IsInFrameworkUpdateThread && session.SelectLease(leaseID, generation);

    private long? TakePendingInteraction(IReadOnlyList<CompanionDisplayLease> leases)
    {
        var leaseID = pendingInteractionLeaseID;
        var generation = pendingInteractionGeneration;
        ClearPendingInteraction();
        if (leaseID is not { } id || generation <= 0) return null;
        var lease = leases.FirstOrDefault(x => x.LeaseId == id);
        return lease is { State: CompanionDisplayState.Active } current && current.Generation == generation
            ? current.LeaseId : null;
    }

    private void ClearPendingInteraction()
    {
        pendingInteractionLeaseID = null;
        pendingInteractionGeneration = 0;
    }

    public bool TryCreate(CompanionDisplayLease lease, out string failure)
    {
        var result = Create(lease);
        failure = string.IsNullOrWhiteSpace(result.Failure)
            ? "宠物模型暂时无法创建，请稍后重试。" : result.Failure;
        return result.Kind == CompanionActorCreateKind.Created;
    }

    /// <summary>
    /// Typed native allocation boundary. A known-full manager is a temporary queue
    /// condition, not a failed pet; only the next Framework frame may retry it. An
    /// unknown census is deferred without crossing the allocator boundary.
    /// </summary>
    public CompanionActorCreateResult Create(CompanionDisplayLease lease)
    {
        const string unavailable = "角色状态暂时不可用。";
        const string full = "当前场景可显示的宠物已满，正在等待空位。";
        if (!CanRun(out var source) || !BindingCurrent(source))
            return CompanionActorCreateResult.Deferred(unavailable);

        var manager = ClientObjectManager.Instance();
        if (manager == null)
            return CompanionActorCreateResult.Deferred("正在读取游戏对象列表。");

        var sourceAddress = (nint)source;
        var sourceGeneration = *(uint*)((byte*)source + 0x74);
        var sourceEntityID = source->EntityId;
        var sourceTerritory = client.TerritoryType;
        if (!NativeCompanionCapacityReader.TryRead(manager, out var capacity))
            return CompanionActorCreateResult.Deferred("正在确认可用的宠物位置。");
        if (capacity.AvailablePairs == 0)
            return CompanionActorCreateResult.CapacityPending(full);

        if (!TryResolveModel(lease.CompanionId, out var modelID, out var modelFailure))
            return CompanionActorCreateResult.Failed(modelFailure);

        // Retiring/failed actors leave motion planning before physical deletion.
        // Keep new births deferred while any such actor still owns its native pair;
        // otherwise a pruned slot (including after RecallAll's epoch change) could
        // be reused beside a child whose exact cleanup is still blocked.
        if (actors.Values.Any(x => x.Actor.IsDisposed && !x.Actor.IsPhysicallyRetired)
            || session.Snapshot.Leases.Any(x => (x.State is CompanionDisplayState.Failed or CompanionDisplayState.Retiring)
                && actors.TryGetValue(x.LeaseId, out var held) && held.Generation == x.Generation
                && !held.Actor.IsPhysicallyRetired))
            return CompanionActorCreateResult.Deferred("正在完成上一只宠物模型的回收。");

        // Reserve from the authoritative planner before SetupCompanion creates its
        // child at the physical host position. All native followers use NativeFollow,
        // so creation and subsequent Update share the same formation spacing.
        long creationEpoch = sceneEpoch;
        int slot = behavior.ReserveFormationSlot(creationEpoch, lease.LeaseId, lease.Generation);
        var anchor = CompanionFormationLayout.GetDestination(slot, source->Position, source->Rotation,
            CompanionFormationLayout.GetSpacing(NativeFollow.BodyRadius));
        var actor = NativeCompanionDisplayActor.Create(source, (ushort)lease.CompanionId,
            modelID, anchor, log, out var nativeStatus);
        if (actor is null)
        {
            behavior.ReleaseUnobservedReservation(creationEpoch, lease.LeaseId, lease.Generation);
            // The census is advisory; the allocator's explicit UINT_MAX result is the
            // only capacity-pending proof. Setup failures after a valid slot remain
            // Failed, so partially allocated actors cannot loop forever as "waiting".
            if (nativeStatus == NativeCompanionAllocationStatus.CapacityPending)
                return CompanionActorCreateResult.CapacityPending(full);
            return CompanionActorCreateResult.Failed("宠物模型暂时无法创建，请稍后重试。");
        }

        // Retain the exact actor before validating the source again. Native setup can
        // observe a source/territory change and may already have requested retirement;
        // if that first retirement is blocked by a transient ownership/target cleanup
        // failure, the session must be able to retry it on a later Framework frame.
        // Dropping this reference here leaks the manager-owned host and makes the
        // capacity slot look permanently occupied.
        actors.Add(lease.LeaseId, (lease.Generation, actor));
        actor.BeforeRetire = () => ClearTarget(actor);

        // A source/territory change during native setup invalidates the actor. Retire
        // it before handing ownership to the session so a stale host cannot leak into
        // the pending queue or be mistaken for the new scene.
        var current = Control.GetLocalPlayer();
        if (current == null || (nint)current != sourceAddress
            || *(uint*)((byte*)current + 0x74) != sourceGeneration
            || current->EntityId != sourceEntityID || client.TerritoryType != sourceTerritory
            || !BindingCurrent(source) || actor.IsDisposed)
        {
            if (!actor.TryRetire())
                return CompanionActorCreateResult.Failed("正在回收切换场景中的宠物模型。");
            RetainRetiredObservation(lease, actor);
            actors.Remove(lease.LeaseId);
            behavior.ReleaseUnobservedReservation(creationEpoch, lease.LeaseId, lease.Generation);
            ForgetFollowExecution(lease);
            return CompanionActorCreateResult.Deferred("角色状态在召唤期间发生变化。");
        }
        return CompanionActorCreateResult.Created;
    }

    private bool TryResolveModel(uint companionID, out ushort modelID, out string failure)
    {
        modelID = 0;
        failure = "当前客户端无法显示这只宠物。";
        if (pendingModelByCompanion.TryGetValue(companionID, out modelID))
            return modelID != 0;
        if (!data.GetExcelSheet<SheetCompanion>().TryGetRow(companionID, out var row)
            || row.Model.RowId is 0 or > ushort.MaxValue) return false;
        modelID = (ushort)row.Model.RowId;
        return true;
    }

    public CompanionActorReadiness Observe(CompanionDisplayLease lease, out string failure)
    {
        failure = "宠物模型已失效，请稍后重试。";
        if (!CanRun(out var source) || !BindingCurrent(source)
            || !actors.TryGetValue(lease.LeaseId, out var entry) || entry.Generation != lease.Generation)
            return CompanionActorReadiness.Lost;
        if (followRequests.TryGetValue(lease.LeaseId, out var plan)
            && plan.Key.LeaseGeneration == lease.Generation && followExecution is { } execution
            && execution.TryTakeRequest(plan.Key, plan.Sequence, Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()), out var request))
        {
            var actor = entry.Actor;
            bool ScopeCurrent() => CanRun(out var current) && current == source && BindingCurrent(current) && !NativeMountActive(current)
                && plan.Key == new CompanionExecutionKey(sceneEpoch, ownerID, ownerGeneration, lease.LeaseId, lease.Generation)
                && actors.TryGetValue(lease.LeaseId, out var owned) && owned.Generation == lease.Generation
                && ReferenceEquals(owned.Actor, actor) && !actor.IsDisposed
                && session.IsCurrentLiveLease(lease);
            var now = Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());
            var receipt = actor.ExecuteFollow(request, source, now, ScopeCurrent);
            execution.Observe(receipt, now);
            actor.Observe(source);
        }
        else entry.Actor.Observe(source);
        if (entry.Actor.IsDisposed) return CompanionActorReadiness.Lost;
        failure = "";
        return entry.Actor.IsReady ? CompanionActorReadiness.Ready
            : entry.Actor.IsNativeHidden ? CompanionActorReadiness.NativeHidden : CompanionActorReadiness.Pending;
    }

    public bool TryRetire(CompanionDisplayLease lease, out string failure)
    {
        failure = "";
        if (!actors.TryGetValue(lease.LeaseId, out var entry)) return true;
        if (entry.Generation != lease.Generation) { failure = "宠物模型代次已改变。"; return false; }
        if (!entry.Actor.TryRetire()) { failure = "正在回收宠物模型。"; return false; }
        ForgetDevelopmentIdleFollow(lease);
        RetainRetiredObservation(lease, entry.Actor);
        actors.Remove(lease.LeaseId); ForgetFollowExecution(lease); return true;
    }

    private void ForgetFollowExecution(CompanionDisplayLease lease)
    {
        followRequests.Remove(lease.LeaseId);
        followExecution?.Forget(new(sceneEpoch, ownerID, ownerGeneration, lease.LeaseId, lease.Generation));
    }

    private void InvalidateFollowExecution()
    {
        followRequests.Clear(); followExecution = null;
    }

    internal System.Text.Json.JsonElement ReadFollowExecutionJSON()
        => System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            ProductionAdmission = false,
            ChildMovementConfirmed = false,
            Samples = (followExecution?.Snapshot ?? []).Select(x => new
            {
                x.Request, x.State, x.RequestDispatched, x.Receipt, x.FailureReason,
            }).ToArray(),
        }, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });

    public CompanionDisplayLease? ObserveSelected()
    {
        if (!CanRun(out var source) || !BindingCurrent(source)
            || TargetSystem.Addresses.Instance.Value == 0) return null;
        var target = TargetSystem.Instance();
        if (target == null || (nint)target != TargetSystem.Addresses.Instance.Value) return null;
        var selected = target->Target;
        if (selected == null) return null;
        foreach (var lease in session.Snapshot.Leases)
        {
            if (lease.State != CompanionDisplayState.Active
                || !actors.TryGetValue(lease.LeaseId, out var entry) || entry.Generation != lease.Generation) continue;
            var actor = entry.Actor;
            var child = actor.Companion;
            if (child == null || !actor.IsReady || actor.IsDisposed
                || (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)child != selected) continue;
            // All checks after the source binding are raw reads. Never call a target setter
            // or touch another native actor while reconciling this detached UI identity.
            if ((nint)target == TargetSystem.Addresses.Instance.Value && target->Target == selected
                && actor.IsCurrentCompanion(child)) return lease;
        }
        return null;
    }

    public bool TrySelect(CompanionDisplayLease lease)
    {
        if (!CanRun(out var source) || !BindingCurrent(source)
            || !actors.TryGetValue(lease.LeaseId, out var entry) || entry.Generation != lease.Generation) return false;
        var actor = entry.Actor;
        return actor.IsReady && !actor.IsDisposed
            && NativeCompanionTarget.TrySelect(actor.Companion, () => !disposed && actor.IsReady && actor.IsCurrentCompanion(actor.Companion));
    }

    private static bool ClearTarget(NativeCompanionDisplayActor actor)
    {
        var child = actor.Companion;
        return child == null || NativeCompanionTarget.ClearReferencesBeforeRetire(child, () => actor.IsCurrentCompanion(child));
    }

    private bool CanRun(out NativeCharacter* source)
    {
        source = null;
        if (disposed || !allowed || !framework.IsInFrameworkUpdateThread || !client.IsLoggedIn
            || conditions[ConditionFlag.LoggingOut] || conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51]
            || conditions[ConditionFlag.InCombat] || conditions[ConditionFlag.Unconscious] || conditions[ConditionFlag.Crafting]
            || conditions[ConditionFlag.PreparingToCraft] || conditions[ConditionFlag.WatchingCutscene]
            || conditions[ConditionFlag.OccupiedInCutSceneEvent]) return false;
        source = (NativeCharacter*)Control.GetLocalPlayer(); return source != null;
    }

    private static bool NativeMountActive(NativeCharacter* source)
        => source != null && CompanionMountPolicy.IsNativeMountActive(source->Mount.MountId, source->Mount.MountObject != null);

    private bool BindingCurrent(NativeCharacter* source, bool includeTerritory = true)
        => source != null && (nint)source == ownerAddress && source->GetGameObjectId().Id == ownerID
            && *(uint*)((byte*)source + 0x74) == ownerGeneration
            && (!includeTerritory || territory == client.TerritoryType);

    public void Dispose()
    {
        if (disposed || !framework.IsInFrameworkUpdateThread) return;
        RecallAll(CompanionDisplayRetireReason.PluginReload);
        disposed = true;
        // A retained Framework callback pins this cleanup owner until native retirement is
        // acknowledged. It cannot create, draw, target or resurrect a retired pet.
        if (HasWorldActors) framework.Update += DrainRetired;
    }

    private void DrainRetired(IFramework _)
    {
        session.DrainRetired();
        if (!HasWorldActors) framework.Update -= DrainRetired;
    }
}
