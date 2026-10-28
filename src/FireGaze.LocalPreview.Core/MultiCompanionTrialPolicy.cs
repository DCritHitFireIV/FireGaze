namespace FireGaze.LocalPreview.Core;

/// <summary>
/// Decides when the optional multi-companion trial may start and which world
/// transitions are allowed to keep it alive. This policy is deliberately free
/// of Dalamud/native types so the cancellation contract can be tested offline.
/// </summary>
public enum MultiCompanionTrialStopReason
{
    UserCancelled,
    EnteredDuty,
    NativeCommand,
    LoggedOut,
    PluginReload,
}

public sealed class MultiCompanionTrialPolicy
{
    public bool Enabled { get; private set; }

    public void SetEnabled(bool enabled) => Enabled = enabled;

    public bool CanRequest => Enabled;

    /// <summary>Normal map changes do not stop a user-enabled local pet group.</summary>
    public bool KeepsAcrossTerritoryChange(bool enteringDuty) => Enabled && !enteringDuty;

    /// <summary>
    /// A normal map load may rebuild the local-player object.  Once the caller has
    /// observed the map-transition boundary, that source replacement is a request to
    /// recreate the enabled group, not a user cancellation.  An unexplained source
    /// replacement still fails closed.
    /// </summary>
    public bool ShouldRebindAfterSourceChange(bool mapTransitionObserved, bool enteringDuty)
        => Enabled && mapTransitionObserved && !enteringDuty;

    public bool ShouldStop(MultiCompanionTrialStopReason reason)
        => reason is MultiCompanionTrialStopReason.UserCancelled
            or MultiCompanionTrialStopReason.EnteredDuty
            or MultiCompanionTrialStopReason.NativeCommand
            or MultiCompanionTrialStopReason.LoggedOut
            or MultiCompanionTrialStopReason.PluginReload;
}
