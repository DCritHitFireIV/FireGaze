namespace FireGaze.LocalPreview.Core;

/// <summary>Capabilities already qualified by the host. False means the action is unknown, not supported.</summary>
public readonly record struct CompanionInteractionPlaybackCapabilities(
    bool GenderSpecial = false,
    bool BattleEnemy = false,
    bool BattlePlayer = false,
    bool SameSpecies = false);

public enum CompanionInteractionPlaybackKind
{
    GenderSpecial,
    BattleEnemy,
    BattlePlayer,
    SameSpecies,
}

public enum CompanionInteractionPlaybackStopReason
{
    UserCancelled,
    EnteredDuty,
    NativeCommand,
    RealCombat,
    SceneChanged,
    LoggedOut,
    PluginReload,
}

/// <summary>
/// Detached actor identity used by the playback scheduler.  No pointer or native object
/// crosses this boundary; the host must supply a fresh state on every Framework frame.
/// </summary>
public readonly record struct CompanionInteractionPlaybackActor(
    long LeaseId,
    long Generation,
    uint CompanionId,
    int InstanceIndex,
    CompanionDisplayState State,
    CompanionInteractionPlaybackCapabilities Capabilities,
    CompanionBehaviorMode BehaviorMode);

/// <summary>
/// One local-only action request.  Battle labels describe the animation being previewed;
/// they never authorize a combat target or change the game's combat state.
/// </summary>
public readonly record struct CompanionInteractionPlaybackRequest(
    long RequestId,
    long SceneEpoch,
    long LeaseId,
    long Generation,
    uint CompanionId,
    int InstanceIndex,
    CompanionInteractionPlaybackKind Kind,
    CompanionBattleDemoAudience Audience,
    CompanionCharacterGender CharacterGender,
    bool ForcedPreview,
    bool IsLocalOnly,
    bool EntersCombat,
    bool UsesHostileTargetApi);

/// <summary>
/// Framework-only FIFO for interaction previews.  It supplies the future native consumer
/// one validated request per frame and fails closed when the actor is stale or its
/// behavior is unsupported.  It does not attempt to play an animation itself.
/// </summary>
public sealed class CompanionInteractionPlaybackQueue
{
    public const int MaxSameSpeciesInstances = 30;

    private readonly Queue<CompanionInteractionPlaybackRequest> pending = new();
    private readonly HashSet<(long LeaseId, long Generation, int InstanceIndex,
        CompanionInteractionPlaybackKind Kind)> keys = [];
    private long nextRequestId;

    public int PendingCount => pending.Count;
    public CompanionInteractionPlaybackStopReason? LastStopReason { get; private set; }

    public bool TryEnqueueGender(long sceneEpoch, CompanionInteractionPlaybackActor actor,
        CompanionCharacterGender characterGender, out CompanionInteractionPlaybackRequest request,
        out string reason)
    {
        request = default;
        if (!ValidateActor(sceneEpoch, actor, out reason)) return false;
        if (!actor.Capabilities.GenderSpecial)
        {
            reason = "gender interaction capability is unknown";
            return false;
        }
        if (!Enum.IsDefined(characterGender) || characterGender == CompanionCharacterGender.Unknown)
        {
            reason = "character gender is unknown";
            return false;
        }

        request = New(sceneEpoch, actor, CompanionInteractionPlaybackKind.GenderSpecial,
            CompanionBattleDemoAudience.None, characterGender, characterGender != CompanionCharacterGender.Female);
        return Add(request, out reason);
    }

    public bool TryEnqueueBattle(long sceneEpoch, CompanionInteractionPlaybackActor actor,
        CompanionBattleDemoAudience audience, out CompanionInteractionPlaybackRequest request,
        out string reason)
    {
        request = default;
        if (!ValidateActor(sceneEpoch, actor, out reason)) return false;
        var kind = audience switch
        {
            CompanionBattleDemoAudience.Enemy when actor.Capabilities.BattleEnemy
                => CompanionInteractionPlaybackKind.BattleEnemy,
            CompanionBattleDemoAudience.Player when actor.Capabilities.BattlePlayer
                => CompanionInteractionPlaybackKind.BattlePlayer,
            CompanionBattleDemoAudience.Enemy or CompanionBattleDemoAudience.Player
                => CompanionInteractionPlaybackKind.GenderSpecial,
            _ => CompanionInteractionPlaybackKind.GenderSpecial,
        };
        if (kind == CompanionInteractionPlaybackKind.GenderSpecial)
        {
            reason = audience is CompanionBattleDemoAudience.Enemy or CompanionBattleDemoAudience.Player
                ? "battle interaction capability is unknown"
                : "battle audience is invalid";
            return false;
        }

        request = New(sceneEpoch, actor, kind, audience, CompanionCharacterGender.Unknown, false);
        return Add(request, out reason);
    }

    /// <summary>
    /// Adds one manually selected instance.  The caller must provide the instance index;
    /// this prevents a bulk click from silently creating duplicates and keeps the 30-item
    /// limit observable before any native allocation is attempted.
    /// </summary>
    public bool TryEnqueueSameSpecies(long sceneEpoch, CompanionInteractionPlaybackActor actor,
        out CompanionInteractionPlaybackRequest request, out string reason)
    {
        request = default;
        if (!ValidateActor(sceneEpoch, actor, out reason)) return false;
        if (!actor.Capabilities.SameSpecies)
        {
            reason = "same-species interaction capability is unknown";
            return false;
        }
        if (actor.InstanceIndex is < 1 or > MaxSameSpeciesInstances)
        {
            reason = "same-species instance limit reached";
            return false;
        }

        request = New(sceneEpoch, actor, CompanionInteractionPlaybackKind.SameSpecies,
            CompanionBattleDemoAudience.None, CompanionCharacterGender.Unknown, false);
        return Add(request, out reason);
    }

    /// <summary>Returns one still-current request for this Framework frame.</summary>
    public bool TryTakeForFrame(long sceneEpoch,
        IReadOnlyList<CompanionInteractionPlaybackActor> actors,
        out CompanionInteractionPlaybackRequest request)
    {
        ArgumentNullException.ThrowIfNull(actors);
        request = default;
        if (sceneEpoch <= 0) return false;

        var active = new Dictionary<long, CompanionInteractionPlaybackActor>();
        var ambiguous = new HashSet<long>();
        foreach (var actor in actors)
        {
            if (actor.LeaseId <= 0 || actor.Generation <= 0 || actor.CompanionId == 0
                || actor.State != CompanionDisplayState.Active) continue;
            if (!active.TryAdd(actor.LeaseId, actor)) ambiguous.Add(actor.LeaseId);
        }
        foreach (var lease in ambiguous) active.Remove(lease);

        while (pending.Count != 0)
        {
            var candidate = pending.Dequeue();
            keys.Remove(Key(candidate));
            if (candidate.SceneEpoch != sceneEpoch
                || !active.TryGetValue(candidate.LeaseId, out var actor)
                || actor.Generation != candidate.Generation
                || actor.CompanionId != candidate.CompanionId
                || actor.InstanceIndex != candidate.InstanceIndex
                || !CanRun(actor.BehaviorMode)
                || !Supports(actor.Capabilities, candidate.Kind)) continue;
            request = candidate;
            return true;
        }
        return false;
    }

    /// <summary>Cancels all pending local actions at a known lifecycle boundary.</summary>
    public int Cancel(CompanionInteractionPlaybackStopReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        var count = pending.Count;
        pending.Clear();
        keys.Clear();
        LastStopReason = reason;
        return count;
    }

    private bool Add(CompanionInteractionPlaybackRequest request, out string reason)
    {
        if (!keys.Add(Key(request)))
        {
            reason = "the same interaction is already queued";
            return false;
        }
        pending.Enqueue(request);
        reason = string.Empty;
        return true;
    }

    private CompanionInteractionPlaybackRequest New(long sceneEpoch,
        CompanionInteractionPlaybackActor actor, CompanionInteractionPlaybackKind kind,
        CompanionBattleDemoAudience audience, CompanionCharacterGender gender, bool forcedPreview)
        => new(checked(++nextRequestId), sceneEpoch, actor.LeaseId, actor.Generation,
            actor.CompanionId, actor.InstanceIndex, kind, audience, gender, forcedPreview,
            IsLocalOnly: true, EntersCombat: false, UsesHostileTargetApi: false);

    private static (long LeaseId, long Generation, int InstanceIndex, CompanionInteractionPlaybackKind Kind)
        Key(CompanionInteractionPlaybackRequest request)
        => (request.LeaseId, request.Generation, request.InstanceIndex, request.Kind);

    private static bool ValidateActor(long sceneEpoch, CompanionInteractionPlaybackActor actor, out string reason)
    {
        reason = string.Empty;
        if (sceneEpoch <= 0 || actor.LeaseId <= 0 || actor.Generation <= 0 || actor.CompanionId == 0)
        {
            reason = "actor identity is invalid";
            return false;
        }
        if (actor.InstanceIndex is < 1 or > MaxSameSpeciesInstances)
        {
            reason = "instance index is invalid";
            return false;
        }
        if (actor.State != CompanionDisplayState.Active)
        {
            reason = "actor is not active";
            return false;
        }
        if (!CanRun(actor.BehaviorMode))
        {
            reason = actor.BehaviorMode switch
            {
                CompanionBehaviorMode.Suspended => "actor is suspended",
                CompanionBehaviorMode.WaitingAtShore => "water behavior is not qualified",
                CompanionBehaviorMode.Swim or CompanionBehaviorMode.Hover => "water interaction is not qualified",
                _ => "actor behavior is not qualified",
            };
            return false;
        }
        return true;
    }

    private static bool CanRun(CompanionBehaviorMode mode)
        => mode is CompanionBehaviorMode.Idle or CompanionBehaviorMode.Follow or CompanionBehaviorMode.Perched;

    private static bool Supports(CompanionInteractionPlaybackCapabilities capabilities,
        CompanionInteractionPlaybackKind kind)
        => kind switch
        {
            CompanionInteractionPlaybackKind.GenderSpecial => capabilities.GenderSpecial,
            CompanionInteractionPlaybackKind.BattleEnemy => capabilities.BattleEnemy,
            CompanionInteractionPlaybackKind.BattlePlayer => capabilities.BattlePlayer,
            CompanionInteractionPlaybackKind.SameSpecies => capabilities.SameSpecies,
            _ => false,
        };
}
