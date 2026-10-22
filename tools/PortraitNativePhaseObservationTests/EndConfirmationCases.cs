using System.Reflection;
using FireGaze.PortraitPreview;

internal static class EndConfirmationCases
{
    internal static void Run(Action<bool, string> check, Action<string, Action> run)
    {
        run("detached active capture is revoked and cannot ABA", () => {
            var property = typeof(PortraitNativePhaseObservation).GetProperty("ActiveCapture", BindingFlags.Instance | BindingFlags.NonPublic);
            check(property is not null, "recorder exposes immutable lock-free active capture identity");
            if (property is null) return;
            long now = 0; var clockCalls = 0;
            using var recorder = new PortraitNativePhaseObservation(timestamp: () => { clockCalls++; return now; }, timestampFrequency: 1000);
            check(property.GetValue(recorder) is null, "inactive recorder has no active capture identity");
            recorder.TryStart(TimeSpan.FromSeconds(1)); var first = property.GetValue(recorder);
            check(first is not null, "successful recorder start publishes one active capture identity");
            var gate = typeof(PortraitNativePhaseObservation).GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recorder)!;
            var before = clockCalls; Task<object?> read;
            bool completed;
            lock (gate) { read = Task.Run(() => property.GetValue(recorder)); completed = read.Wait(TimeSpan.FromSeconds(1)); }
            check(completed && ReferenceEquals(read.GetAwaiter().GetResult(), first) && before == clockCalls,
                "active capture identity reads take no recorder lock or external clock delegate");
            recorder.Stop(); check(property.GetValue(recorder) is null, "Stop revokes the old active capture identity");
            recorder.TryStart(TimeSpan.FromSeconds(1)); var second = property.GetValue(recorder);
            check(second is not null && !ReferenceEquals(first, second), "Stop/Start cannot restore the previous active capture instance");
            now = 1000; recorder.Snapshot();
            check(property.GetValue(recorder) is null, "observed recorder expiry revokes active capture identity");
        });
        var method = typeof(PortraitNativePhaseObservation).GetMethod("TryEnd", BindingFlags.Instance | BindingFlags.NonPublic);
        bool End(PortraitNativePhaseObservation recorder, PortraitNativePhaseCall call, bool returned)
            => method is not null && (bool)method.Invoke(recorder, [call, returned, null])!;
        run("exact original return confirmation", () => {
            long now = 0; var thread = new PortraitNativeThreadStamp(17, 23);
            using var recorder = new PortraitNativePhaseObservation(timestamp: () => now, timestampFrequency: 1000, threadStamp: () => thread);
            check(recorder.TryStart(TimeSpan.FromSeconds(1)), "return confirmation capture starts");
            var call = recorder.Begin(PortraitNativeObservedPhase.SourceCharaViewRender, 0x1234, 0);
            check(End(recorder, call, true), "only actual successful LIFO completion confirms the source return");
            check(!End(recorder, call, true), "a completion cannot be replayed as a second read window");
        });
        run("throw confirmation preserves evidence but rejects admission", () => {
            using var recorder = new PortraitNativePhaseObservation(timestamp: () => 0, timestampFrequency: 1000);
            recorder.TryStart(TimeSpan.FromSeconds(1)); var call = recorder.Begin(PortraitNativeObservedPhase.SourceCharaViewRender, 9, 7);
            check(!End(recorder, call, false), "an Original throw cannot confirm a successful source return");
            check(recorder.Snapshot().Records.Select(row => row.Edge).SequenceEqual(new[] { PortraitNativeObservedEdge.Enter, PortraitNativeObservedEdge.Throw }),
                "TryEnd retains the exact throw edge without granting return admission");
        });
        run("return confirmation rejects capture lifecycle and clocks", () => {
            foreach (var mode in new[] { "stop", "restart", "expiry", "rollback", "thread", "nesting", "zero", "clock-throw" }) {
                long now = 10; var throws = false; var thread = new PortraitNativeThreadStamp(8, 9);
                using var recorder = new PortraitNativePhaseObservation(timestamp: () => throws ? throw new IOException() : now,
                    timestampFrequency: 1000, threadStamp: () => thread);
                recorder.TryStart(TimeSpan.FromSeconds(1)); var call = recorder.Begin(PortraitNativeObservedPhase.SourceCharaViewRender, 3, 1);
                switch (mode) {
                    case "stop": recorder.Stop(); break;
                    case "restart": recorder.Stop(); recorder.TryStart(TimeSpan.FromSeconds(1)); break;
                    case "expiry": now = 1010; break;
                    case "rollback": now = 9; break;
                    case "thread": thread = new(8, 10); break;
                    case "nesting": recorder.Begin(PortraitNativeObservedPhase.ManagerRender, 5); break;
                    case "zero": call = default; break;
                    case "clock-throw": throws = true; break;
                }
                check(!End(recorder, call, true), "invalid return cannot authorize a native read: " + mode);
            }
        });
    }
}
