namespace FireGaze.LocalPreview.Core;

/// <summary>
/// Offline proof flags for the future CharacterBase/Scene companion backend.
/// These flags describe evidence only; they never grant a native allocation.
/// </summary>
public readonly record struct CompanionIndependentSceneCapability(
    bool AbiVerified,
    bool VersionBound,
    bool SceneRootVerified,
    bool DrawVerified,
    bool SelectionVerified,
    bool RetirementVerified)
{
    public static CompanionIndependentSceneCapability None { get; } = new();

    public bool IsComplete
        => AbiVerified && VersionBound && SceneRootVerified && DrawVerified
            && SelectionVerified && RetirementVerified;

    /// <summary>
    /// The independent scene path is intentionally a development contract only.
    /// This remains hard false until the complete offline and CN runtime gates pass.
    /// </summary>
    public bool ProductionAdmission => false;

    public IReadOnlyList<string> MissingProofs
    {
        get
        {
            var missing = new List<string>(6);
            if (!AbiVerified) missing.Add("ABI");
            if (!VersionBound) missing.Add("客户端版本绑定");
            if (!SceneRootVerified) missing.Add("独立场景根");
            if (!DrawVerified) missing.Add("真实绘制");
            if (!SelectionVerified) missing.Add("目标选择");
            if (!RetirementVerified) missing.Add("安全回收");
            return missing;
        }
    }
}

public enum CompanionIndependentSceneRequestState
{
    CapabilityPending,
}

/// <summary>Detached request accepted by the future independent-scene backend.</summary>
public readonly record struct CompanionIndependentSceneRequest(
    long RequestId,
    uint CompanionId,
    long Generation,
    CompanionIndependentSceneRequestState State);

public readonly record struct CompanionIndependentSceneAdmission(
    bool Accepted,
    long RequestId,
    long Generation,
    string Reason)
{
    public static CompanionIndependentSceneAdmission Rejected(string reason)
        => new(false, 0, 0, reason);
}

public sealed class CompanionIndependentSceneSnapshot
{
    internal CompanionIndependentSceneSnapshot(
        CompanionIndependentSceneCapability capability,
        IEnumerable<CompanionIndependentSceneRequest> requests)
    {
        Capability = capability;
        Requests = Array.AsReadOnly(requests.ToArray());
    }

    public CompanionIndependentSceneCapability Capability { get; }
    public IReadOnlyList<CompanionIndependentSceneRequest> Requests { get; }
    public int PendingCount => Requests.Count;
    public int ActiveCount => 0;
    public bool ProductionAdmission => Capability.ProductionAdmission;
}

/// <summary>
/// The smallest safe seam for a future independent CharacterBase/Scene backend.
/// It stores detached requests and a capability gate, but has no draw, target,
/// or native pointer implementation. Requests cannot be reported active here.
/// </summary>
public sealed class CompanionIndependentSceneBoundary
{
    private readonly Dictionary<long, CompanionIndependentSceneRequest> requests = [];
    private readonly int maximumPending;
    private long nextRequestId;
    private long nextGeneration;

    public CompanionIndependentSceneBoundary(
        CompanionIndependentSceneCapability capability,
        int maximumPending = int.MaxValue)
    {
        if (maximumPending < 1) throw new ArgumentOutOfRangeException(nameof(maximumPending));
        Capability = capability;
        this.maximumPending = maximumPending;
    }

    public CompanionIndependentSceneCapability Capability { get; }

    /// <summary>Hard false by design; complete evidence alone is not live validation.</summary>
    public bool ProductionAdmission => false;

    public CompanionIndependentSceneSnapshot Snapshot
        => new(Capability, requests.Values.OrderBy(x => x.RequestId));

    public CompanionIndependentSceneAdmission Request(uint companionId)
    {
        if (companionId == 0)
            return CompanionIndependentSceneAdmission.Rejected("companion id must be positive");
        if (requests.Count >= maximumPending)
            return CompanionIndependentSceneAdmission.Rejected("independent scene request queue is full");

        var request = new CompanionIndependentSceneRequest(
            checked(++nextRequestId), companionId, checked(++nextGeneration),
            CompanionIndependentSceneRequestState.CapabilityPending);
        requests.Add(request.RequestId, request);
        return new(true, request.RequestId, request.Generation, "等待独立场景能力证明。");
    }

    /// <summary>
    /// The create boundary intentionally remains closed. It preserves requests so a
    /// later implementation can consume them only after a separately reviewed gate.
    /// </summary>
    public bool TryTakeForCreate(
        out CompanionIndependentSceneRequest request,
        out string reason)
    {
        request = default;
        reason = Capability.IsComplete
            ? "独立场景仍未通过生产准入。"
            : $"独立场景能力尚未完成：{string.Join("、", Capability.MissingProofs)}。";
        return false;
    }

    /// <summary>Removes one exact detached request; stale generations cannot cancel a replacement.</summary>
    public bool Cancel(long requestId, long generation)
    {
        if (!requests.TryGetValue(requestId, out var request) || request.Generation != generation)
            return false;
        requests.Remove(requestId);
        return true;
    }
}
