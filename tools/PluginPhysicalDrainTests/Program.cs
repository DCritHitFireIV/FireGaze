using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherCN", "addon", "Hooks", "dev");
var pluginPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/FireGaze/bin/Release/FireGaze.dll"));
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (var directory in new[] { Path.GetDirectoryName(pluginPath)!, sdk })
    {
        var path = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
var type = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath).GetType("FireGaze.Plugin", throwOnError: true)!;
const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
var checks = 0;
foreach (var managedFailure in new[] { false, true })
{
    // Exercise the actual host begin-close method with Framework release already complete.
    var plugin = RuntimeHelpers.GetUninitializedObject(type);
    type.GetField("disposed", fields)!.SetValue(plugin, 1);
    type.GetField("portraitManagedDrain", fields)!.SetValue(plugin,
        managedFailure ? Task.FromException(new IOException("accepted file failed")) : Task.CompletedTask);
    var physical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    type.GetField("portraitPhysicalDrain", fields)?.SetValue(plugin, physical.Task);
    var returned = (ValueTask)type.GetMethod("BeginDisposeAsync", fields)!.Invoke(plugin, null)!;
    var close = returned.AsTask();
    var held = !close.IsCompleted;
    physical.SetResult();
    try { await close; } catch (IOException) when (managedFailure) { }
    if (!held) throw new InvalidOperationException("actual plugin host releases provider scope while physical work remains unfinished; fileFailure=" + managedFailure);
    if (close.IsFaulted != managedFailure) throw new InvalidOperationException("actual host lost managed close outcome");
    checks += 2;
    Console.WriteLine("PASS actual host waits physical retirement after Framework release; fileFailure=" + managedFailure);
}
foreach (var managedFailure in new[] { false, true })
{
    var physicalType = type.Assembly.GetType("FireGaze.PortraitPreview.PortraitCaptureRetirement", true)!;
    var physical = Activator.CreateInstance(physicalType, nonPublic: true)!;
    var resource = new FailedResource();
    physicalType.GetMethod("Release")!.Invoke(physical, [resource]);
    var retirement = (Task)physicalType.GetMethod("Seal")!.Invoke(physical, null)!;
    var plugin = RuntimeHelpers.GetUninitializedObject(type);
    type.GetField("disposed", fields)!.SetValue(plugin, 1);
    type.GetField("portraitManagedDrain", fields)!.SetValue(plugin,
        managedFailure ? Task.FromException(new IOException("accepted file failed")) : Task.CompletedTask);
    type.GetField("portraitPhysicalDrain", fields)!.SetValue(plugin, retirement);
    var close = ((ValueTask)type.GetMethod("BeginDisposeAsync", fields)!.Invoke(plugin, null)!).AsTask();
    await Task.Delay(20);
    if (close.IsCompleted || retirement.IsCompleted || resource.Attempts != 1
        || (int)physicalType.GetProperty("CleanupFailures")!.GetValue(physical)! != 1)
        throw new InvalidOperationException("cleanup quarantine allowed actual host provider scope retirement; fileFailure=" + managedFailure);
    checks++;
    Console.WriteLine("PASS actual host remains incomplete after unconfirmed provider release; fileFailure=" + managedFailure);
}
checks += await CaptureProgressIpcCases.RunAsync(type.Assembly);
Console.WriteLine($"PluginPhysicalDrainTests: {checks} passed, 0 failed; real host method, no game or GPU.");
return 0;

sealed class FailedResource : IDisposable
{
    public int Attempts;
    public void Dispose() { Attempts++; throw new InvalidOperationException("unconfirmed release"); }
}
