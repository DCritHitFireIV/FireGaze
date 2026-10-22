namespace FireGaze.PortraitPreview;

// Physical lifetime evidence only. Reading this model never polls a recorder,
// expires a request, installs/disables a Hook or grants native admission.
internal sealed record PortraitSourceCensusSafetyHost(string State, int Hooks, int InFlight,
    bool ReservationHeld, bool Failed);

internal sealed record PortraitSourceCensusSafetyState(bool Known, string State, long Attempt,
    bool BlocksOperations, bool PhysicalDrainCompleted, PortraitSourceCensusSafetyHost? Source,
    PortraitSourceCensusSafetyHost? Counters, PortraitSourceCensusSafetyHost? Resources)
{
    internal static PortraitSourceCensusSafetyState Unknown { get; } = new(false, "Unknown", 0, true, false, null, null, null);
    public bool ProductionAdmission => false;
}
