namespace FireGaze.LocalPreview.Runtime;

/// <summary>Decides when a local trial bar may be visible. Market-board addons
/// are the one game-owned UI context allowed to keep the bar while ordinary
/// action bars are hidden.</summary>
public static class TrialSpecialHotbarVisibilityPolicy
{
    public static bool ShouldShow(bool trialActive, bool gameUiHidden, bool marketBoardOpen, bool hasSpecialCommands)
        => trialActive && (marketBoardOpen ? hasSpecialCommands : !gameUiHidden);
}
