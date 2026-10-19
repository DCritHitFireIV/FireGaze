using FireGaze.PortraitPreview;

var passed = 0;
var failed = 0;
void Check(bool value, string name)
{
    Console.WriteLine((value ? "PASS " : "FAIL ") + name);
    if (value) passed++; else failed++;
}
var isolation = new PortraitCaptureIsolation();
var historyCalls = 0;
var liveCalls = 0;
isolation.RunHistory(() => historyCalls++);
isolation.RunLive(() => liveCalls++);
Check(historyCalls == 0 && liveCalls == 0, "opening the diagnostic preview cannot start either native capture channel");
Check(isolation.TryConfigure("history", false, false), "history channel can be selected while closed and physically drained");
isolation.RunHistory(() => historyCalls++);
isolation.RunLive(() => liveCalls++);
Check(historyCalls == 1 && liveCalls == 0, "history isolation starts exactly the history operation");
Check(!isolation.TryConfigure("live", true, false), "capture channel cannot change during an active preview");
Check(!isolation.TryConfigure("live", false, true), "capture channel cannot change while old physical work is pending");
Check(!isolation.TryConfigure("both", false, false), "diagnostic isolation refuses concurrent capture channels");
isolation.RunHistory(() => historyCalls++);
Check(historyCalls == 2, "rejected channel changes preserve the selected history channel");
Check(isolation.TryConfigure("live", false, false), "live channel can be selected after physical drain");
isolation.RunHistory(() => historyCalls++);
isolation.RunLive(() => liveCalls++);
Check(historyCalls == 2 && liveCalls == 1, "live isolation starts exactly the live operation");
Check(isolation.TryConfigure("off", false, false), "capture can be disabled after physical drain");
isolation.RunHistory(() => historyCalls++);
isolation.RunLive(() => liveCalls++);
Check(historyCalls == 2 && liveCalls == 1, "disabled capture cannot invoke a retained native callback");
long clock = 0;
var messages = new List<string>();
var trace = new PortraitPhaseTrace(messages.Add, () => clock);
trace.Arm();
var outer = trace.Measure("open.resolve");
Check(trace.ActiveStages.SequenceEqual(["open.resolve"]) && messages.Count == 1,
    "entry is observable before a potentially blocking native operation starts");
using (trace.Measure("open.capture"))
{
    Check(trace.ActiveStages.Length == 2, "nested phase does not overwrite its still running parent");
    clock = 60;
}
Check(trace.ActiveStages.SequenceEqual(["open.resolve"]), "completed nested phase leaves parent visible");
outer.Dispose();
var logged = messages.Count;
outer.Dispose();
Check(trace.ActiveStages.Length == 0 && messages.Count == logged, "duplicate scope disposal cannot double complete a phase");
Check(trace.SlowStages == 2, "both 60 millisecond phases retain their measured slow boundary");
var brokenLogger = new PortraitPhaseTrace(_ => throw new IOException("unavailable log"), () => clock);
try { brokenLogger.Arm(); using var scope = brokenLogger.Measure("draw"); Check(true, "diagnostic logging failure cannot escape into the game loop"); }
catch { Check(false, "diagnostic logging failure cannot escape into the game loop"); }
Console.WriteLine($"PortraitRuntimeSafetyTests: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
