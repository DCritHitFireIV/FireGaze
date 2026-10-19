namespace FireGaze.PortraitPreview;

internal sealed class PortraitCaptureIsolation
{
    // Temporary incident isolation. Do not persist this switch in player preferences.
    // Every plugin load begins without automatic GPU readback; explicitly select one
    // channel for a bounded probe only after the previous editor and tasks have closed.
    public string Mode { get; private set; } = "off";
    public bool HistoryEnabled => Mode == "history";
    public bool LiveEnabled => Mode == "live";

    public bool TryConfigure(string mode, bool active, bool busy)
    {
        if (active || busy || mode is not ("off" or "history" or "live")) return false;
        Mode = mode;
        return true;
    }

    public void RunHistory(Action action) { if (HistoryEnabled) action(); }
    public void RunLive(Action action) { if (LiveEnabled) action(); }
}
