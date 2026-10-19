using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using FireGaze.Diagnostics;

internal static class Program
{
    private static int checks;
    public static int Main()
    {
        try
        {
            FileDrainAndIdempotentShutdown();
            TimerDoesNotRootCollectibleMirror();
            WriteShutdownRaceKeepsAcceptedEntries();
            Console.WriteLine($"ActivityLogLifecycleTests: {checks} checks passed."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void FileDrainAndIdempotentShutdown()
    {
        var directory = NewDirectory(); ActivityLog.Initialize(directory);
        for (var i = 0; i < 1200; i++) ActivityLog.Info("lifecycle", "entry-" + i + " " + new string('x', 320));
        var first = ActivityLog.ShutdownAsync(); var second = ActivityLog.ShutdownAsync();
        Check(ReferenceEquals(first, second), "repeated shutdown returns one task");
        Check(first.GetAwaiter().GetResult(), "shutdown completes after final flush");
        var text = string.Join(Environment.NewLine, Directory.GetFiles(Path.Combine(directory, "logs"), "*.log").Select(File.ReadAllText));
        Check(text.Contains("entry-0", StringComparison.Ordinal) && text.Contains("entry-1199", StringComparison.Ordinal), "final flush writes all accepted entries, not one 200KB batch");
        Check(ActivityLog.Mirror is null && ActivityLog.Directory is null, "shutdown clears mirror and directory roots");
        ActivityLog.Info("lifecycle", "rejected-after-shutdown");
        Check(!ActivityLog.Snapshot().Contains("rejected-after-shutdown", StringComparison.Ordinal), "writes after shutdown are rejected instead of rooting a closed log");
        Directory.Delete(directory, true);
    }

    private static void TimerDoesNotRootCollectibleMirror()
    {
        var directory = NewDirectory(); ActivityLog.Initialize(directory);
        var weak = CreateCollectibleMirror();
        ActivityLog.Info("alc", "before-shutdown");
        Check(weak.IsAlive, "timer mirror initially roots collectible callback");
        Check(ActivityLog.ShutdownAsync().GetAwaiter().GetResult(), "collectible shutdown completes");
        ForceGC(weak); Check(!weak.IsAlive, "shutdown clears timer and mirror collectible roots");
        Directory.Delete(directory, true);
    }

    private static void WriteShutdownRaceKeepsAcceptedEntries()
    {
        var directory = NewDirectory(); ActivityLog.Initialize(directory);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        ActivityLog.Mirror = (_, _) => { entered.Set(); Check(release.Wait(TimeSpan.FromSeconds(5)), "accepted mirror is released"); };
        var write = Task.Run(() => ActivityLog.Info("race", "accepted-before-close")); Check(entered.Wait(TimeSpan.FromSeconds(5)), "write reaches mirror before close");
        var shutdown = ActivityLog.ShutdownAsync(); Check(!shutdown.IsCompleted, "shutdown waits for accepted mirror");
        ActivityLog.Info("race", "rejected-after-close"); release.Set(); write.GetAwaiter().GetResult();
        Check(shutdown.GetAwaiter().GetResult(), "race shutdown completes");
        var text = string.Join(Environment.NewLine, Directory.GetFiles(Path.Combine(directory, "logs"), "*.log").Select(File.ReadAllText));
        Check(text.Contains("accepted-before-close", StringComparison.Ordinal) && !text.Contains("rejected-after-close", StringComparison.Ordinal), "close rejects new writes while retaining accepted write"); Directory.Delete(directory, true);
    }

    private static WeakReference CreateCollectibleMirror()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new("ActivityLog.CollectibleMirror." + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("Mirror").DefineType("Mirror", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var method = type.DefineMethod("Call", MethodAttributes.Public | MethodAttributes.Static, typeof(void), [typeof(ActivityLevel), typeof(string)]);
        method.GetILGenerator().Emit(OpCodes.Ret); var created = type.CreateType()!;
        ActivityLog.Mirror = (Action<ActivityLevel, string>)Delegate.CreateDelegate(typeof(Action<ActivityLevel, string>), created.GetMethod("Call")!);
        return new WeakReference(assembly);
    }
    private static string NewDirectory() { var path = Path.Combine(Path.GetTempPath(), "ActivityLogLifecycle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void ForceGC(WeakReference reference) { for (var i = 0; i < 20 && reference.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(20); } }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException("FAIL: " + message); }
}
