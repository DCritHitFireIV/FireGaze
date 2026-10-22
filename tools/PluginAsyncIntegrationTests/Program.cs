using Dalamud.Plugin;
using FireGaze;

var pluginType = typeof(Plugin);
var asyncContract = typeof(IAsyncDalamudPlugin);
var syncContract = typeof(IDalamudPlugin);
var load = pluginType.GetMethod(nameof(IAsyncDalamudPlugin.LoadAsync), [typeof(CancellationToken)]);
var disposeAsync = pluginType.GetMethod(nameof(IAsyncDisposable.DisposeAsync), Type.EmptyTypes);

var checks = new (bool Value, string Name)[]
{
    (asyncContract.IsAssignableFrom(pluginType), "Plugin uses Dalamud's asynchronous loading contract"),
    (!syncContract.IsAssignableFrom(pluginType), "Plugin does not also advertise the synchronous contract"),
    (load is not null && load.ReturnType == typeof(Task), "Plugin exposes LoadAsync(CancellationToken)"),
    (disposeAsync is not null && disposeAsync.ReturnType == typeof(ValueTask), "Plugin exposes asynchronous disposal"),
};

foreach (var check in checks)
    Console.WriteLine($"{(check.Value ? "PASS" : "FAIL")} {check.Name}");

var failures = checks.Count(check => !check.Value);
var behavior = await LoadThreadCases.Run();
failures += behavior.Failures;
Console.WriteLine($"PluginAsyncIntegrationTests: {checks.Length + behavior.Checks} checks, {failures} failures");
return failures == 0 ? 0 : 1;
