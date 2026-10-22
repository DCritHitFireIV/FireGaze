using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

// Exercise the actual compiled Feature partial and managed drain. No Feature constructor,
// game process, Framework service, or GPU is used; only its accepted observation is supplied.
var pluginPath = args.Length == 1 ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/FireGaze/bin/Release/FireGaze.dll"));
var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "XIVLauncherCN", "addon", "Hooks", "dev");
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (var directory in new[] { Path.GetDirectoryName(pluginPath)!, sdk })
    {
        var candidate = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(candidate)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
    }
    return null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(pluginPath);
var featureType = assembly.GetType("FireGaze.PortraitPreview.PortraitPreviewFeature", true)!;
var managedType = assembly.GetType("FireGaze.PortraitPreview.PortraitManagedDrain", true)!;
var writerType = assembly.GetType("FireGaze.PortraitPreview.OwnedCacheWriter", true)!;
const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
var drainObservation = featureType.GetMethod("DrainStorageObservation", instance)!;
var combine = managedType.GetMethod("Combine", [typeof(Task[])])!;
var failures = new List<string>();
var checks = 0;

void Check(bool condition, string name)
{
    checks++;
    Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
    if (!condition) failures.Add(name);
}

Task Observe(Task<bool>? observation)
{
    var feature = RuntimeHelpers.GetUninitializedObject(featureType);
    featureType.GetField("storageObservation", instance)!.SetValue(feature, observation);
    var result = (Task)drainObservation.Invoke(feature, null)!;
    Check((bool)featureType.GetField("storageObservationClosing", instance)!.GetValue(feature)!,
        "actual Feature seals storage observation admission");
    return result;
}

Task Combine(params Task[] tasks) => (Task)combine.Invoke(null, [tasks])!;

static async Task<Exception?> Outcome(Task task)
{
    try { await task; return null; }
    catch (Exception error) { return error; }
}

Console.WriteLine($"Actual FireGaze {assembly.GetName().Version}; MVID {assembly.ManifestModule.ModuleVersionId}; SHA256 {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pluginPath)))}");

var unchanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var unchangedDrain = Combine(Task.FromResult(true), Task.FromResult(true), Task.FromResult(true), Observe(unchanged.Task));
Check(!unchangedDrain.IsCompleted, "final close waits for accepted unchanged storage observation");
unchanged.SetResult(false);
var unchangedError = await Outcome(unchangedDrain);
if (unchangedError is not null) Console.WriteLine("Actual unchanged-observation error: " + unchangedError.Message);
Check(unchangedError is null,
    "unchanged storage observation is successful close when all durable writes succeed");
Check(await Outcome(Combine(Task.FromResult(true), Observe(Task.FromResult(false)))) is null,
    "already completed unchanged storage observation is successful close");

var recovered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var recoveredDrain = Combine(Task.FromResult(true), Observe(recovered.Task));
Check(!recoveredDrain.IsCompleted, "final close waits for accepted recovery observation");
recovered.SetResult(true);
Check(await Outcome(recoveredDrain) is null, "recovery observation completes successful close");
Check(await Outcome(Combine(Task.FromResult(true), Observe(null))) is null,
    "absent observation completes successful close");

var observationFailure = new IOException("storage observation fixture failure");
var failedObservation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var failedObservationDrain = Combine(Task.FromResult(true), Observe(failedObservation.Task));
Check(!failedObservationDrain.IsCompleted, "observation fault remains pending until its task settles");
failedObservation.SetException(observationFailure);
Check(ReferenceEquals(await Outcome(failedObservationDrain), observationFailure),
    "actual observation exception remains visible to final close");

using var canceled = new CancellationTokenSource();
canceled.Cancel();
var canceledDrain = Combine(Task.FromResult(true), Observe(Task.FromCanceled<bool>(canceled.Token)));
Check(await Outcome(canceledDrain) is OperationCanceledException && canceledDrain.IsCanceled,
    "canceled observation cannot become successful close");

// Keep the actual writer's false durability receipt in the same aggregation. Returning false
// at its persistence boundary exercises its real queue, worker, and BeginDispose contract.
var writer = Activator.CreateInstance(writerType, [(Func<string, bool>)(_ => false)])!;
Check((bool)writerType.GetMethod("Queue", [typeof(long), typeof(string)])!.Invoke(writer, [1L, "retained final snapshot"])!,
    "actual owned-cache writer accepts final snapshot");
var writerDrain = (Task<bool>)writerType.GetMethod("BeginDispose")!.Invoke(writer, null)!;
Check(!await writerDrain, "actual owned-cache writer exposes unsuccessful final durability");
var lastObservation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var failedWriteDrain = Combine(Task.FromResult(true), writerDrain, Observe(lastObservation.Task));
Check(!failedWriteDrain.IsCompleted, "unsuccessful durable write still waits for accepted observation");
lastObservation.SetResult(false);
Check(await Outcome(failedWriteDrain) is IOException { Message: var writeError } && writeError.StartsWith("1 ", StringComparison.Ordinal),
    "unsuccessful durable write remains a failed close after unchanged observation");

Console.WriteLine($"PortraitStorageObservationDrainTests: {checks - failures.Count} passed, {failures.Count} failed; actual Feature and writer, no game or GPU.");
return failures.Count == 0 ? 0 : 1;
