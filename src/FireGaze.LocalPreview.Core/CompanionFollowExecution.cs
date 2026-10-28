using System.Numerics;

namespace FireGaze.LocalPreview.Core;

/// <summary>Applied acknowledges the hidden host request, never observed child movement.</summary>
public enum CompanionFollowExecutionState { Pending, Submitted, Applied, ResetApplied, Refused, Failed, Invalidated }
/// <summary>One exact, detached ordinary follow request. No native address escapes the adapter.</summary>
public readonly record struct CompanionFollowRequest(CompanionExecutionKey Key, long Sequence,
    Vector3 TargetPosition, float Rotation, bool ResetMotion, TimeSpan Deadline);
public readonly record struct CompanionFollowReceipt(CompanionExecutionKey Key, long Sequence,
    long ObservationSequence, TimeSpan ObservedAt, CompanionFollowExecutionState State,
    Vector3 ActualHostPosition, bool BindingCurrent, bool ResetApplied, string? FailureReason,
    bool HostTransformApplied = false);
public sealed record CompanionFollowExecutionSnapshot(CompanionFollowRequest Request,
    CompanionFollowExecutionState State, bool RequestDispatched, CompanionFollowReceipt? Receipt, string? FailureReason);

/// <summary>Framework-owned request and acknowledgement state; failures are isolated by exact lease.</summary>
public sealed class CompanionFollowExecutionSession
{
    private sealed class Entry(CompanionFollowRequest request, TimeSpan now)
    {
        public CompanionFollowRequest Request = request;
        public CompanionFollowExecutionState State = CompanionFollowExecutionState.Pending;
        public bool RequestDispatched;
        public TimeSpan CreatedAt = now;
        public TimeSpan SubmittedAt;
        public CompanionFollowReceipt? Receipt;
        public string? FailureReason;
        public CompanionFollowExecutionSnapshot View() => new(Request, State, RequestDispatched, Receipt, FailureReason);
    }
    private readonly Dictionary<CompanionExecutionKey, Entry> entries = [];
    private readonly HashSet<CompanionExecutionContext> invalidatedContexts = [];
    private CompanionExecutionContext context;
    private TimeSpan lastTime;

    public CompanionFollowExecutionSession(CompanionExecutionContext context)
    {
        if (!CompanionExecutionPlan.ValidContext(context)) throw new ArgumentException("Invalid follow context.", nameof(context));
        this.context = context;
    }
    public CompanionExecutionContext Context => context;
    public IReadOnlyList<CompanionFollowExecutionSnapshot> Snapshot
        => Array.AsReadOnly(entries.Values.OrderBy(x => x.Request.Key.LeaseID)
            .ThenBy(x => x.Request.Key.LeaseGeneration).Select(x => x.View()).ToArray());
    public bool TryQueue(CompanionFollowRequest request, TimeSpan now, out string reason)
    {
        reason = "follow-request-invalid";
        if (!TimeCurrent(now) || !CompanionExecutionPlan.ValidKey(request.Key) || request.Key.Context != context
            || request.Sequence <= 0 || request.Deadline <= now || !CompanionExecutionPlan.Finite(request.TargetPosition)
            || !float.IsFinite(request.Rotation)) return false;
        if (entries.TryGetValue(request.Key, out var prior)
            && (request.Sequence <= prior.Request.Sequence || prior.State == CompanionFollowExecutionState.Submitted))
        { reason = "follow-request-stale-or-outstanding"; return false; }
        lastTime = now;
        entries[request.Key] = new Entry(request, now);
        reason = "";
        return true;
    }
    public bool TryTakeRequest(CompanionExecutionKey key, long sequence, TimeSpan now,
        out CompanionFollowRequest request)
    {
        request = default;
        if (!TimeCurrent(now) || key.Context != context || !entries.TryGetValue(key, out var entry)
            || entry.Request.Sequence != sequence || entry.State != CompanionFollowExecutionState.Pending
            || entry.RequestDispatched || entry.Request.Deadline <= now) return false;
        lastTime = now;
        entry.RequestDispatched = true;
        entry.SubmittedAt = now;
        entry.State = CompanionFollowExecutionState.Submitted;
        request = entry.Request;
        return true;
    }
    public bool Observe(CompanionFollowReceipt receipt, TimeSpan now)
    {
        if (!TimeCurrent(now) || receipt.Key.Context != context || !entries.TryGetValue(receipt.Key, out var entry)
            || entry.Request.Sequence != receipt.Sequence || !entry.RequestDispatched
            || receipt.ObservationSequence <= (entry.Receipt?.ObservationSequence ?? 0)
            || receipt.ObservedAt < entry.SubmittedAt || receipt.ObservedAt > now
            || receipt.ObservedAt < (entry.Receipt?.ObservedAt ?? TimeSpan.Zero)
            || !CompanionExecutionPlan.Finite(receipt.ActualHostPosition)
            || receipt.State is not (CompanionFollowExecutionState.Applied or CompanionFollowExecutionState.ResetApplied
                or CompanionFollowExecutionState.Refused or CompanionFollowExecutionState.Failed
                or CompanionFollowExecutionState.Invalidated)) return false;
        lastTime = now;
        entry.Receipt = receipt;
        entry.State = receipt.State;
        entry.FailureReason = receipt.FailureReason;
        bool claimsApplied = receipt.State is CompanionFollowExecutionState.Applied or CompanionFollowExecutionState.ResetApplied;
        if (!receipt.BindingCurrent || claimsApplied && (!receipt.HostTransformApplied
            || entry.Request.ResetMotion && !receipt.ResetApplied))
        {
            entry.State = CompanionFollowExecutionState.Failed;
            entry.FailureReason ??= !receipt.BindingCurrent ? "follow-binding-changed"
                : !receipt.HostTransformApplied ? "follow-host-transform-unconfirmed" : "native-motion-reset-unconfirmed";
        }
        return true;
    }
    public bool ResetContext(CompanionExecutionContext next)
    {
        if (!CompanionExecutionPlan.ValidContext(next) || next == context || next.SceneEpoch < context.SceneEpoch
            || invalidatedContexts.Contains(next) || next.SceneEpoch == context.SceneEpoch
                && next.SourceID == context.SourceID && next.SourceGeneration <= context.SourceGeneration) return false;
        invalidatedContexts.Add(context);
        entries.Clear();
        context = next;
        return true;
    }
    public void Forget(CompanionExecutionKey key) => entries.Remove(key);
    private bool TimeCurrent(TimeSpan now) => now >= TimeSpan.Zero && now >= lastTime;
}
