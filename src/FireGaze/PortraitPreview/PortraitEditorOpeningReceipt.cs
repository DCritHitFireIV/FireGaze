namespace FireGaze.PortraitPreview;

internal sealed record PortraitEditorOpeningSource(long Address, uint Generation, ulong AccountID, ulong ContentID,
    uint TerritoryID, uint JobID, float X, float Y, float Z, uint CurrentHP,
    bool InCombat, bool IsMoving, bool IsCasting, uint CastActionID);

internal sealed record PortraitEditorOpeningIdentity(long Editor, long State, long Model, uint Generation, ulong Character);

internal sealed record PortraitEditorOpeningSample(bool IsFrameworkThread, int ThreadID, uint Frame,
    long Agent, long State, long View, long StateOwner, long ViewOwner, bool AgentActive,
    int OpenType, int OpenerSourceID, int CurrentGearsetID, PortraitEditorOpeningSource? Source,
    int[] Conditions, bool PreviewActive, bool SaveGuardReady, bool OperationsIdle,
    PortraitEditorOpeningIdentity? ReadyIdentity)
{
    internal PortraitEditorOpeningSample Detached() => this with { Conditions = Conditions.ToArray() };
}

internal sealed record PortraitEditorOpeningReceipt(int SchemaVersion, Guid FeatureInstance, string? Nonce,
    long Serial, long Epoch, string? RequestedEntry, string Status, int ExpectedSourceID,
    double StartedMonotonic, double DeadlineMonotonic, PortraitEditorOpeningSample? Before,
    PortraitEditorOpeningSample? Returned, PortraitEditorOpeningIdentity? ReadyIdentity, string? Reason)
{
    // This establishes only the cause of these native roots. A caller still needs fresh, complete
    // safety evidence for every operation, including cancel. Ownership is not an action capability.
    public bool MayMutate => false;
    public bool MayClose => false;
    internal PortraitEditorOpeningReceipt Detached() => this with { Before = Before?.Detached(), Returned = Returned?.Detached() };
}

/// <summary>
/// Binds only roots created synchronously by this exact native invocation. An empty native return
/// is terminal and can never adopt a later editor. All lifecycle progression is explicit; Read is pure.
/// </summary>
internal sealed class PortraitEditorOpeningController(Func<PortraitEditorOpeningSample?> read, Func<double> monotonicClock)
{
    private const int MaximumAttempts = 128;
    private readonly HashSet<Guid> usedNonces = [];
    private PortraitEditorOpeningReceipt receipt = new(1, Guid.NewGuid(), null, 0, 1, null,
        "NotExecuted", -1, 0, 0, null, null, null, null);
    private bool invoking;

    public PortraitEditorOpeningReceipt Read() => receipt.Detached();

    public bool Open(string kind, string nonce, Action nativeInvoke)
    {
        if (invoking)
        {
            Invalidate("Conflicting nested editor opening.");
            return false;
        }
        if (receipt.Epoch == 0 || receipt.Serial == long.MaxValue)
        { Invalidate("Feature lifetime opening sequence exhausted."); return false; }
        if (usedNonces.Count >= MaximumAttempts)
        { Invalidate("Feature lifetime diagnostic opening capacity exhausted."); return false; }
        if (kind is not ("gearset" or "plate") || !Guid.TryParseExact(nonce, "D", out var token)
            || token == Guid.Empty || !usedNonces.Add(token)) return false;

        invoking = true;
        try { return Execute(kind, token, nativeInvoke); }
        finally { invoking = false; }
    }

    private bool Execute(string kind, Guid token, Action nativeInvoke)
    {
        var started = monotonicClock();
        var epoch = Next(receipt.Epoch);
        receipt = new(1, receipt.FeatureInstance, token.ToString("D"), receipt.Serial + 1, epoch, kind,
            "NotExecuted", -1, started, started + 10, null, null, null, null);
        PortraitEditorOpeningSample? before;
        try { before = read()?.Detached(); }
        catch (Exception exception)
        { Invalidate("Closed native baseline read failed: " + exception.GetType().Name); return false; }
        if (receipt.Epoch != epoch || receipt.Status != "NotExecuted") return false;
        if (!double.IsFinite(started) || epoch == 0 || !Closed(before))
        {
            Invalidate("Closed native baseline unavailable or unsafe.");
            return false;
        }
        var sourceID = kind == "gearset" ? before!.CurrentGearsetID : 0;
        if (kind == "gearset" && sourceID is < 0 or > 99)
        {
            Invalidate("Current gearset identity unavailable.");
            return false;
        }
        receipt = receipt with { Status = "Executing", ExpectedSourceID = sourceID, Before = before };
        try
        {
            nativeInvoke();
            // A reentrant finalize/dispose/conflicting operation must not be overwritten here.
            if (receipt.Epoch != epoch || receipt.Status != "Executing") return false;
            var returned = read()?.Detached();
            if (receipt.Epoch != epoch || receipt.Status != "Executing") return false;
            receipt = receipt with { Returned = returned };
            if (returned is { State: 0, View: 0 })
            {
                receipt = receipt with { Status = "UnprovenReturn", Reason = "Native invocation returned without new roots; later roots will never be adopted." };
                return false;
            }
            if (!Bound(returned, before!, requireIdle: true) || returned!.ThreadID != before!.ThreadID
                || returned.Frame != before.Frame || returned.Source != before.Source)
            {
                Invalidate("Native return did not preserve the invocation's roots, thread, source and entry.");
                return false;
            }
            receipt = receipt with { Status = "ReturnedPending" };
            // Even a synchronous ready identity is only published by explicit lifecycle Update.
            return true;
        }
        catch (Exception exception)
        {
            Invalidate("Native invocation failed: " + exception.GetType().Name);
            return false;
        }
    }

    public void Update()
    {
        if (receipt.Status is not ("ReturnedPending" or "Ready")) return;
        var epoch = receipt.Epoch;
        PortraitEditorOpeningSample? current;
        try { current = read()?.Detached(); }
        catch (Exception exception)
        { Invalidate("Bound native identity read failed: " + exception.GetType().Name); return; }
        if (receipt.Epoch != epoch) return;
        var now = monotonicClock();
        if (!double.IsFinite(now) || now < receipt.StartedMonotonic
            || receipt.Status == "ReturnedPending" && now >= receipt.DeadlineMonotonic)
        { Invalidate("Pending editor creation exceeded its monotonic budget."); return; }
        if (!Bound(current, receipt.Before!, requireIdle: receipt.Status == "ReturnedPending")
            || current!.State != receipt.Returned!.State || current.View != receipt.Returned.View
            || current.Source != receipt.Before!.Source)
        { Invalidate("Bound editor or source ownership changed."); return; }
        if (current.ReadyIdentity is not { } ready)
        {
            if (receipt.Status == "Ready") Invalidate("Previously ready native identity became unavailable.");
            return;
        }
        if (ready.Editor != current.Agent || ready.State != current.State || ready.Model != current.View
            || ready.Generation == 0 || ready.Character == 0 || ready.Character == (ulong)current.Source!.Address
            || receipt.ReadyIdentity is { } old && old != ready || !current.AgentActive)
        { Invalidate("Native ready identity did not match the bound allocation."); return; }
        receipt = receipt with { Status = "Ready", ReadyIdentity = ready };
    }

    public void Invalidate(string reason) => receipt = receipt with
    { Epoch = Next(receipt.Epoch), Status = "Invalidated", Reason = reason };

    private static long Next(long previous) => previous is > 0 and < long.MaxValue ? previous + 1 : 0;

    private static bool Common(PortraitEditorOpeningSample? value, bool opened, bool requireIdle)
        => value is { IsFrameworkThread: true, Agent: > 0, SaveGuardReady: true, Source: { Address: > 0,
            Generation: > 0, AccountID: > 0, ContentID: > 0, CurrentHP: > 0, InCombat: false,
            IsMoving: false, IsCasting: false, CastActionID: 0 } source }
            && value.ThreadID > 0 && float.IsFinite(source.X) && float.IsFinite(source.Y) && float.IsFinite(source.Z)
            && value.Conditions is { Length: 112 } && value.Conditions[1] == 1
            && value.Conditions.Select((flag, index) => index is 1 || opened && index is 100 ? flag is 0 or 1 : flag == 0).All(valid => valid)
            && (!opened || value.Conditions[100] == 1)
            && (!requireIdle || !value.PreviewActive && value.OperationsIdle);

    private static bool Closed(PortraitEditorOpeningSample? value)
        => Common(value, opened: false, requireIdle: true) && value is { State: 0, View: 0, AgentActive: false, ReadyIdentity: null };

    private bool Bound(PortraitEditorOpeningSample? value, PortraitEditorOpeningSample before, bool requireIdle)
        => Common(value, opened: true, requireIdle) && value!.Agent == before.Agent && value.State > 0 && value.View > 0
            && value.State != value.Agent && value.View != value.Agent && value.State != value.View
            // NativePortraitTarget.Resolve's audited contract permits null view.Agent. The state
            // must own this exact editor and the synchronously created view root remains fixed.
            && value.StateOwner == value.Agent && (value.ViewOwner == 0 || value.ViewOwner == value.Agent)
            && value.OpenType == (receipt.RequestedEntry == "gearset" ? 1 : 2)
            && value.OpenerSourceID == receipt.ExpectedSourceID && value.CurrentGearsetID == before.CurrentGearsetID;
}
