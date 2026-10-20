using System.Runtime.CompilerServices;
using FireGaze.PortraitPreview;

internal static class OwnedDrainRetentionCases
{
    private static long module = 0x7C00000000;
    internal static int Run()
    {
        try
        {
            var pending = BeginUnknownCreate();
            // Let canceled timer continuations and the retirement driver's initial
            // 5ms yield finish; only the exported pending drain Task remains owned.
            Thread.Sleep(30); Collect();
            if (pending.Drain.IsCompleted || !pending.Hooks.IsAlive || !pending.Provider.IsAlive || !pending.Environment.IsAlive)
                throw new InvalidOperationException("pending standalone owned physical drain lost the actual hook owner/provider/environment under unknown Create");
            GC.KeepAlive(pending.Drain);
            var completed = BeginCompleted(); Thread.Sleep(30); Collect();
            if (!completed.Drain.IsCompletedSuccessfully || completed.Hooks.IsAlive || completed.Provider.IsAlive || completed.Environment.IsAlive)
                throw new InvalidOperationException("completed standalone owned physical drain retains its old plugin hook owner/provider/environment");
            GC.KeepAlive(completed.Drain);
            Console.WriteLine("PASS 2 standalone owned only-task physical retention checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Collect() { for (var index = 0; index < 3; index++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); } }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task Drain, WeakReference Hooks, WeakReference Provider, WeakReference Environment) BeginUnknownCreate()
    {
        var environment = new EnvironmentProof((nint)Interlocked.Add(ref module, 0x4000000));
        var provider = new OwnedPhysicalCases.Provider { FailCreateAt = 3 };
        var hooks = new PortraitOwnedSceneHooks(environment);
        if (hooks.TryInstallPassive(new DalamudPortraitOwnedSceneHookFactory(provider, new OwnedPhysicalCases.Proof()), TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("unknown Create fixture unexpectedly installed");
        hooks.TryDispose();
        return (hooks.DrainPhysicalAsync(), new(hooks), new(provider), new(environment));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task Drain, WeakReference Hooks, WeakReference Provider, WeakReference Environment) BeginCompleted()
    {
        var environment = new EnvironmentProof((nint)Interlocked.Add(ref module, 0x4000000)); var provider = new OwnedPhysicalCases.Provider();
        var hooks = new PortraitOwnedSceneHooks(environment);
        if (!hooks.TryInstallPassive(new DalamudPortraitOwnedSceneHookFactory(provider, new OwnedPhysicalCases.Proof()), TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("completed owner fixture did not install");
        var first = hooks.DrainPhysicalAsync();
        if (!hooks.TryDispose() || !first.IsCompletedSuccessfully || !ReferenceEquals(first, hooks.DrainPhysicalAsync()))
            throw new InvalidOperationException("owned physical completion must retain its immediate and idempotent Task contract");
        return (first, new(hooks), new(provider), new(environment));
    }
    private sealed class EnvironmentProof(nint module) : IPortraitOwnedSceneHookEnvironment
    {
        public bool IsFrameworkThread => true;
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool TryValidateInstallation(out string reason) { reason = "No production scene admission."; return false; }
        public bool IsSourceCurrent(PortraitIdentity source) => false;
        public bool IsSlotIdle(uint slot) => false;
        public bool IsSlotOwnedBy(uint slot, nint view) => false;
        public bool TryReadViewSlot(nint view, out uint slot) { slot = uint.MaxValue; return false; }
    }
}
