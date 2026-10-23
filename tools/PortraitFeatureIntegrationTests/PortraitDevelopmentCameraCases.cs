using System.Reflection;
using System.Text.Json;
using FireGaze.PortraitPreview;

internal static class PortraitDevelopmentCameraCases
{
    private static readonly MethodInfo? Command = typeof(PortraitPreviewFeature).GetMethod("TryDevelopmentCameraCommand", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo? ReadReceipt = typeof(PortraitPreviewFeature).GetMethod("ReadDevelopmentCameraReceipt", BindingFlags.NonPublic | BindingFlags.Instance);

    public static void Run(string root, Action<bool, string> check)
    {
        check(Command is not null && ReadReceipt is not null,
            "development camera command must expose guarded native mutation and detached receipt");
        if (Command is null || ReadReceipt is null) return;
        RunPassiveHistory(root, check);
        RunGates(root, check);
        RunStaleBinding(root, check);
        RunInvalidAndFailedWrites(root, check);
        RunFrameworkDispatch(root, check);
        RunExpiry(root, check);
    }

    private static PortraitPreviewFeature Create(string root, string name)
    {
        var value = new PortraitPreviewFeature(Path.Combine(root, "dev-camera-" + name + ".json"));
        value.EditComposition(c => c with
        {
            CameraTarget = new(.1f, -.2f, .05f, 1), CameraPosition = new(.1f, -.2f, 1.25f, 1),
            CameraDistance = 1.2f, CameraYaw = 0, CameraPitch = 0, AnimationProgress = .25f,
            DirectionalRed = 11, DirectionalVerticalAngle = 12, AmbientBrightness = 13,
        });
        value.Sample(); value.OpenPreview();
        return value;
    }

    private static bool Send(PortraitPreviewFeature feature, params string[] parts)
        => (bool)Command!.Invoke(feature, [parts])!;
    private static JsonElement Receipt(PortraitPreviewFeature feature)
        => JsonSerializer.SerializeToElement(ReadReceipt!.Invoke(feature, null));
    private static string Prepare(PortraitPreviewFeature feature)
    {
        var nonce = Guid.NewGuid().ToString("D");
        Send(feature, "dev-native-camera", "prepare", nonce);
        return nonce;
    }
    private static bool Is(JsonElement receipt, string state)
        => receipt.GetProperty("status").GetString() == state;
    private static bool Near(float actual, float wanted) => MathF.Abs(actual - wanted) < .00001f;

    private static void RunPassiveHistory(string root, Action<bool, string> check)
    {
        using var feature = Create(root, "passive");
        var baseline = feature.Native.Snapshot.Copy();
        var observed = feature.ObservedSnapshot!.ToJSON();
        var count = feature.Store.Count; var calls = feature.Native.ApplyCalls;
        var nonce = Prepare(feature);
        var prepared = Receipt(feature);
        check(Is(prepared, "Prepared") && prepared.GetProperty("nonce").GetString() == nonce
            && prepared.GetProperty("schema").GetString() == "firegaze-dev-native-camera-v1"
            && feature.Native.ApplyCalls == calls && feature.Store.Count == count,
            "prepare binds fresh native owner and source without mutating editor or history");
        feature.EditComposition(c => c with { AnimationProgress = .8f });
        Send(feature, "dev-native-camera", "yaw", nonce, "2");
        var after = feature.Native.Snapshot;
        var receipt = Receipt(feature);
        check(Is(receipt, "Applied") && receipt.GetProperty("devNativeMutation").GetBoolean()
            && !receipt.GetProperty("uiCallbackSimulated").GetBoolean()
            && !receipt.GetProperty("directHistoryCommit").GetBoolean()
            && receipt.GetProperty("nativeWriteAttempted").GetBoolean()
            && receipt.GetProperty("nativeWriteAccepted").GetBoolean()
            && receipt.GetProperty("postVerified").GetBoolean(),
            "successful camera receipt identifies real dev native mutation and never claims simulated UI or history success");
        check(Near(after.Composition.CameraYaw, .034906585f)
            && Near(after.Composition.CameraPosition.X, .1418794f)
            && Near(after.Composition.CameraPosition.Y, -.2f)
            && Near(after.Composition.CameraPosition.Z, 1.2492689f)
            && (after.Composition with { CameraYaw = 0, CameraPosition = baseline.Composition.CameraPosition, AnimationProgress = .25f }) == baseline.Composition,
            "two degree native yaw preserves distance pitch target lighting appearance and live playback without hidden clamp");
        check(feature.Store.Count == count && feature.ObservedSnapshot!.ToJSON() == observed,
            "dev mutation cannot directly publish history or replace the ordinary editor observation");
        feature.Sample(); feature.Sample(advanceMilliseconds: 600);
        var recorded = PortraitEditorSnapshot.FromJSON(feature.Store.GetRecords(feature.Key!.Value)[0].SnapshotJSON)!;
        check(feature.Store.Count == count + 1 && Near(recorded.Composition.CameraYaw, .034906585f)
            && recorded.Composition.AnimationProgress == .8f,
            "ordinary passive sampler records the real dev camera change after its normal idle debounce");
        calls = feature.Native.ApplyCalls;
        Send(feature, "dev-native-camera", "yaw", nonce, "2");
        check(Is(Receipt(feature), "Rejected") && feature.Native.ApplyCalls == calls,
            "a consumed native camera nonce cannot write twice");
        Send(feature, "dev-native-camera", "prepare", nonce);
        check(Is(Receipt(feature), "Rejected") && feature.Native.ApplyCalls == calls,
            "preparing an already used nonce cannot reacquire mutation authority");
        check(!Send(feature, "unrelated-command"), "dev camera route leaves unrelated commands to existing handlers");
    }

    private static void RunGates(string root, Action<bool, string> check)
    {
        foreach (var gate in new[] { "not-dev", "disposed", "diagnostic", "fit", "suppressed", "photo-busy", "photo-drain", "photo-pending", "live-busy", "live-drain", "fit-busy", "fit-drain", "pointer", "keyboard", "no-save", "not-ready", "not-current", "no-generation" })
        {
            using var feature = Create(root, "gate-" + gate);
            feature.SetDevelopmentCameraGate(gate);
            var before = feature.Native.Snapshot.ToJSON(); var calls = feature.Native.ApplyCalls; var count = feature.Store.Count;
            var nonce = Prepare(feature);
            check(Is(Receipt(feature), "Rejected") && feature.Native.ApplyCalls == calls
                && feature.Native.Snapshot.ToJSON() == before && feature.Store.Count == count
                && feature.DevelopmentCameraPreservedCensus,
                $"native camera prepare refuses {gate} without writes history commits or stopping diagnostics");
        }
    }

    private static void RunStaleBinding(string root, Action<bool, string> check)
    {
        foreach (var change in new[] { "owner", "account", "generation", "session", "no-save", "diagnostic", "pointer", "keyboard", "expression", "equipment", "beauty", "paused-progress", "entry-point", "target" })
        {
            using var feature = Create(root, "stale-" + change);
            if (change == "paused-progress") feature.EditComposition(c => c with { Paused = true });
            var nonce = Prepare(feature);
            switch (change)
            {
                case "expression": feature.EditComposition(c => c with { Expression = 91 }); break;
                case "equipment":
                    var look = feature.Native.Snapshot.Look.Copy(); look.Pieces[3] = new(901, 2, 4);
                    feature.Native.Snapshot = feature.Native.Snapshot with { Look = look }; break;
                case "beauty":
                    var customize = (byte[])feature.Native.Snapshot.Customize.Clone(); customize[6] = 19;
                    feature.Native.Snapshot = feature.Native.Snapshot with { Customize = customize }; break;
                case "paused-progress": feature.EditComposition(c => c with { AnimationProgress = .9f }); break;
                case "entry-point": feature.Native.Snapshot = feature.Native.Snapshot with { EntryPoint = 1 }; break;
                case "target": feature.EditComposition(c => c with { CameraTarget = new(.2f, -.2f, .05f, 1) }); break;
                default: feature.SetDevelopmentCameraGate(change); break;
            }
            var before = feature.Native.Snapshot.ToJSON(); var calls = feature.Native.ApplyCalls; var count = feature.Store.Count;
            Send(feature, "dev-native-camera", "yaw", nonce, "-2");
            check(Is(Receipt(feature), "Rejected") && feature.Native.ApplyCalls == calls
                && feature.Native.Snapshot.ToJSON() == before && feature.Store.Count == count,
                $"prepared dev camera rejects changed {change} without overwriting current owner or direct history");
        }
    }

    private static void RunInvalidAndFailedWrites(string root, Action<bool, string> check)
    {
        foreach (var degrees in new[] { "NaN", "Infinity", "0", ".5", "5.1", "-6", "invalid" })
        {
            using var feature = Create(root, "angle-" + degrees.Replace('.', '_'));
            var nonce = Prepare(feature); var calls = feature.Native.ApplyCalls;
            Send(feature, "dev-native-camera", "yaw", nonce, degrees);
            check(Is(Receipt(feature), "Rejected") && feature.Native.ApplyCalls == calls,
                $"invalid native yaw {degrees} is rejected before the native write");
            Send(feature, "dev-native-camera", "yaw", nonce, "2");
            check(Is(Receipt(feature), "Rejected") && feature.Native.ApplyCalls == calls,
                $"invalid yaw {degrees} consumes its prepared nonce and cannot later be replayed");
        }
        using (var feature = Create(root, "wrong-nonce"))
        {
            _ = Prepare(feature); var calls = feature.Native.ApplyCalls;
            Send(feature, "dev-native-camera", "yaw", Guid.NewGuid().ToString("D"), "2");
            check(Is(Receipt(feature), "Rejected") && feature.Native.ApplyCalls == calls, "unbound nonce cannot select a current editor by accident");
        }
        foreach (var failure in new[] { "native", "post" })
        {
            using var feature = Create(root, "failure-" + failure);
            var nonce = Prepare(feature); var count = feature.Store.Count; var observed = feature.ObservedSnapshot!.ToJSON();
            if (failure == "native") feature.Native.RejectApply = true;
            else feature.Native.AfterEditorApply = () => feature.EditComposition(c => c with { Expression = 97 });
            Send(feature, "dev-native-camera", "yaw", nonce, "2");
            var receipt = Receipt(feature);
            check(Is(receipt, failure == "native" ? "NativeWriteRejected" : "PostVerifyFailed")
                && !receipt.GetProperty("postVerified").GetBoolean() && feature.Store.Count == count
                && feature.ObservedSnapshot!.ToJSON() == observed,
                $"{failure} failure cannot claim applied history or replace the sampler baseline");
        }
        using (var feature = Create(root, "preserve-native-range"))
        {
            feature.EditComposition(c => c with { CameraPitch = -.1f, CameraDistance = 2,
                CameraPosition = new(.1f, -.00033316f, 2.0400083f, 1) });
            var nonce = Prepare(feature);
            Send(feature, "dev-native-camera", "yaw", nonce, "-5");
            check(Is(Receipt(feature), "Applied") && feature.Native.Snapshot.Composition.CameraDistance == 2
                && feature.Native.Snapshot.Composition.CameraPitch == -.1f,
                "dev yaw preserves legal native camera extremes without the artistic viewport clamp");
        }
    }

    private static void RunFrameworkDispatch(string root, Action<bool, string> check)
    {
        using var feature = Create(root, "framework");
        var calls = feature.Native.EditorCaptureCalls;
        FireGaze.Plugin.Framework.IsInFrameworkUpdateThread = false;
        var nonce = Prepare(feature);
        check(feature.DevelopmentCameraQueuedActions.Count == 1 && feature.Native.EditorCaptureCalls == calls,
            "dev camera command dispatches before any native owner or snapshot reads");
        feature.DrainDevelopmentCameraCommands();
        check(Is(Receipt(feature), "Prepared"), "queued prepare establishes its fresh binding on Framework only");
        FireGaze.Plugin.Framework.IsInFrameworkUpdateThread = false;
        Send(feature, "dev-native-camera", "yaw", nonce, "2");
        feature.SetDevelopmentCameraGate("session");
        var writes = feature.Native.ApplyCalls;
        feature.DrainDevelopmentCameraCommands();
        check(Is(Receipt(feature), "Rejected") && feature.Native.ApplyCalls == writes,
            "Framework queued yaw cannot mutate a session replaced after enqueue");
    }

    private static void RunExpiry(string root, Action<bool, string> check)
    {
        using var feature = Create(root, "expiry");
        var nonce = Prepare(feature); var calls = feature.Native.ApplyCalls;
        Thread.Sleep(5100);
        Send(feature, "dev-native-camera", "yaw", nonce, "2");
        check(Is(Receipt(feature), "Rejected") && Receipt(feature).GetProperty("reason").GetString() == "Expired"
            && feature.Native.ApplyCalls == calls, "a prepared native camera request expires after five seconds");
    }
}
