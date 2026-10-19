using System.Diagnostics;

namespace FireGaze.PortraitPreview;

internal sealed class PortraitCaptureIsolation
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    private readonly object sync = new();
    private readonly Func<TimeSpan> clock;
    private string mode = "off";
    private TimeSpan? expiresAt;

    public PortraitCaptureIsolation(Func<TimeSpan>? clock = null)
    {
        var origin = Stopwatch.GetTimestamp();
        this.clock = clock ?? (() => Stopwatch.GetElapsedTime(origin));
    }

    // Temporary incident isolation. Do not persist this switch in player preferences.
    // Every plugin load begins without automatic GPU readback; explicitly select one
    // channel for a bounded probe only after the previous editor and tasks have closed.
    public string Mode
    {
        get
        {
            lock (sync) return ReadMode();
        }
    }
    public bool HistoryEnabled => Mode == "history";
    public bool LiveEnabled => Mode == "live";

    public bool TryConfigure(string mode, bool active, bool busy)
    {
        lock (sync)
        {
            _ = ReadMode();
            if (active || busy || mode is not ("off" or "history" or "live")) return false;
            if (mode == "off") expiresAt = null;
            else if (expiresAt is null) expiresAt = clock() + LeaseDuration;
            this.mode = mode;
            return true;
        }
    }

    private string ReadMode()
    {
        if (expiresAt is { } deadline && clock() >= deadline)
        {
            mode = "off";
            expiresAt = null;
        }
        return mode;
    }

    public void RunHistory(Action action) { if (HistoryEnabled) action(); }
    public void RunLive(Action action) { if (LiveEnabled) action(); }
}
