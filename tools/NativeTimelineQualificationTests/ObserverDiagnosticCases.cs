using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Runtime;

internal static unsafe partial class Program
{
    private static IEnumerable<(string Name, System.Action Run)> ObserverDiagnosticCases()
    {
        yield return ("runtime closing revokes resolved admission before retaining any failed native callback", () => {
            using var runtime = EnabledDiagnosticRuntime();
            runtime.Dispose();
            foreach (var field in new[] { "resolvedTimelineAdmitted", "resolvedActionTimelineAdmitted" })
                Check(!(bool)typeof(ActorPreviewRuntime).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!,
                    "a callback surviving native teardown must take pure Original forwarding without consulting revoked services");
        });
        yield return ("effective resolved observers do not hide original SDK return-signature rejection", () => {
            using var runtime = EnabledDiagnosticRuntime();
            foreach (var action in new[] { false, true })
            {
                var sdkName = action ? "SdkActionTimelinePlayStatus" : "SdkTimelinePlayStatus";
                var sdk = typeof(ActorPreviewRuntime).GetProperty(sdkName)?.GetValue(runtime) as OptionalNativeHookStatus;
                Check(sdk is { Installed: false } && sdk.Reason.Contains("Boolean") && sdk.Reason.Contains("Void"),
                    "SDK rejection remains separately readable after independent observer admission");
                var effective = action ? runtime.ActionTimelinePlayStatus : runtime.TimelinePlayStatus;
                var resolved = action ? runtime.ResolvedActionTimelinePlayStatus : runtime.ResolvedTimelinePlayStatus;
                Check(effective.Installed && resolved.Installed, "effective and resolved observers expose actual enabled installation");
            }
        });
        yield return ("standalone diagnostics serializes SDK and resolved observer evidence independently", () => {
            using var runtime = EnabledDiagnosticRuntime();
            var hostType = typeof(FireGaze.LocalPreview.Plugin);
            var host = RuntimeHelpers.GetUninitializedObject(hostType);
            hostType.GetField("runtime", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(host, runtime);
            var state = hostType.GetProperty("ClientState")!;
            var previous = state.GetValue(null);
            state.SetValue(null, TimelineHookInstallationTests.Proxy.Create<IClientState>((method, _) => method.Name == "get_TerritoryType" ? 1u : method.ReturnType == typeof(bool) ? false : null));
            try
            {
                var text = (string)hostType.GetMethod("Diagnostics", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host, null)!;
                using var json = JsonDocument.Parse(text);
                foreach (var route in new[] { "play", "action" })
                {
                    var root = json.RootElement;
                    Check(root.GetProperty("timelineObservers").GetProperty(route).GetProperty("Installed").GetBoolean(), "effective observer remains available");
                    Check(root.TryGetProperty("sdkTimelineObservers", out var sdk) && !sdk.GetProperty(route).GetProperty("Installed").GetBoolean(), "JSON includes original SDK rejection");
                    Check(root.TryGetProperty("resolvedTimelineObservers", out var resolved) && resolved.GetProperty(route).GetProperty("Installed").GetBoolean(), "JSON includes independently admitted observer");
                }
            }
            finally { state.SetValue(null, previous); }
        });
    }

    private static ActorPreviewRuntime EnabledDiagnosticRuntime()
    {
        var runtime = TimelineHookInstallationTests.Create(new TimelineHookInstallationTests.RecordingInterop());
        foreach (var action in new[] { false, true })
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var field = typeof(ActorPreviewRuntime).GetField(action ? "resolvedActionTimelinePlay" : "resolvedTimelinePlay", flags)!;
            var hook = (IDisposable)typeof(HookBackendDouble).GetMethod("Create")!.MakeGenericMethod(field.FieldType.GetGenericArguments()[0]).Invoke(null, null)!;
            HookBackendDouble.Enable(hook);
            field.SetValue(runtime, hook);
            typeof(ActorPreviewRuntime).GetField(action ? "resolvedActionTimelineAdmitted" : "resolvedTimelineAdmitted", flags)!.SetValue(runtime, true);
            ((List<IDisposable>)typeof(ActorPreviewRuntime).GetField("installedHooks", flags)!.GetValue(runtime)!).Add(hook);
        }
        return runtime;
    }
}
