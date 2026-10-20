using System.Reflection;
using FireGaze.PortraitPreview;

internal static class OwnedPassiveScalarEventCases
{
    private static int checks, originals;
    private static long module = 0x7900000000;
    private static Action? duringOriginal;
    private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
    internal static int Run()
    {
        try
        {
            TransparentBoundedEvents(); EntryBeforeCompleteInstall(); StopDuringOriginal(); ClockAndThreadFailures();
            ScalarPointersAndNonPassive();
            Console.WriteLine($"PASS {checks} bounded passive scalar context event checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { duringOriginal = null; }
    }
    private static void TransparentBoundedEvents()
    {
        var environment = new EnvironmentProof((nint)Interlocked.Add(ref module, 0x4000000));
        var provider = new OwnedPhysicalCases.Provider(); var reads = 0;
        using var hooks = new PortraitOwnedSceneHooks(environment, timestamp: () => { reads++; return 0; }, timestampFrequency: 1000,
            nativeThread: () => new(123, Environment.CurrentManagedThreadId));
        Check(hooks.PassiveContextEvents is null && reads == 0, "construction and pure event getter perform no native/clock work");
        provider.OnEnable = () => Check(Construct(provider)(0x100) == 0x101 && hooks.PassiveContextEvents is null,
            "partial enabling forwards native exactly once and cannot record a complete owned generation");
        Check(hooks.TryInstallPassive(new DalamudPortraitOwnedSceneHookFactory(new OriginalProvider(provider), new OwnedPhysicalCases.Proof()), TimeSpan.FromSeconds(1)), hooks.LastError ?? "event installation");
        var empty = hooks.PassiveContextEvents!;
        Check(empty is { Capacity: 64, Dropped: 0, Events.Count: 0, ProductionAdmission: false } && empty.Installation != Guid.Empty,
            "new fully enabled passive owner begins with a bounded scalar ledger and no ownership admission");
        var ctor = Construct(provider); var dtor = Destroy(provider); var before = originals;
        Check(ctor(0x200) == 0x201 && dtor(0x300, 0xA5) == 0x3A7 && originals == before + 2,
            "exact constructor pointer result and deleting-destructor byte flags preserve one Original each");
        var first = hooks.PassiveContextEvents!;
        Check(first.Events.Count == 2 && empty.Events.Count == 0, "event snapshots are detached and do not change under later callbacks");
        var constructed = first.Events[0]; var destroyed = first.Events[1];
        Check(constructed is { Sequence: 1, Kind: PortraitOwnedScenePassiveContextEventKind.Construct, Context: 0x200, Flags: 0, Result: 0x201, OriginalReturned: true, EnteredAt: 0, ReturnedAt: 0 }
            && constructed.EntryThread.NativeID == 123 && constructed.EntryThread == constructed.ReturnThread,
            "constructor ledger contains scalar arguments, actual result and independently captured thread/clock only");
        Check(destroyed is { Sequence: 2, Kind: PortraitOwnedScenePassiveContextEventKind.Destroy, Context: 0x300, Flags: 0xA5, Result: 0x3A7, OriginalReturned: true },
            "destructor ledger retains exact flags and native return without dereferencing the receiver");
        var failure = new InvalidOperationException("actual native fixture exception"); duringOriginal = () => throw failure;
        before = originals; Exception? observed = null;
        try { ctor(0x400); } catch (Exception error) { observed = error; }
        Check(ReferenceEquals(observed, failure) && originals == before + 1, "constructor Original exception is propagated unchanged exactly once");
        Check(hooks.PassiveContextEvents!.Events[2] is { OriginalReturned: false, Result: 0, Context: 0x400 },
            "a throwing Original is recorded as failure and does not invent a native return value");
        before = originals; observed = null;
        try { dtor(0x450, 0xFF); } catch (Exception error) { observed = error; }
        Check(ReferenceEquals(observed, failure) && originals == before + 1
            && hooks.PassiveContextEvents!.Events[3] is { Kind: PortraitOwnedScenePassiveContextEventKind.Destroy, Context: 0x450, Flags: 0xFF, OriginalReturned: false, Result: 0 },
            "destructor failure preserves exact exception, flags and one native call without a guessed result");
        duringOriginal = null;
        var publishingComplete = 0; var snapshotReads = 0; var tornSnapshots = 0;
        using var readerStarted = new ManualResetEventSlim();
        var reader = Task.Factory.StartNew(() =>
        {
            do
            {
                var snapshot = hooks.PassiveContextEvents!;
                if (snapshot.Events.Count > 64 || snapshot.Events.Select(value => value.Sequence).Distinct().Count() != snapshot.Events.Count
                    || snapshot.Events.Any(value => value.Sequence is < 1 or > 64 || value.EntryThread.NativeID != 123
                        || value.EntryThread.ManagedID <= 0 || value.EntryThread != value.ReturnThread
                        || value.EnteredAt != 0 || value.ReturnedAt != 0)) Interlocked.Increment(ref tornSnapshots);
                Interlocked.Increment(ref snapshotReads); readerStarted.Set(); Thread.Yield();
            } while (Volatile.Read(ref publishingComplete) == 0);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Check(readerStarted.Wait(TimeSpan.FromSeconds(5)), "detached scalar reader starts before concurrent publication");
        var callbacks = Enumerable.Range(0, 128).Select(index => Task.Run(() => ctor(0x500 + index))).ToArray();
        Check(Task.WaitAll(callbacks, TimeSpan.FromSeconds(5)), "concurrent typed native context callbacks return");
        Volatile.Write(ref publishingComplete, 1);
        Check(reader.Wait(TimeSpan.FromSeconds(5)) && snapshotReads > 0 && tornSnapshots == 0,
            "concurrent detached reads see only complete unique fixed-capacity scalar publications");
        var full = hooks.PassiveContextEvents!;
        Check(full.Events.Count == 64 && full.Capacity == 64 && full.Dropped == 68
            && full.Events.Select(value => value.Sequence).SequenceEqual(Enumerable.Range(1, 64).Select(value => (long)value)),
            "fixed first-64 storage publishes each unique concurrent slot and counts every overflow");
        Check(full.Events.All(value => value.EntryThread.ManagedID > 0 && value.EntryThread == value.ReturnThread),
            "concurrent event evidence never substitutes a remembered source thread for the actual callback stamp");
        Check(hooks.TryDispose() && hooks.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)), "bounded collector does not hold up physical retirement");
        ctor(0x800); dtor(0x900, 1);
        Check(hooks.PassiveContextEvents!.Events.Count == 64 && hooks.PassiveContextEvents.Dropped == 68,
            "late stable direct entry after detach cannot append or revive old ledger evidence");
    }
    private static void EntryBeforeCompleteInstall()
    {
        var environment = new EnvironmentProof((nint)Interlocked.Add(ref module, 0x4000000)); var provider = new OwnedPhysicalCases.Provider();
        using var hooks = new PortraitOwnedSceneHooks(environment);
        using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim(); Task<nint>? callback = null; var starts = 0;
        duringOriginal = () => { entered.Set(); finish.Wait(); };
        provider.OnEnable = () =>
        {
            if (Interlocked.Increment(ref starts) != 1) return;
            callback = Task.Run(() => Construct(provider)(0xD00));
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "constructor entry began while the owned batch was only partially enabled");
        };
        try
        {
            Check(hooks.TryInstallPassive(new DalamudPortraitOwnedSceneHookFactory(new OriginalProvider(provider), new OwnedPhysicalCases.Proof()), TimeSpan.FromSeconds(1)), "partial-entry fixture fully installs");
            finish.Set(); Check(callback!.Wait(TimeSpan.FromSeconds(5)) && callback.Result == 0xD01, "preinstallation native Original returned after the complete owner became installed");
            Check(hooks.PassiveContextEvents!.Events.Count == 0, "a callback entered before full39 installation cannot seed the new scalar generation when it later returns");
            duringOriginal = null; Construct(provider)(0xD10);
            Check(hooks.PassiveContextEvents!.Events.Count == 1, "only a future invocation begun under the complete39 owner can record");
        }
        finally { finish.Set(); callback?.Wait(TimeSpan.FromSeconds(5)); duringOriginal = null; }
    }
    private static void StopDuringOriginal()
    {
        var environment = new EnvironmentProof((nint)Interlocked.Add(ref module, 0x4000000)); var provider = new OwnedPhysicalCases.Provider();
        using var hooks = new PortraitOwnedSceneHooks(environment);
        Check(hooks.TryInstallPassive(new DalamudPortraitOwnedSceneHookFactory(new OriginalProvider(provider), new OwnedPhysicalCases.Proof()), TimeSpan.FromSeconds(1)), "pending context install");
        var ctor = Construct(provider); using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        duringOriginal = () => { entered.Set(); finish.Wait(); };
        var callback = Task.Run(() => ctor(0xA00));
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "context Original entered under complete passive owner");
        try
        {
            Check(!hooks.TryDispose() && !hooks.DrainPhysicalAsync().IsCompleted, "stop retains actual gateway/trampoline while context Original is active");
            finish.Set(); Check(callback.Wait(TimeSpan.FromSeconds(5)) && callback.Result == 0xA01, "stopped native Original still completes transparently");
            Check(hooks.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && hooks.PassiveContextEvents!.Events.Count == 0,
                "a native invocation completing after stop cannot publish fresh old-generation scalar evidence");
        }
        finally { finish.Set(); callback.Wait(TimeSpan.FromSeconds(5)); duringOriginal = null; }
    }
    private static void ClockAndThreadFailures()
    {
        var environment = new EnvironmentProof((nint)Interlocked.Add(ref module, 0x4000000)); var provider = new OwnedPhysicalCases.Provider();
        var now = 0L; var failThread = false; var failClock = false; var unknownThread = false;
        using var hooks = new PortraitOwnedSceneHooks(environment,
            timestamp: () => failClock ? throw new InvalidOperationException("clock unavailable") : now, timestampFrequency: 1000,
            nativeThread: () => failThread ? throw new InvalidOperationException("thread unavailable") : unknownThread ? default : new(123, 1));
        Check(hooks.TryInstallPassive(new DalamudPortraitOwnedSceneHookFactory(new OriginalProvider(provider), new OwnedPhysicalCases.Proof()), TimeSpan.FromSeconds(1)), "fallible scalar evidence install");
        var ctor = Construct(provider); failThread = true;
        Check(ctor(0xB00) == 0xB01 && hooks.PassiveContextEvents!.Events.Count == 0,
            "observer thread failure cannot unwind through native Original or publish guessed thread evidence");
        failThread = false; failClock = true;
        Check(ctor(0xB10) == 0xB11 && hooks.PassiveContextEvents!.Events.Count == 0,
            "entry clock failure cannot replace Original behavior or invent timing evidence");
        failClock = false; unknownThread = true;
        Check(ctor(0xB20) == 0xB21 && hooks.PassiveContextEvents!.Events.Count == 0,
            "zero thread identity remains unknown rather than an admissible callback stamp");
        unknownThread = false;
        duringOriginal = () => failClock = true;
        Check(ctor(0xB30) == 0xB31 && hooks.PassiveContextEvents!.Events.Count == 0,
            "return clock failure suppresses evidence without changing the exact native result");
        duringOriginal = null; failClock = false;
        duringOriginal = () => failThread = true;
        Check(ctor(0xB40) == 0xB41 && hooks.PassiveContextEvents!.Events.Count == 0,
            "return thread failure suppresses evidence without unwinding through Original");
        duringOriginal = null; failThread = false;
        duringOriginal = () => now = -1;
        Check(ctor(0xB50) == 0xB51 && hooks.PassiveContextEvents!.Events.Count == 0,
            "backward return clock does not publish an invented monotonic event");
        duringOriginal = null; now = 1000;
        Check(ctor(0xC00) == 0xC01 && hooks.PassiveContextEvents!.Events.Count == 0,
            "monotonic total deadline suppresses new scalar events without suppressing native Original");
    }
    private static void ScalarPointersAndNonPassive()
    {
        foreach (var passive in new[] { false, true })
        {
            var environment = new EnvironmentProof((nint)Interlocked.Add(ref module, 0x4000000), true);
            var provider = new OwnedPhysicalCases.Provider();
            using var hooks = new PortraitOwnedSceneHooks(environment, timestamp: () => 0, timestampFrequency: 1000,
                nativeThread: () => new(123, 1));
            var factory = new DalamudPortraitOwnedSceneHookFactory(new OriginalProvider(provider), new OwnedPhysicalCases.Proof());
            Check(passive ? hooks.TryInstallPassive(factory, TimeSpan.FromSeconds(1)) : hooks.TryInstall(factory),
                "scalar pointer fixture installs the selected passive or nonpassive mode");
            var ctor = Construct(provider); var dtor = Destroy(provider); var before = originals;
            foreach (var pointer in new nint[] { 0, 1, -1, unchecked((nint)0x7FFF_FFFF_FFFF_FFFFL) })
                Check(ctor(pointer) == unchecked(pointer + 1) && dtor(pointer, 0xFF) == unchecked(pointer + 257),
                    "scalar context observation never dereferences null, unreadable or noncanonical receiver values");
            Check(originals == before + 8, "scalar pointer diagnostics never retry or suppress any Original");
            Check(passive ? hooks.PassiveContextEvents is { Events.Count: 8 } : hooks.PassiveContextEvents is null,
                "only an explicitly passive complete owner collects scalar context events");
            Check(hooks.TryDispose(), "scalar-only diagnostics do not retain native ownership after calls return");
        }
    }
    private static ConstructContext Construct(OwnedPhysicalCases.Provider provider)
    {
        var native = provider.Created.Single(hook => hook.Detour.GetType().GetMethod("Invoke") is { ReturnType: var result } invoke
            && result == typeof(nint) && invoke.GetParameters().Select(value => value.ParameterType).SequenceEqual(new[] { typeof(nint) }));
        return (ConstructContext)Delegate.CreateDelegate(typeof(ConstructContext), native.Detour.Target, native.Detour.Method);
    }
    private static DestroyContext Destroy(OwnedPhysicalCases.Provider provider)
    {
        var native = provider.Created.Single(hook => hook.Detour.GetType().GetMethod("Invoke") is { ReturnType: var result } invoke
            && result == typeof(nint) && invoke.GetParameters().Select(value => value.ParameterType).SequenceEqual(new[] { typeof(nint), typeof(byte) }));
        return (DestroyContext)Delegate.CreateDelegate(typeof(DestroyContext), native.Detour.Target, native.Detour.Method);
    }
    public static nint ConstructOriginal(nint context) { Interlocked.Increment(ref originals); duringOriginal?.Invoke(); return context + 1; }
    public static nint DestroyOriginal(nint context, byte flags) { Interlocked.Increment(ref originals); duringOriginal?.Invoke(); return context + flags + 2; }
    private sealed class OriginalProvider(OwnedPhysicalCases.Provider inner) : IPortraitQualifiedNativeHookProvider
    {
        public string Identity => inner.Identity;
        public bool TryQualifyStartDisabled(out string reason) => inner.TryQualifyStartDisabled(out reason);
        public bool QualificationPreserved() => inner.QualificationPreserved();
        public Delegate DirectTarget(nint address, Type type) => inner.DirectTarget(address, type);
        public IPortraitNativePhaseHostHook CreateQualifiedHook(nint address, Type type, Delegate detour, Func<bool> verifyEntry)
        {
            var hook = inner.CreateQualifiedHook(address, type, detour, verifyEntry); var invoke = type.GetMethod("Invoke")!;
            var parameters = invoke.GetParameters().Select(value => value.ParameterType).ToArray(); string? name = null;
            if (invoke.ReturnType == typeof(nint) && parameters.SequenceEqual(new[] { typeof(nint) })) name = nameof(ConstructOriginal);
            if (invoke.ReturnType == typeof(nint) && parameters.SequenceEqual(new[] { typeof(nint), typeof(byte) })) name = nameof(DestroyOriginal);
            return name is null ? hook : new OriginalHook(hook, Delegate.CreateDelegate(type, typeof(OwnedPassiveScalarEventCases).GetMethod(name, BindingFlags.Public | BindingFlags.Static)!));
        }
    }
    private sealed class OriginalHook(IPortraitNativePhaseHostHook inner, Delegate original) : IPortraitNativePhaseHostHook
    {
        public nint Address => inner.Address; public string BackendName => inner.BackendName;
        public bool IsEnabled => inner.IsEnabled; public bool IsDisposed => inner.IsDisposed; public Delegate Original => original;
        public void Enable() => inner.Enable(); public void Disable() => inner.Disable(); public void Dispose() => inner.Dispose();
    }
    private sealed class EnvironmentProof(nint module, bool allowInstallation = false) : IPortraitOwnedSceneHookEnvironment
    {
        public bool IsFrameworkThread => true;
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool TryValidateInstallation(out string reason) { reason = "Test-only installation qualification; no native scene access."; return allowInstallation; }
        public bool IsSourceCurrent(PortraitIdentity source) => false;
        public bool IsSlotIdle(uint slot) => false;
        public bool IsSlotOwnedBy(uint slot, nint view) => false;
        public bool TryReadViewSlot(nint view, out uint slot) { slot = uint.MaxValue; return false; }
    }
}
