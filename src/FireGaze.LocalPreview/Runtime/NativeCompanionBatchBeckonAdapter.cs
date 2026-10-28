using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Core;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>Framework/native adapter data only; never publish native addresses to UI.</summary>
public readonly record struct NativeCompanionBeckonBinding(CompanionExecutionKey Key,
    nint Child, nint Source, uint ChildGeneration, bool OrdinaryBeckonQualified = false);

public enum NativeCompanionBatchBeckonStatus
{
    Queued, Pending, QueueNoLongerMatches, NativeRefused, Cancelled, Invalidated, Duplicate, FrameBudgetReached
}

/// <summary>Native queue evidence only. This adapter cannot verify a visible reaction.</summary>
public sealed record NativeCompanionBatchBeckonResult(CompanionBatchInteractionRequest Request,
    NativeCompanionBatchBeckonStatus Status, NativeCompanionBeckonReceipt? Submission = null,
    NativeCompanionBeckonObservation? Observation = null, string? Reason = null)
{
    public bool VisibleReactionVerified => false;
}

/// <summary>
/// Thin Framework-owned bridge from one existing FIFO request to the reviewed native router.
/// The host resolver must verify Active/ready/exact lease and qualify the ordinary reaction
/// from reviewed interaction catalog data and the current native Special; unknown is false.
/// Cancellation revokes this adapter's future processing, never an already submitted native action.
/// </summary>
public sealed class NativeCompanionBatchBeckonAdapter
{
    private readonly Func<bool> frameworkThread;
    private readonly Func<CompanionBatchInteractionRequest, NativeCompanionBeckonBinding?> resolve;
    private readonly Func<NativeCompanionBeckonBinding, Func<bool>, NativeCompanionBeckonReceipt> submit;
    private readonly Func<NativeCompanionBeckonBinding, NativeCompanionBeckonReceipt, Func<bool>, NativeCompanionBeckonObservation> observe;
    private sealed record Pending(CompanionBatchInteractionRequest Request, NativeCompanionBeckonBinding Binding,
        NativeCompanionBeckonReceipt Receipt, long CancellationRevision);
    private readonly Dictionary<long, Pending> pending = [];
    private long sceneEpoch, lastRequestID, lastNativeFrame = -1, cancellationRevision;
    public int PendingCount => pending.Count;

    public NativeCompanionBatchBeckonAdapter(IFramework framework, NativeCompanionTargetEmoteRouter router,
        Func<CompanionBatchInteractionRequest, NativeCompanionBeckonBinding?> resolve)
        : this(() => framework.IsInFrameworkUpdateThread, resolve,
            (binding, scope) => router.SubmitBeckon(binding.Child, binding.Source, scope),
            (binding, receipt, scope) => router.ObserveBeckon(binding.Child, binding.Source, receipt, scope)) { }

    internal NativeCompanionBatchBeckonAdapter(Func<bool> frameworkThread,
        Func<CompanionBatchInteractionRequest, NativeCompanionBeckonBinding?> resolve,
        Func<NativeCompanionBeckonBinding, Func<bool>, NativeCompanionBeckonReceipt> submit,
        Func<NativeCompanionBeckonBinding, NativeCompanionBeckonReceipt, Func<bool>, NativeCompanionBeckonObservation> observe)
    {
        this.frameworkThread = frameworkThread; this.resolve = resolve;
        this.submit = submit; this.observe = observe;
    }

    public bool ResetScene(long epoch)
    {
        if (!frameworkThread() || epoch <= sceneEpoch) return false;
        sceneEpoch = epoch;
        CancelAll();
        return true;
    }

    public int CancelAll()
    {
        if (!frameworkThread()) return 0;
        var count = pending.Count;
        pending.Clear();
        cancellationRevision++;
        return count;
    }

    /// <summary>Stops observing one exact lease, without cancelling a native action or siblings.</summary>
    public int CancelLease(long leaseID, long generation)
    {
        if (!frameworkThread() || leaseID <= 0 || generation <= 0) return 0;
        var ids = pending.Where(x => x.Value.Request.LeaseId == leaseID
            && x.Value.Request.Generation == generation).Select(x => x.Key).ToArray();
        foreach (var id in ids) pending.Remove(id);
        return ids.Length;
    }

    public NativeCompanionBatchBeckonResult Submit(CompanionBatchInteractionRequest request, long frameworkFrame)
    {
        if (!frameworkThread() || request.SceneEpoch != sceneEpoch || sceneEpoch <= 0
            || request.RequestId <= 0 || request.LeaseId <= 0 || request.Generation <= 0
            || request.Kind != CompanionBatchInteractionKind.Beckon)
            return new(request, NativeCompanionBatchBeckonStatus.Cancelled, Reason: "The request is outside the current Framework scene.");
        if (request.RequestId <= lastRequestID)
            return new(request, NativeCompanionBatchBeckonStatus.Duplicate);
        if (frameworkFrame <= lastNativeFrame)
            return new(request, NativeCompanionBatchBeckonStatus.FrameBudgetReached);
        var binding = Resolve(request);
        if (binding is not { } exact || !Matches(request, exact))
            return new(request, NativeCompanionBatchBeckonStatus.Invalidated, Reason: "The exact ready actor could not be resolved.");
        if (!exact.OrdinaryBeckonQualified)
        {
            lastRequestID = request.RequestId;
            return new(request, NativeCompanionBatchBeckonStatus.NativeRefused, Reason: "Ordinary beckon has no reviewed catalog qualification.");
        }
        // Reserve before the callback: reentrant submissions cannot bypass either budget.
        lastRequestID = request.RequestId;
        lastNativeFrame = frameworkFrame;
        var revision = cancellationRevision;
        bool ScopeCurrent() => IsCurrent(request, exact, revision);
        try
        {
            if (!ScopeCurrent()) return new(request, NativeCompanionBatchBeckonStatus.Cancelled);
            var receipt = submit(exact, ScopeCurrent);
            if (!ScopeCurrent()) return new(request, NativeCompanionBatchBeckonStatus.Invalidated, receipt);
            var status = Map(receipt.Status);
            if (receipt.Status == NativeCompanionBeckonSubmitStatus.Queued)
            {
                if (receipt.SourceGeneration != exact.Key.SourceGeneration || receipt.ChildGeneration != exact.ChildGeneration
                    || receipt.Child != exact.Child || receipt.Source != exact.Source)
                    return new(request, NativeCompanionBatchBeckonStatus.Invalidated, receipt, Reason: "Native receipt does not match the admitted binding.");
                pending[request.RequestId] = new(request, exact, receipt, revision);
            }
            return new(request, status, receipt, Reason: receipt.Reason);
        }
        catch (Exception) { return new(request, NativeCompanionBatchBeckonStatus.Invalidated, Reason: "Native adapter completion is unknown."); }
    }

    public NativeCompanionBatchBeckonResult Observe(CompanionBatchInteractionRequest request)
    {
        if (!frameworkThread()) return new(request, NativeCompanionBatchBeckonStatus.Invalidated);
        if (!pending.TryGetValue(request.RequestId, out var entry) || entry.Request != request)
            return new(request, NativeCompanionBatchBeckonStatus.Cancelled, Reason: "This adapter no longer observes the request; native cancellation is not implied.");
        bool ScopeCurrent() => pending.TryGetValue(request.RequestId, out var current)
            && ReferenceEquals(current, entry) && IsCurrent(request, entry.Binding, entry.CancellationRevision)
            && pending.TryGetValue(request.RequestId, out current) && ReferenceEquals(current, entry);
        try
        {
            if (!ScopeCurrent())
            {
                pending.Remove(request.RequestId);
                return new(request, NativeCompanionBatchBeckonStatus.Invalidated, entry.Receipt);
            }
            var observed = observe(entry.Binding, entry.Receipt, ScopeCurrent);
            var status = !ScopeCurrent() ? NativeCompanionBatchBeckonStatus.Invalidated : observed.Status switch
            {
                NativeCompanionBeckonObservationStatus.Pending => NativeCompanionBatchBeckonStatus.Pending,
                NativeCompanionBeckonObservationStatus.QueueNoLongerMatches => NativeCompanionBatchBeckonStatus.QueueNoLongerMatches,
                NativeCompanionBeckonObservationStatus.NotQueued => NativeCompanionBatchBeckonStatus.NativeRefused,
                _ => NativeCompanionBatchBeckonStatus.Invalidated,
            };
            if (status != NativeCompanionBatchBeckonStatus.Pending) pending.Remove(request.RequestId);
            return new(request, status, entry.Receipt, observed, observed.Reason);
        }
        catch (Exception)
        {
            pending.Remove(request.RequestId);
            return new(request, NativeCompanionBatchBeckonStatus.Invalidated, entry.Receipt);
        }
    }

    private NativeCompanionBeckonBinding? Resolve(CompanionBatchInteractionRequest request)
    { try { return resolve(request); } catch { return null; } }
    private bool IsCurrent(CompanionBatchInteractionRequest request, NativeCompanionBeckonBinding binding, long revision)
        => frameworkThread() && sceneEpoch == request.SceneEpoch && revision == cancellationRevision
            && Resolve(request) is { } current && current == binding
            && frameworkThread() && sceneEpoch == request.SceneEpoch && revision == cancellationRevision;
    private static bool Matches(CompanionBatchInteractionRequest request, NativeCompanionBeckonBinding binding)
        => binding.Key.SceneEpoch == request.SceneEpoch && binding.Key.LeaseID == request.LeaseId
            && binding.Key.LeaseGeneration == request.Generation && binding.Key.SourceID != 0
            && binding.Key.SourceGeneration != 0 && binding.ChildGeneration != 0
            && binding.Child != 0 && binding.Source != 0 && binding.Child != binding.Source;
    private static NativeCompanionBatchBeckonStatus Map(NativeCompanionBeckonSubmitStatus status) => status switch
    {
        NativeCompanionBeckonSubmitStatus.Queued => NativeCompanionBatchBeckonStatus.Queued,
        NativeCompanionBeckonSubmitStatus.NativeRefused => NativeCompanionBatchBeckonStatus.NativeRefused,
        NativeCompanionBeckonSubmitStatus.Cancelled => NativeCompanionBatchBeckonStatus.Cancelled,
        _ => NativeCompanionBatchBeckonStatus.Invalidated,
    };
}
