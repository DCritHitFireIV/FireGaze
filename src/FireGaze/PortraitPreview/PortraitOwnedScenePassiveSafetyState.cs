namespace FireGaze.PortraitPreview;

/// <summary>Detached physical ownership evidence; reading it never advances or retires the controller.</summary>
internal sealed record PortraitOwnedScenePassiveSafetyState(bool Known, string State, long Attempt, long CensusAttempt,
    bool BlocksOperations, bool PhysicalDrainCompleted, int Hooks, int InFlight, bool ReservationHeld, bool Failed)
{
    public bool ProductionAdmission => false;
    internal static PortraitOwnedScenePassiveSafetyState Unknown { get; } = new(false, "Unknown", 0, 0, true, false, 0, 0, true, true);
}
