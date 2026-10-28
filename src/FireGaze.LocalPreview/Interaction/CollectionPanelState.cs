namespace FireGaze.LocalPreview.Interaction;

/// <summary>
/// Mutual-exclusion state for the two local-preview collection panels.
/// </summary>
public readonly record struct CollectionPanelState(bool MountVisible, bool PetVisible, bool AccessoryVisible = false)
{
    public CollectionPanelState ToggleMount()
        => MountVisible ? new(false, false, false) : new(true, false, false);

    public CollectionPanelState TogglePet()
        => PetVisible ? new(false, false, false) : new(false, true, false);

    public CollectionPanelState ToggleAccessory()
        => AccessoryVisible ? new(false, false, false) : new(false, false, true);
}
