namespace FireGaze.LocalPreview;

/// <summary>Explicit user opt-in for local preview and native chat sentence links.</summary>
internal sealed class LocalPreviewPreferences(Configuration config)
{
    private const int CurrentRevision = 1;
    private int confirmationPending;

    public bool ConfirmationPending => System.Threading.Volatile.Read(ref confirmationPending) != 0;
    public bool CanLinkEmoteMessages => config.LocalPreviewEnabled && config.LocalPreviewChatLinksEnabled;

    public bool Migrate()
    {
        if (config.LocalPreviewSettingsRevision >= CurrentRevision) return false;
        config.LocalPreviewEnabled = false;
        config.LocalPreviewChatLinksEnabled = false;
        config.LocalPreviewItemMenusEnabled = true;
        config.LocalPreviewMountEnabled = true;
        config.LocalPreviewMinionEnabled = true;
        config.LocalPreviewAccessoryEnabled = true;
        config.LocalPreviewEmoteEnabled = true;
        config.LocalPreviewSettingsRevision = CurrentRevision;
        return true;
    }

    public void RequestEnable() => System.Threading.Interlocked.Exchange(ref confirmationPending, 1);

    public bool CompleteEnable(bool accepted)
    {
        var pending = System.Threading.Interlocked.Exchange(ref confirmationPending, 0) != 0;
        if (!pending || !accepted) return false;
        config.LocalPreviewEnabled = true;
        return true;
    }

    public void SetChatLinksEnabled(bool value) => config.LocalPreviewChatLinksEnabled = value;
}
