using FireGaze.PortraitPreview;

internal static class CaptureLeaseCases
{
    public static void Run(Action<bool, string> check)
    {
        var now = TimeSpan.Zero;
        var isolation = new PortraitCaptureIsolation(() => now);
        var historyCalls = 0;
        var liveCalls = 0;
        check(isolation.TryConfigure("history", false, false), "history probe acquires a finite monotonic lease");
        now = TimeSpan.FromSeconds(60) - TimeSpan.FromTicks(1);
        isolation.RunHistory(() => historyCalls++);
        check(isolation.Mode == "history" && historyCalls == 1,
            "history remains enabled immediately before the absolute expiry boundary");
        now = TimeSpan.FromSeconds(60);
        isolation.RunHistory(() => historyCalls++);
        isolation.RunLive(() => liveCalls++);
        check(isolation.Mode == "off" && !isolation.HistoryEnabled && !isolation.LiveEnabled
            && historyCalls == 1 && liveCalls == 0, "exact expiry blocks both new capture channels");
        now = TimeSpan.FromMinutes(14);
        isolation.RunHistory(() => historyCalls++);
        isolation.RunLive(() => liveCalls++);
        check(isolation.Mode == "off" && historyCalls == 1 && liveCalls == 0,
            "failed history probe cannot leave capture enabled fourteen minutes later");

        foreach (var channel in new[] { "history", "live" })
        {
            now = TimeSpan.Zero;
            isolation = new PortraitCaptureIsolation(() => now);
            isolation.TryConfigure(channel, false, false);
            now = TimeSpan.FromMinutes(14);
            var dormantDispatches = 0;
            isolation.RunHistory(() => dormantDispatches++);
            isolation.RunLive(() => dormantDispatches++);
            check(isolation.Mode == "off" && dormantDispatches == 0,
                $"dormant {channel} probe expires before its first observation fourteen minutes later");
        }

        now = TimeSpan.Zero;
        isolation = new PortraitCaptureIsolation(() => now);
        isolation.TryConfigure("live", false, false);
        now = TimeSpan.FromSeconds(40);
        check(isolation.TryConfigure("live", false, false), "same-mode diagnostic command remains idempotent while closed");
        isolation.RunLive(() => liveCalls++);
        now = TimeSpan.FromSeconds(59);
        check(!isolation.TryConfigure("live", true, false)
            && !isolation.TryConfigure("live", false, true)
            && !isolation.TryConfigure("history", true, false)
            && !isolation.TryConfigure("both", false, false),
            "active busy and invalid requests cannot renew an existing diagnostic lease");
        _ = isolation.Mode;
        _ = isolation.LiveEnabled;
        now = TimeSpan.FromSeconds(60);
        isolation.RunLive(() => liveCalls++);
        check(isolation.Mode == "off" && liveCalls == 1,
            "same-mode commands observations and capture callbacks never slide the deadline");
        check(!isolation.TryConfigure("live", true, false)
            && !isolation.TryConfigure("live", false, true) && isolation.Mode == "off",
            "expiry during an active editor cannot be rearmed before physical drain");

        now = TimeSpan.Zero;
        isolation = new PortraitCaptureIsolation(() => now);
        isolation.TryConfigure("history", false, false);
        now = TimeSpan.FromSeconds(40);
        check(isolation.TryConfigure("live", false, false), "closed drained channel switch remains available within a lease");
        now = TimeSpan.FromSeconds(60);
        check(isolation.Mode == "off", "enabled channel switches share the original absolute lease budget");

        now = TimeSpan.FromSeconds(100);
        check(isolation.TryConfigure("history", false, false), "a later closed drained probe may explicitly acquire a fresh lease");
        now = TimeSpan.FromSeconds(125);
        check(isolation.TryConfigure("off", false, false) && isolation.Mode == "off",
            "explicit off clears the remaining lease without scheduling capture");
        check(isolation.TryConfigure("live", false, false), "explicitly enabled live probe starts after off");
        now = TimeSpan.FromSeconds(160);
        check(isolation.LiveEnabled, "off clears the old history deadline before a new probe");
        now = TimeSpan.FromSeconds(185);
        check(isolation.Mode == "off", "fresh probe retains its own exact sixty second boundary");

        now = TimeSpan.Zero;
        isolation = new PortraitCaptureIsolation(() => now);
        isolation.TryConfigure("history", false, false);
        var completion = new TaskCompletionSource<bool>();
        Task<bool>? alreadyStarted = null;
        isolation.RunHistory(() => alreadyStarted = completion.Task);
        now = TimeSpan.FromSeconds(60);
        var expiredDispatches = 0;
        isolation.RunHistory(() => expiredDispatches++);
        completion.SetResult(true);
        check(isolation.Mode == "off" && alreadyStarted?.IsCompletedSuccessfully == true
            && alreadyStarted.Result && expiredDispatches == 0,
            "expiry leaves already started work available for its existing drain path");
    }
}
