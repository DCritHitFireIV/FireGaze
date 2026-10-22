using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

internal static unsafe class EnvironmentCases
{
    static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public static int Run()
    {
#if !REAL_ENVIRONMENT
        Console.Error.WriteLine("FAIL actual source/idle/owned native reader is not connected to the dispatcher's production environment");
        return 1;
#else
        var passed = 0; var failed = 0;
        foreach (var (name, run) in new (string, Action)[] {
            ("production installation remains denied without native reads or Hook creation", DefaultDenied),
            ("real native source adapter queues then issues a scoped creation permit", QueueToActualSource),
            ("actual active environment resumes new token and preserves prepared owner pin", ActiveCounterOwnerPin),
            ("real owner binding permits initialized-to-prepared exactly once", InitializationUpgrade),
            ("actor allocation ABA does not bind the replacement or release it", ActorReplacement),
            ("prepared camera replacement does not silently rebind the live owner", CameraReplacement),
            ("native root loss during slot sampling does not issue a lease", RootLossDuringRead),
            ("source teardown enters a qualified retirement phase before its native original", SourceRetirement),
            ("foreign native Initialize retires own view before the slot is reused", NativeTakeover),
            ("a validated own native call survives its internal loading-state transition", LoadingInsideOwnedCall),
            ("same-address renderer reset cannot continue an old owned request", ResetBetweenFrames),
        })
        {
            try { run(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        Console.WriteLine($"EnvironmentCases: {passed} passed; {failed} failed");
        return failed == 0 ? 0 : 1;
#endif
    }

#if REAL_ENVIRONMENT
    static void DefaultDenied()
    {
        using var f = new Fixture(); f.SetIdle(6);
        var phases = new PortraitOwnedSceneSlotNativePhaseSource(f.Memory);
        var env = new PortraitOwnedSceneHookEnvironment(f.Memory, phases, () => true, unchecked((nint)0x140000000));
        var factory = new PhaseCases.Factory();
        using var hooks = new PortraitOwnedSceneHooks(env);
        Check(f.Memory.Reads == 0, "environment constructor read native state");
        Check(!hooks.TryInstall(factory) && f.Memory.Reads == 0 && !hooks.IsInstalled, "production admission opened");
    }

    internal sealed class Pair : IDisposable
    {
        static long nextModule = 0x640000000;
        public readonly Fixture F = new();
        public readonly PortraitOwnedSceneSlotNativePhaseSource Phases;
        public readonly PortraitOwnedSceneHookEnvironment Environment;
        public readonly PortraitOwnedSceneHooks Hooks;
        public readonly PhaseCases.Factory Factory = new();
        public IPortraitOwnedSceneLease? Lease;
        public int Calls, RetireCalls;
        public bool RetireEnabled, Destroyed;
        public readonly List<string> Events = [];
        public Action<IPortraitOwnedSceneLease>? Advance;
        public Pair()
        {
            F.SetIdle(6);
            Phases = new(F.Memory, () => true, () => true, () => 1);
            Environment = new(F.Memory, Phases, () => true,
                (nint)Interlocked.Add(ref nextModule, 0x1000000), Validate);
            Hooks = new(Environment);
            Factory.ReleaseOriginal = view => {
                Events.Add(view == (nint)F.Owned ? "own-release" : "source-release");
                if (view != (nint)F.Owned) return;
                *F.Entry(6) = default;
                *(nint*)(F.Phase.Roots.SceneCameras + 0xA0 + 6 * 8) = 0;
                *(int*)F.CountAddress(6) = 0;
                F.Owned->Camera = null; F.Owned->CameraManager = 0;
                F.Owned->ClientObjectId = uint.MaxValue; F.Owned->State = 0;
            };
            Factory.DestroyOriginal = (view, flags) => { Events.Add("own-dtor:" + flags); Destroyed = true; };
            Factory.InitializeOriginal = (view, slot) => Events.Add("native-takeover");
            Check(Hooks.TryInstall(Factory), Hooks.LastError ?? "install failed");
        }
        static bool Validate(out string reason) { reason = "fixture native boundary qualified"; return true; }
        public void Queue()
        {
            Check(Hooks.TryRequest(F.Phase.Source, 6, lease => {
                Lease = lease; Calls++; Advance?.Invoke(lease);
            }, lease => {
                RetireCalls++; Events.Add("retire");
                if (!RetireEnabled) return false;
                using var call = lease.EnterOwnedCall((nint)F.Owned);
                Check(call is not null && lease.TryBeginRetirement((nint)F.Owned), "no retirement permit");
                Factory.Release((nint)F.Owned);
                Check(lease.CanReleaseView((nint)F.Owned), "Release erased its own retirement permit");
                Factory.Destroy((nint)F.Owned, 1);
                return lease.MarkReleased((nint)F.Owned);
            }), "real adapter did not queue the detached request");
        }
        public void Render(uint frame) => Factory.Render((CharaView*)F.Source, frame);
        public void PrepareOwned()
        {
            Check(Factory.ConstructDetour is not null, "native context constructor hook missing");
            Factory.ConstructDetour!(F.Context);
            F.SetOwned(6);
        }
        public void BindInitialized(IPortraitOwnedSceneLease lease)
        {
            Check(lease.TryBindCreatedView((nint)F.Owned), "binding failed");
            using (var ctor = lease.EnterCreationCall((nint)F.Owned, PortraitOwnedSceneCreationCall.Construct))
                Check(ctor is not null && lease.ConfirmConstructed((nint)F.Owned), "construct permit failed");
            using (var setup = lease.EnterCreationCall((nint)F.Owned, PortraitOwnedSceneCreationCall.Setup))
            {
                Check(setup is not null, "setup permit failed");
                F.SetInitialized(6);
                Check(lease.ConfirmSetup((nint)F.Owned), "actual initialized source/slot read rejected setup");
            }
        }
        public void Dispose()
        {
            Hooks.RequestStop();
            if (Lease?.CanAccess((nint)F.Owned) != true) Hooks.TryDispose();
            F.Dispose();
        }
    }

    static void ActiveCounterOwnerPin()
    {
        using var f = new Fixture(); f.SetIdle(6);
        var phases = new PortraitOwnedSceneSlotNativePhaseSource(f.Memory, () => true, () => true, () => 1);
        var env = new PortraitOwnedSceneHookEnvironment(f.Memory, phases, () => true, unchecked((nint)0x710000000), Validate);
        Check(env.QueueRequest(f.Phase.Source, 6, 1), "actual active request queue failed");
        using var originalScope = env.EnterSourcePhase(f.Phase.Source, 90, 1);
        Check(originalScope is not null && env.TryQualifyActiveSource(f.Phase.Source, 90, 1, 6, out var renderer), "actual complete eight-slot qualification failed");
        env.TryQualifyActiveSource(f.Phase.Source, 90, 1, 6, out renderer);
        env.BeginContextLifetimeBatch();
        env.ObserveContextConstructed(f.Context, f.Context);
        f.SetOwned(6);
        Check(env.IsSlotOwnedBy(6, (nint)f.Owned), "actual prepared context ownership did not pin");
        phases.TryCapture(out var before);
        using (env.BeginOwnedCounterMutation())
            Check(!env.IsSourceCurrent(f.Phase.Source) && !phases.TryCapture(out _), "actual environment allowed native reads during own counter Original");
        using var resumed = env.ResumeOwnedCounterPhase(f.Phase.Source, 90, 1, renderer);
        Check(resumed is not null && env.IsSlotOwnedBy(6, (nint)f.Owned) && phases.TryCapture(out var after)
            && after.PhaseToken != before.PhaseToken && after.ManagerGeneration == before.ManagerGeneration, "actual environment failed exact owner/token revalidation");
        originalScope!.Dispose(); Check(env.IsSourceCurrent(f.Phase.Source), "old source scope closed the replacement token");
        using (env.BeginOwnedCounterMutation()) { *(uint*)((byte*)f.Actor + 0x74) = 38; }
        using var replaced = env.ResumeOwnedCounterPhase(f.Phase.Source, 90, 1, renderer);
        Check(replaced is not null && !env.IsSlotOwnedBy(6, (nint)f.Owned), "actual environment silently rebound same-address character allocation after a counter");
        env.EndRequest(1);
        static bool Validate(out string reason) { reason = "self-allocated SDK fixture"; return true; }
    }
    static void QueueToActualSource()
    {
        using var p = new Pair();
        var before = p.F.Memory.Reads;
        p.Advance = lease => Check(lease.CanCreate && lease.NativeFrame == 112, "native phase permit missing");
        p.Queue();
        Check(p.F.Memory.Reads == before, "real environment read outside a native phase");
        p.Render(112);
        Check(p.Calls == 1 && p.Lease is { IsCurrent: false } && !p.Phases.TryCapture(out _),
            p.Hooks.LastError ?? "real adapter failed or leaked its lease");
    }
    static void InitializationUpgrade()
    {
        using var p = new Pair();
        p.Advance = lease => {
            if (lease.CanCreate) p.BindInitialized(lease);
            Check(lease.CanAccess((nint)p.F.Owned), "owned native slot binding rejected");
            if (p.Calls == 1)
            {
                p.PrepareOwned();
                Check(lease.CanAccess((nint)p.F.Owned), "initial context/camera upgrade rejected");
            }
        };
        p.Queue(); p.Render(1); p.Render(2);
        Check(p.Calls == 2 && p.Hooks.LastError is null, p.Hooks.LastError ?? "second frame rejected own pin");
    }
    static void ActorReplacement()
    {
        using var p = new Pair();
        p.Advance = lease => { if (lease.CanCreate) p.BindInitialized(lease); };
        p.Queue(); p.Render(1);
        *((uint*)((byte*)p.F.Actor + 0x74)) = 38;
        p.Render(2);
        Check(p.Calls == 1 && p.RetireCalls == 0 && p.Hooks.LastError is not null,
            "actor replacement was adopted or given stale retirement");
    }
    static void CameraReplacement()
    {
        using var p = new Pair();
        p.Advance = lease => { if (lease.CanCreate) { p.BindInitialized(lease); p.PrepareOwned(); Check(lease.CanAccess((nint)p.F.Owned), "upgrade failed"); } };
        p.Queue(); p.Render(1);
        p.PrepareOwned();
        p.Render(2);
        Check(p.Calls == 1 && p.RetireCalls == 0, "replacement context/camera silently became owned");
    }
    static void RootLossDuringRead()
    {
        using var p = new Pair(); p.Queue(); var once = false;
        p.F.Memory.AfterRead = _ => { if (!once) { once = true; p.F.Memory.Roots = default; } };
        p.Render(1);
        Check(p.Calls == 0 && p.Factory.OriginalCalls == 1 && !p.Phases.TryCapture(out _),
            "mixed native roots granted a source phase or swallowed original");
    }
    static void SourceRetirement()
    {
        using var p = new Pair();
        p.Advance = lease => { if (lease.CanCreate) p.BindInitialized(lease); };
        p.RetireEnabled = true; p.Queue(); p.Render(1);
        var afterFreeReads = 0;
        p.F.Memory.AfterRead = address => { if (p.Destroyed && address >= (nint)p.F.Owned && address < (nint)p.F.Owned + 0x430) afterFreeReads++; };
        p.Factory.Release((nint)p.F.Source);
        Check(string.Join(",", p.Events) == "retire,own-release,own-dtor:1,source-release",
            "source teardown was not preceded by exact owned retirement: " + string.Join(",", p.Events));
        Check(afterFreeReads == 0 && !p.Phases.TryCapture(out _) && p.Hooks.TryDispose(),
            "retirement read destroyed view, escaped scope or retained active hooks");
    }
    static void NativeTakeover()
    {
        using var p = new Pair();
        p.Advance = lease => { if (lease.CanCreate) p.BindInitialized(lease); };
        p.RetireEnabled = true; p.Queue(); p.Render(1);
        p.Factory.Initialize((nint)p.F.Actor, 6);
        Check(string.Join(",", p.Events) == "retire,own-release,own-dtor:1,native-takeover",
            "native takeover ran before owned retirement: " + string.Join(",", p.Events));
        Check(p.Hooks.TryDispose(), "retired takeover retained the hook batch");
    }
    static void LoadingInsideOwnedCall()
    {
        using var p = new Pair();
        p.Factory.RenderOriginal = (view, frame) => {
            if (view != (nint)p.F.Owned) return;
            // Native Render really passes through State5 while constructing its
            // context and camera. A nested qualified native entry may observe
            // partial own state before the enclosing Original completes.
            p.F.Owned->State = 5; p.F.Owned->CameraManager = p.F.Context;
            p.Factory.Initialize(view, 6);
            p.PrepareOwned();
        };
        p.Advance = lease => {
            p.BindInitialized(lease);
            using var call = lease.EnterOwnedCall((nint)p.F.Owned);
            Check(call is not null, "own call had no validated entry token");
            p.Factory.Render((CharaView*)p.F.Owned, lease.NativeFrame);
            Check(lease.CanAccess((nint)p.F.Owned), "transient own state was mistaken for an external takeover");
        };
        p.Queue(); p.Render(1);
        Check(p.Calls == 1 && p.RetireCalls == 0 && p.Hooks.LastError is null,
            p.Hooks.LastError ?? "own context loading tried to retire an active Original");
    }
    static void ResetBetweenFrames()
    {
        using var p = new Pair();
        p.Advance = lease => { if (lease.CanCreate) p.BindInitialized(lease); };
        p.Queue(); p.Render(1);
        p.Phases.InvalidateManagers();
        p.Render(2);
        Check(p.Calls == 1 && p.RetireCalls == 0 && p.Hooks.LastError is not null,
            "old request was reused after renderer reset with identical native addresses");
    }
#endif
}
