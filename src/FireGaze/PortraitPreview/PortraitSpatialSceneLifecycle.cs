namespace FireGaze.PortraitPreview;

/// <summary>
/// Detached identity for a future portrait-space scene. It contains no native pointer,
/// texture handle, callback or renderer state. Revision and generation are supplied by
/// the qualified portrait editor and must change when that editor is replaced.
/// </summary>
internal readonly record struct PortraitSpatialSceneOwner(Guid Owner, long Revision, uint Generation)
{
    public bool IsValid => Owner != Guid.Empty && Revision > 0 && Generation > 0;
}

internal enum PortraitSpatialSceneState
{
    Off,
    Qualifying,
    Loading,
    Ready,
    Retiring,
    Stopped,
}

/// <summary>
/// Pure managed lifecycle gate for the independent portrait-space experiment.
///
/// This intentionally does not allocate a CharaView, claim a native slot, create a
/// render target, consume input, or invoke a callback. A future native adapter must
/// prove each transition on the Framework thread and use the owner token on every
/// completion. Late callbacks are rejected by the state and token checks.
/// </summary>
internal sealed class PortraitSpatialSceneBroker
{
    private PortraitSpatialSceneOwner activeOwner;

    public PortraitSpatialSceneState State { get; private set; } = PortraitSpatialSceneState.Off;
    public PortraitSpatialSceneOwner ActiveOwner => activeOwner;

    public bool TryQualify(PortraitSpatialSceneOwner owner)
    {
        if (!owner.IsValid || State is not (PortraitSpatialSceneState.Off or PortraitSpatialSceneState.Stopped))
            return false;
        activeOwner = owner;
        State = PortraitSpatialSceneState.Qualifying;
        return true;
    }

    public bool TryBeginLoad(PortraitSpatialSceneOwner owner)
    {
        if (!Matches(owner) || State != PortraitSpatialSceneState.Qualifying) return false;
        State = PortraitSpatialSceneState.Loading;
        return true;
    }

    public bool TryReady(PortraitSpatialSceneOwner owner)
    {
        if (!Matches(owner) || State != PortraitSpatialSceneState.Loading) return false;
        State = PortraitSpatialSceneState.Ready;
        return true;
    }

    public bool TryBeginRetire(PortraitSpatialSceneOwner owner)
    {
        if (!Matches(owner) || State is not (PortraitSpatialSceneState.Qualifying
            or PortraitSpatialSceneState.Loading or PortraitSpatialSceneState.Ready)) return false;
        State = PortraitSpatialSceneState.Retiring;
        return true;
    }

    public bool TryCompleteRetire(PortraitSpatialSceneOwner owner)
    {
        if (!Matches(owner) || State != PortraitSpatialSceneState.Retiring) return false;
        activeOwner = default;
        State = PortraitSpatialSceneState.Off;
        return true;
    }

    /// <summary>
    /// Stops the current session on any lifecycle failure. The old owner is discarded;
    /// a later qualification is an explicit new session and never an automatic resume.
    /// </summary>
    public bool TryStop(PortraitSpatialSceneOwner owner)
    {
        if (!Matches(owner) || State is PortraitSpatialSceneState.Off or PortraitSpatialSceneState.Stopped)
            return false;
        activeOwner = default;
        State = PortraitSpatialSceneState.Stopped;
        return true;
    }

    private bool Matches(PortraitSpatialSceneOwner owner)
        => owner.IsValid && activeOwner == owner;
}

internal enum PortraitSpatialSceneEventKind
{
    Qualify,
    BeginLoad,
    Ready,
    BeginRetire,
    CompleteRetire,
    Stop,
}

internal readonly record struct PortraitSpatialSceneReplayEvent(
    PortraitSpatialSceneEventKind Kind, PortraitSpatialSceneOwner Owner);

internal readonly record struct PortraitSpatialSceneReplayStep(
    PortraitSpatialSceneReplayEvent Event, bool Accepted, PortraitSpatialSceneState State,
    PortraitSpatialSceneOwner ActiveOwner);

internal sealed class PortraitSpatialSceneReplayResult
{
    public PortraitSpatialSceneReplayResult(IReadOnlyList<PortraitSpatialSceneReplayStep> trace)
    {
        Trace = trace;
        AcceptedEvents = trace.Count(step => step.Accepted);
        RejectedEvents = trace.Count - AcceptedEvents;
        FinalState = trace.Count == 0 ? PortraitSpatialSceneState.Off : trace[^1].State;
        ActiveOwner = trace.Count == 0 ? default : trace[^1].ActiveOwner;
    }

    public IReadOnlyList<PortraitSpatialSceneReplayStep> Trace { get; }
    public int AcceptedEvents { get; }
    public int RejectedEvents { get; }
    public PortraitSpatialSceneState FinalState { get; }
    public PortraitSpatialSceneOwner ActiveOwner { get; }
}

/// <summary>
/// Deterministic offline replay for lifecycle diagnostics. It is deliberately separate from
/// any native adapter: replaying a trace can prove state-machine behavior but cannot prove a
/// game callback, render fence or slot ownership.
/// </summary>
internal static class PortraitSpatialSceneReplay
{
    public static PortraitSpatialSceneReplayResult Run(IEnumerable<PortraitSpatialSceneReplayEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var broker = new PortraitSpatialSceneBroker();
        var trace = new List<PortraitSpatialSceneReplayStep>();
        foreach (var item in events)
        {
            var accepted = item.Kind switch
            {
                PortraitSpatialSceneEventKind.Qualify => broker.TryQualify(item.Owner),
                PortraitSpatialSceneEventKind.BeginLoad => broker.TryBeginLoad(item.Owner),
                PortraitSpatialSceneEventKind.Ready => broker.TryReady(item.Owner),
                PortraitSpatialSceneEventKind.BeginRetire => broker.TryBeginRetire(item.Owner),
                PortraitSpatialSceneEventKind.CompleteRetire => broker.TryCompleteRetire(item.Owner),
                PortraitSpatialSceneEventKind.Stop => broker.TryStop(item.Owner),
                _ => false,
            };
            trace.Add(new(item, accepted, broker.State, broker.ActiveOwner));
        }
        return new(trace);
    }
}

/// <summary>
/// A detached layer description for the spatial preview. UVs and dimensions are copied
/// values only. The layer does not own an atlas, SRV, ImGui texture ID or native node.
/// </summary>
internal readonly record struct PortraitSpatialLayer(
    float X, float Y, float Width, float Height, float U0, float V0, float U1, float V1, bool Visible)
{
    public bool IsValid => float.IsFinite(X) && float.IsFinite(Y)
        && float.IsFinite(Width) && float.IsFinite(Height) && Width >= 0 && Height >= 0
        && float.IsFinite(U0) && float.IsFinite(V0) && float.IsFinite(U1) && float.IsFinite(V1)
        && U0 >= 0 && V0 >= 0 && U1 <= 1 && V1 <= 1 && U0 <= U1 && V0 <= V1;
}

/// <summary>
/// Background, frame and decoration are retained as separate camera-space layers.
/// Keeping them separate prevents a captured composite from being mistaken for a
/// transparent character surface in a future renderer.
/// </summary>
internal readonly record struct PortraitSpatialLayerSnapshot(
    PortraitSpatialLayer Background, PortraitSpatialLayer Frame, PortraitSpatialLayer Decoration)
{
    public bool IsValid => Background.IsValid && Frame.IsValid && Decoration.IsValid;
}
