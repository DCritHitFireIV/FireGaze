using System.Numerics;

namespace FireGaze.LocalPreview.Core;

/// <summary>Detached planner output for observation, never a native request or acknowledgment.</summary>
public readonly record struct CompanionFollowPlanDraft(
    CompanionExecutionKey Key, long PlanRevision, CompanionBehaviorIntent Intent, TimeSpan PlannedAt);

public readonly record struct CompanionNativeChildState(
    CompanionExecutionKey Key, long BindingRevision,
    ushort HostSlot, ushort HostIndex, uint HostGeneration,
    ushort ChildIndex, uint ChildGeneration, ushort CompanionID,
    Vector3 ActualPosition, Vector3 PhysicalHostPosition,
    byte SpawnStateRaw, byte BehaviorStateRaw, byte BehaviorSpecialRaw, bool ModelReady);

public enum CompanionFollowDiagnosticResult
{
    CurrentPair, NoPlan, PendingChild, Retiring, Unavailable,
    BindingChanged, FrameUnavailable, FrameChanged
}

public sealed record CompanionFollowDiagnosticSample(
    CompanionExecutionKey Key, long SampleSequence, uint? NativeFrame,
    CompanionFollowPlanDraft? Plan, CompanionNativeChildState? Child,
    CompanionFollowDiagnosticResult Result)
{
    /// <summary>Optional exact host request metadata; association never proves native causality.</summary>
    public CompanionFollowHostCorrelation? HostCorrelation { get; init; }
    public CompanionFollowCaptureBoundary? Capture { get; init; }
}

public sealed record CompanionFollowHostCorrelation(
    CompanionFollowRequest? Request, CompanionFollowReceipt? Receipt, string Status, string? Reason)
{
    public bool CausalityConfirmed => false;
    // Existing host receipts do not carry a native child binding at dispatch.
    public bool PhysicalPairAtDispatchConfirmed => false;
}

public sealed record CompanionFollowCaptureBoundary(
    uint Territory, long FrameworkFrame, uint? BeforeNativeFrame, uint? AfterNativeFrame,
    TimeSpan CapturedAt)
{
    public string Phase => "after-host-consumer-framework-observation";
}

public sealed record CompanionFollowDiagnosticSnapshot(
    bool Enabled, long DiagnosticEpoch, long PublicationSequence,
    IReadOnlyList<CompanionFollowDiagnosticSample> Samples)
{
    public string Kind => "framework-sample";
    public string SelectionKind { get; init; } = "companion-id";
    public uint? BatchBeforeNativeFrame { get; init; }
    public uint? BatchAfterNativeFrame { get; init; }
    public bool BatchComplete { get; init; }
    public string? BatchFailureReason { get; init; }
    public bool NativeChainObserved => false;
    public bool MutationApplied => false;
}
