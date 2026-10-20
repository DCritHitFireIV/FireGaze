using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

internal static unsafe class OwnedPhysicalCases
{
    private static int checks, originals;
    private static Action? duringNative;
    private static long module = 0x7400000000;
    private static readonly PortraitIdentity Source = new(11, 22, 33, 7, 44);
    internal static int Run()
    {
        try
        {
            Qualification(); Passive(); NewOwnerBaseline(); ExpiryWithoutPolling(); StopAndPartialEnable(); CallbackDrain(); AsyncBatchDrain(); UnknownCreate();
            Console.WriteLine($"PASS {checks} owned physical transport checks"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
    private static EnvironmentProof NewEnvironment() => new((nint)Interlocked.Add(ref module, 0x4000000));
    private static bool Reserved(EnvironmentProof environment) => environment.Targets.All(target => new PortraitNativePhaseHostReservations().IsReserved(target.Address));
    private static bool Released(EnvironmentProof environment) => environment.Targets.All(target => !new PortraitNativePhaseHostReservations().IsReserved(target.Address));

    private static void Qualification()
    {
        var environment = NewEnvironment(); var provider = new Provider { Qualified = false }; var proof = new Proof();
        var factory = new DalamudPortraitOwnedSceneHookFactory(provider, proof);
        using var hooks = new PortraitOwnedSceneHooks(environment);
        Check(!hooks.TryInstallPassive(factory, TimeSpan.FromSeconds(1)) && provider.Created.Count == 0 && Released(environment),
            "unqualified official provider cannot reserve/create passive owned hooks");
        provider.Qualified = true; proof.Valid = false;
        Check(!hooks.TryInstallPassive(factory, TimeSpan.FromSeconds(1)) && provider.Created.Count == 0 && Released(environment),
            "absent entry proof cannot reserve/create passive owned hooks");
    }
    private static void Passive()
    {
        var environment = NewEnvironment(); var provider = new Provider(); var proof = new Proof(); var now = 0L;
        var factory = new DalamudPortraitOwnedSceneHookFactory(provider, proof);
        using var hooks = new PortraitOwnedSceneHooks(environment, timestamp: () => now, timestampFrequency: 1000);
        provider.OnEnable = () => Check(!hooks.IsInstalled && !hooks.TryRequest(Source, 6, _ => throw new Exception("grant"), _ => true),
            "a partial passive Enable cannot publish installation/admission");
        Check(hooks.TryInstallPassive(factory, TimeSpan.FromSeconds(1)) && hooks.IsInstalled && environment.AdmissionChecks == 0,
            hooks.LastError ?? "qualified explicit passive transport did not install independently of scene admission");
        Check(provider.Created.Count == 39 && Reserved(environment) && proof.Verifications >= 80,
            "all 39 entries must share reservation and repeat entry proof before Create/Enable");
        Check(!hooks.TryRequest(Source, 6, _ => throw new Exception("grant"), _ => true),
            "fully installed passive transport cannot request a scene even with permissive non-scoped environment");
        now = 1000; hooks.UpdatePassive();
        Check(!hooks.IsInstalled && hooks.DrainPhysicalAsync().IsCompletedSuccessfully && Released(environment)
            && provider.Created.All(hook => hook.IsDisposed), "passive deadline must physically retire the complete batch");
        Check(!hooks.TryInstallPassive(factory, TimeSpan.FromSeconds(1)), "closed generation cannot be reused");
    }
    private static void CallbackDrain()
    {
        var environment = NewEnvironment(); var provider = new Provider(); var factory = new DalamudPortraitOwnedSceneHookFactory(provider, new Proof());
        Check(factory.TryQualifyTransport(environment.Targets, out var reason), reason);
        var target = environment.Targets.Single(target => target.Kind == PortraitOwnedSceneNativeKind.Render);
        foreach (var pause in new[] { "prefix", "completion" })
        {
            originals = 0;
            using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
            IPortraitOwnedSceneNativeHook<CharaView.Delegates.Render>? hook = null;
            CharaView.Delegates.Render dispatcher = (view, frame) => {
                if (pause == "prefix") { entered.Set(); finish.Wait(); }
                hook!.Original(view, frame);
                if (pause == "completion") { entered.Set(); finish.Wait(); }
            };
            hook = factory.Create(target, dispatcher); hook.Enable();
            var physical = (IPortraitOwnedScenePhysicalHook)hook; var native = provider.Created.Last();
            var dispatch = (CharaView.Delegates.Render)Delegate.CreateDelegate(typeof(CharaView.Delegates.Render), native.Detour.Target, native.Detour.Method);
            var running = Task.Run(() => dispatch(null, 123));
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "typed callback failed to enter " + pause);
            Check(physical.InFlight == 1 && physical.BeginRetirement(out _) && !physical.TryCompleteRetirement(out _)
                && !native.IsDisposed, "physical lifetime must cover " + pause + " before trampoline disposal");
            var enableRejected = false;
            try { hook.Enable(); } catch (InvalidOperationException) { enableRejected = true; }
            Check(enableRejected && !hook.IsEnabled, "close permanently refuses Enable while a callback drains");
            finish.Set(); Check(running.Wait(TimeSpan.FromSeconds(5)) && originals == 1, "Original exactly once across typed forwarding");
            Check(physical.TryCompleteRetirement(out _) && native.IsDisposed && physical.InFlight == 0,
                "callback return must permit trampoline retirement");
            dispatch(null, 124);
            Check(originals == 2, "late stable entry must call only direct target");
            var staleOriginalRejected = false;
            try { _ = hook.Original; } catch (InvalidOperationException) { staleOriginalRejected = true; }
            Check(staleOriginalRejected, "disposed wrapper cannot retain/publish a typed trampoline");
        }
    }
    private static void StopAndPartialEnable()
    {
        foreach (var partial in new[] { false, true })
        {
            var environment = NewEnvironment(); var provider = new Provider();
            var factory = new DalamudPortraitOwnedSceneHookFactory(provider, new Proof()); using var hooks = new PortraitOwnedSceneHooks(environment);
            if (partial) provider.OnEnable = hooks.RequestStop;
            var installed = hooks.TryInstallPassive(factory, TimeSpan.FromSeconds(1));
            if (!partial) hooks.RequestStop();
            hooks.UpdatePassive();
            Check(installed != partial && !hooks.IsInstalled && hooks.DrainPhysicalAsync().IsCompletedSuccessfully
                && Released(environment) && provider.Created.All(hook => hook.IsDisposed),
                "explicit stop during " + (partial ? "partial Enable" : "passive observation") + " must close and drain the complete generation");
        }
    }
    private static void NewOwnerBaseline()
    {
        var environment = NewEnvironment(); var provider = new Provider(); var nativeThread = new PortraitNativeThreadStamp(123, 456);
        var factory = new DalamudPortraitOwnedSceneHookFactory(provider, new Proof());
        using var hooks = new PortraitOwnedSceneHooks(environment, timestamp: () => 0, timestampFrequency: 1000, nativeThread: () => nativeThread);
        Check(!hooks.TryArmPassiveSourceBaseline(Source, 7, nativeThread, TimeSpan.FromSeconds(1)), "uninstalled owner cannot arm source return baseline");
        provider.OnEnable = () => Check(!hooks.TryArmPassiveSourceBaseline(Source, 7, nativeThread, TimeSpan.FromSeconds(1)),
            "partial Enable cannot arm a source baseline");
        Check(hooks.TryInstallPassive(factory, TimeSpan.FromSeconds(1)), hooks.LastError ?? "baseline transport install");
        var native = provider.Created.Single(hook => hook.Address == environment.Targets.Single(target => target.Kind == PortraitOwnedSceneNativeKind.Render).Address);
        var dispatch = (CharaView.Delegates.Render)Delegate.CreateDelegate(typeof(CharaView.Delegates.Render), native.Detour.Target, native.Detour.Method);
        dispatch((CharaView*)Source.Model, uint.MaxValue);
        Check(hooks.PassiveSourceBaseline is null, "unarmed new-owner return cannot become a stored baseline");
        // Arm while an earlier callback is already inside Original. Its later return
        // cannot satisfy the requirement for a future invocation of this new owner.
        duringNative = () => Check(hooks.TryArmPassiveSourceBaseline(Source, 7, nativeThread, TimeSpan.FromSeconds(1)), "baseline arms after complete installation");
        dispatch((CharaView*)Source.Model, uint.MaxValue); duringNative = null;
        Check(hooks.PassiveSourceBaseline?.State == PortraitOwnedScenePassiveBaselineState.Waiting, "callback begun before Arm cannot publish baseline after Original");
        dispatch((CharaView*)(Source.Model + 1), 1);
        Check(hooks.PassiveSourceBaseline?.State == PortraitOwnedScenePassiveBaselineState.Waiting, "foreign source return cannot satisfy exact view baseline");
        dispatch((CharaView*)Source.Model, 0);
        var baseline = hooks.PassiveSourceBaseline;
        Check(baseline is { State: PortraitOwnedScenePassiveBaselineState.Observed, NativeFrame: 0, ProductionAdmission: false }
            && baseline.Source == Source && baseline.SessionEpoch == 7 && baseline.Thread == nativeThread && baseline.Installation != Guid.Empty,
            "fresh actual new-owner source Return records native frame zero, exact source/session/thread without scene admission");
        Check(!hooks.TryArmPassiveSourceBaseline(Source, 7, nativeThread, TimeSpan.FromSeconds(1)), "one installed owner never rearms or reuses historical baseline");
        hooks.TryDispose();
        Check(hooks.PassiveSourceBaseline?.State == PortraitOwnedScenePassiveBaselineState.Revoked, "retirement revokes new-owner baseline");
        dispatch((CharaView*)Source.Model, 1);
        Check(hooks.PassiveSourceBaseline?.State == PortraitOwnedScenePassiveBaselineState.Revoked, "late stable direct entry cannot revive retired baseline");
    }
    private static void ExpiryWithoutPolling()
    {
        foreach (var duration in new[] { TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(20) })
        {
        var environment = NewEnvironment(); var provider = new Provider();
        var factory = new DalamudPortraitOwnedSceneHookFactory(provider, new Proof()); using var hooks = new PortraitOwnedSceneHooks(environment);
        var installed = hooks.TryInstallPassive(factory, duration);
        Check(installed || duration < TimeSpan.FromMilliseconds(1), hooks.LastError ?? "passive no-poll expiry installs");
        Check(hooks.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && !hooks.IsInstalled && Released(environment)
            && provider.Created.All(hook => hook.IsDisposed), "passive expiry closes and physically drains without a Framework poll: " + duration);
        }
    }
    private static void AsyncBatchDrain()
    {
        var environment = NewEnvironment(); var provider = new Provider(); var factory = new DalamudPortraitOwnedSceneHookFactory(provider, new Proof());
        using var hooks = new PortraitOwnedSceneHooks(environment);
        Check(hooks.TryInstallPassive(factory, TimeSpan.FromSeconds(1)), hooks.LastError ?? "async physical batch installation");
        provider.OnDisable = () => Check(provider.Created.All(hook => hook.Detour.Target!.GetType().GetField("Observer")!.GetValue(hook.Detour.Target) is null),
            "all 39 plugin forwards must clear before the first native Disable");
        var native = provider.Created.Single(hook => hook.Address == environment.Targets.Single(target => target.Kind == PortraitOwnedSceneNativeKind.Render).Address);
        var dispatch = (CharaView.Delegates.Render)Delegate.CreateDelegate(typeof(CharaView.Delegates.Render), native.Detour.Target, native.Detour.Method);
        using var entered = new ManualResetEventSlim(); using var finish = new ManualResetEventSlim();
        duringNative = () => { entered.Set(); finish.Wait(); };
        var running = Task.Run(() => dispatch(null, 0));
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "full batch Original must enter");
        Check(!hooks.TryDispose() && !hooks.DrainPhysicalAsync().IsCompleted && Reserved(environment)
            && provider.Created.All(hook => !hook.IsEnabled) && !native.IsDisposed,
            "physical batch stop detaches/disables first, retains all 39 reservations and provider until callback return");
        finish.Set(); Check(running.Wait(TimeSpan.FromSeconds(5)), "full batch Original returned"); duringNative = null;
        Check(hooks.DrainPhysicalAsync().Wait(TimeSpan.FromSeconds(5)) && Released(environment) && provider.Created.All(hook => hook.IsDisposed),
            "retained async physical task releases complete batch only after last callback");
        dispatch(null, 1);
    }
    private static void UnknownCreate()
    {
        var environment = NewEnvironment(); var provider = new Provider { FailCreateAt = 3 };
        var factory = new DalamudPortraitOwnedSceneHookFactory(provider, new Proof()); using var hooks = new PortraitOwnedSceneHooks(environment);
        Check(!hooks.TryInstallPassive(factory, TimeSpan.FromSeconds(1)) && Reserved(environment), "unknown physical Create isolates all 39 addresses");
        Check(!hooks.TryDispose() && !hooks.DrainPhysicalAsync().IsCompleted && provider.Created.All(hook => hook.IsDisposed),
            "unknown physical Create keeps provider drain incomplete while returned handles retire");
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RenderOriginal(CharaView* view, uint frame) { Interlocked.Increment(ref originals); duringNative?.Invoke(); }

    private sealed class EnvironmentProof(nint address) : IPortraitOwnedSceneHookEnvironment
    {
        public int AdmissionChecks;
        public bool IsFrameworkThread => true;
        public bool TryValidateInstallation(out string reason) { AdmissionChecks++; reason = "scene creation deliberately denied"; return false; }
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(address);
        public bool IsSourceCurrent(PortraitIdentity candidate) => candidate == Source;
        public bool IsSlotIdle(uint slot) => true;
        public bool IsSlotOwnedBy(uint slot, nint view) => false;
        public bool TryReadViewSlot(nint view, out uint slot) { slot = 4; return true; }
    }
    internal sealed class Proof : IPortraitOwnedSceneTransportProof
    {
        public bool Valid = true;
        public int Verifications;
        public bool TryValidate(IReadOnlyList<PortraitOwnedSceneNativeTarget> targets, out string reason) { reason = "detached exact entry proof"; return Valid; }
        public bool VerifyEntry(PortraitOwnedSceneNativeTarget target) { Verifications++; return Valid; }
    }
    internal sealed class Provider : IPortraitQualifiedNativeHookProvider
    {
        public bool Qualified = true;
        public int FailCreateAt = -1;
        public Action? OnEnable, OnDisable;
        public List<NativeHook> Created { get; } = [];
        public string Identity => "test-bound external official provider qualification";
        public bool TryQualifyStartDisabled(out string reason) { reason = Identity; return Qualified; }
        public bool QualificationPreserved() => Qualified;
        public Delegate DirectTarget(nint address, Type type) => OriginalFor(type);
        public IPortraitNativePhaseHostHook CreateQualifiedHook(nint address, Type type, Delegate detour, Func<bool> verifyEntry)
        {
            Check(Qualified && verifyEntry() && !type.Assembly.IsCollectible && type == detour.GetType(), "stable exact qualified native Create");
            if (Created.Count == FailCreateAt) throw new InvalidOperationException("unknown native Create");
            var original = OriginalFor(type);
            var hook = new NativeHook(address, detour, original, verifyEntry, () => OnEnable?.Invoke(), () => OnDisable?.Invoke()); Created.Add(hook); return hook;
        }
        private static Delegate OriginalFor(Type type)
        {
            Delegate original;
            var invoke = type.GetMethod("Invoke")!;
            if (invoke.ReturnType == typeof(void) && invoke.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(CharaView*), typeof(uint) }))
                original = Marshal.GetDelegateForFunctionPointer((nint)(delegate* unmanaged[Stdcall]<CharaView*, uint, void>)&RenderOriginal, type);
            else
            {
                var method = new DynamicMethod("native_fixture", invoke.ReturnType, invoke.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
                var il = method.GetILGenerator();
                if (invoke.ReturnType != typeof(void)) { il.Emit(OpCodes.Ldc_I4_0); if (invoke.ReturnType.IsPointer || invoke.ReturnType == typeof(nint)) il.Emit(OpCodes.Conv_I); }
                il.Emit(OpCodes.Ret); original = method.CreateDelegate(type);
            }
            return original;
        }
    }
    internal sealed class NativeHook(nint address, Delegate detour, Delegate original, Func<bool> verifyEntry, Action enabled, Action disabling) : IPortraitNativePhaseHostHook
    {
        public Delegate Detour => detour;
        public nint Address => address;
        public string BackendName => "SafetyHook";
        public bool IsEnabled { get; private set; }
        public bool IsDisposed { get; private set; }
        public Delegate Original => original;
        public void Enable() { Check(verifyEntry(), "actual per-Enable entry proof"); enabled(); IsEnabled = true; }
        public void Disable() { disabling(); IsEnabled = false; }
        public void Dispose() { Check(!IsEnabled, "cannot dispose enabled native trampoline"); IsDisposed = true; }
    }
}
