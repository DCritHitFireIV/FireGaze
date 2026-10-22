using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

internal static unsafe class PhaseCases
{
    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public static int Run()
    {
        var passed = 0; var failed = 0;
        foreach (var (name, run) in new (string, Action)[] {
            ("ordinary requests queue without reading native memory; post-original source phase grants the lease", QueueAndSourcePhase),
            ("occupied candidate is rejected inside its source phase before allocation", OccupiedAtSourcePhase),
            ("faulted detached request clears the exact queued epoch", FaultedRequestCleanup),
        })
        {
            try { run(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        Console.WriteLine($"PhaseCases: {passed} passed; {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static void QueueAndSourcePhase()
    {
        using var f = new Fixture(); f.SetIdle(6);
        var env = new StrictEnvironment(f); var factory = new Factory();
        using var hooks = new PortraitOwnedSceneHooks(env);
        Check(hooks.TryInstall(factory), hooks.LastError ?? "install failed");
        var calls = 0; IPortraitOwnedSceneLease? captured = null;
        var reads = f.Memory.Reads;
        Check(hooks.TryRequest(f.Phase.Source, 6, lease => {
            Check(factory.OriginalCalls == calls + 1, "source original did not complete first");
            calls++; captured = lease;
            Check(lease.NativeFrame == 733, "native frame argument was replaced");
            Check(lease.CanCreate && lease.IsCurrent, "real reader did not grant the source-phase creation permit");
        }, _ => false), "request was rejected outside the native phase instead of being queued");
        Check(f.Memory.Reads == reads, "request dereferenced native memory before its source callback");
        factory.Render((CharaView*)f.Source, 733);
        Check(calls == 1, hooks.LastError ?? "source callback did not advance the queued request");
        Check(captured is { IsCurrent: false }, "source lease escaped its native phase");
        Check(!env.Active, "environment phase escaped the source callback");
        factory.Render((CharaView*)f.Source, 733);
        Check(calls == 1, "same native frame advanced twice");
    }

    static void OccupiedAtSourcePhase()
    {
        using var f = new Fixture(); f.SetIdle(6);
        var env = new StrictEnvironment(f); var factory = new Factory();
        using var hooks = new PortraitOwnedSceneHooks(env);
        Check(hooks.TryInstall(factory), hooks.LastError ?? "install failed");
        var calls = 0;
        Check(hooks.TryRequest(f.Phase.Source, 6, _ => calls++, _ => false), "detached queue failed");
        f.Entry(6)->BattleChara = f.Actor; f.Entry(6)->ObjectKind = (FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind)1;
        factory.Render((CharaView*)f.Source, 8);
        Check(calls == 0 && factory.OriginalCalls == 1, "occupied candidate issued a permit or swallowed source original");
    }

    static void FaultedRequestCleanup()
    {
        using var f = new Fixture(); f.SetIdle(6);
        var env = new StrictEnvironment(f); var factory = new Factory();
        using var hooks = new PortraitOwnedSceneHooks(env);
        Check(hooks.TryInstall(factory), "install failed");
        Check(hooks.TryRequest(f.Phase.Source, 6, _ => { }, _ => false), "queue failed");
        f.Entry(6)->BattleChara = f.Actor;
        factory.Render((CharaView*)f.Source, 1);
        Check(hooks.TryDispose(), "unbound failed request could not dispose");
        Check(env.QueuedEpoch > 0 && env.EndedEpoch == env.QueuedEpoch,
            $"queued epoch {env.QueuedEpoch} was cleared as {env.EndedEpoch}");
    }

    // RED boundary: real reader + actual SDK fixture, with memory admitted only
    // while the native dispatcher explicitly enters this external phase boundary.
    sealed class StrictEnvironment(Fixture f) : IPortraitOwnedSceneHookEnvironment, IPortraitOwnedSceneHookPhaseEnvironment
    {
        static long nextModule = 0x540000000;
        readonly nint module = (nint)Interlocked.Add(ref nextModule, 0x1000000);
        public bool Active;
        public long QueuedEpoch, EndedEpoch;
        public bool IsFrameworkThread => true;
        public IReadOnlyList<PortraitOwnedSceneNativeTarget> Targets => PortraitOwnedSceneHooks.FrozenTargets(module);
        public bool TryValidateInstallation(out string reason) { reason = ""; return true; }
        public bool IsSourceCurrent(PortraitIdentity source) => Active && source == f.Phase.Source;
        public bool IsSlotIdle(uint slot) => Active && f.Reader.TryReadIdle(slot, f.Phase, out _);
        public bool IsSlotOwnedBy(uint slot, nint view) => false;
        public bool TryReadViewSlot(nint view, out uint slot)
        { slot = f.Source->ClientObjectIndex; return Active && view == f.Phase.Source.Model; }
        public bool QueueRequest(PortraitIdentity source, uint slot, long requestEpoch)
        { QueuedEpoch = requestEpoch; return !Active && source == f.Phase.Source && slot < 8 && requestEpoch > 0; }
        public IDisposable? EnterSourcePhase(PortraitIdentity source, uint frame, long epoch)
        { if (Active || source != f.Phase.Source) return null; Active = true; return new Scope(this); }
        public IDisposable? EnterRetirementPhase(PortraitIdentity source, uint frame, long epoch) => EnterSourcePhase(source, frame, epoch);
        public void EndRequest(long epoch) { EndedEpoch = epoch; }
        sealed class Scope(StrictEnvironment owner) : IDisposable { public void Dispose() => owner.Active = false; }
    }

    internal sealed class Factory : IPortraitOwnedSceneHookFactory
    {
        CharaView.Delegates.Render? render;
        CharaView.Delegates.Release? release;
        CharaView.Delegates.Dtor? destroy;
        CharaView.Delegates.Initialize? initialize;
        public ConstructContext? ConstructDetour;
        public DestroyContext? ContextDestroyDetour;
        public Func<nint, nint>? ConstructOriginal;
        public Func<nint, byte, nint>? ContextDestroyOriginal;
        public int ConstructCalls, ContextDestroyCalls;
        public ConfigureContext? ConfigureDetour;
        public ConfigureDefaultContext? ConfigureDefaultDetour;
        public CleanupContext? CleanupDetour;
        public int ContextCalls;
        public Action<nint, uint, uint>? ContextOriginal;
        public int OriginalCalls;
        public Action<nint, uint>? RenderOriginal;
        public Action<nint>? ReleaseOriginal;
        public Action<nint, byte>? DestroyOriginal;
        public Action<nint, uint>? InitializeOriginal;
        public void Render(CharaView* view, uint frame) => render!(view, frame);
        public void Release(nint view) => release!((CharaView*)view);
        public void Destroy(nint view, byte flags) => destroy!((CharaView*)view, flags);
        public void Initialize(nint view, uint slot) => initialize!((CharaView*)view, null, slot, 0);
        public IPortraitOwnedSceneNativeHook<T> Create<T>(PortraitOwnedSceneNativeTarget target, T detour) where T : Delegate
        {
            if (target.Kind == PortraitOwnedSceneNativeKind.ConstructContext)
            {
                ConstructDetour = (ConstructContext)(object)detour;
                return new Hook<T>((T)(object)(ConstructContext)(c => { ConstructCalls++; return ConstructOriginal?.Invoke(c) ?? c; }));
            }
            if (target.Kind == PortraitOwnedSceneNativeKind.DestroyContext)
            {
                ContextDestroyDetour = (DestroyContext)(object)detour;
                return new Hook<T>((T)(object)(DestroyContext)((c,f) => { ContextDestroyCalls++; return ContextDestroyOriginal?.Invoke(c,f) ?? c; }));
            }
            if (target.Kind == PortraitOwnedSceneNativeKind.ConfigureContext)
            {
                ConfigureDetour = (ConfigureContext)(object)detour;
                return new Hook<T>((T)(object)(ConfigureContext)((c,s,t) => { ContextCalls++; ContextOriginal?.Invoke(c,s,t); return 0xA7; }));
            }
            if (target.Kind == PortraitOwnedSceneNativeKind.ConfigureDefaultContext)
            {
                ConfigureDefaultDetour = (ConfigureDefaultContext)(object)detour;
                return new Hook<T>((T)(object)(ConfigureDefaultContext)((c,s) => { ContextCalls++; ContextOriginal?.Invoke(c,s,0); return 0xB9; }));
            }
            if (target.Kind == PortraitOwnedSceneNativeKind.CleanupContext)
            {
                CleanupDetour = (CleanupContext)(object)detour;
                return new Hook<T>((T)(object)(CleanupContext)(c => { ContextCalls++; ContextOriginal?.Invoke(c,uint.MaxValue,0); }));
            }
            if (target.Kind == PortraitOwnedSceneNativeKind.Render)
            {
                render = (CharaView.Delegates.Render)(object)detour;
                return new Hook<T>((T)(object)(CharaView.Delegates.Render)((v, f) => { OriginalCalls++; RenderOriginal?.Invoke((nint)v, f); }));
            }
            if (target.Kind == PortraitOwnedSceneNativeKind.Release)
            {
                release = (CharaView.Delegates.Release)(object)detour;
                return new Hook<T>((T)(object)(CharaView.Delegates.Release)(v => ReleaseOriginal?.Invoke((nint)v)));
            }
            if (target.Kind == PortraitOwnedSceneNativeKind.Dtor)
            {
                destroy = (CharaView.Delegates.Dtor)(object)detour;
                return new Hook<T>((T)(object)(CharaView.Delegates.Dtor)((v, f) => { DestroyOriginal?.Invoke((nint)v, f); return v; }));
            }
            if (target.Kind == PortraitOwnedSceneNativeKind.Initialize)
            {
                initialize = (CharaView.Delegates.Initialize)(object)detour;
                return new Hook<T>((T)(object)(CharaView.Delegates.Initialize)((v, _, s, _) => InitializeOriginal?.Invoke((nint)v, s)));
            }
            return new Hook<T>(detour);
        }
        sealed class Hook<T>(T original) : IPortraitOwnedSceneNativeHook<T> where T : Delegate
        {
            public T Original => original;
            public bool IsEnabled { get; private set; }
            public bool IsDisposed { get; private set; }
            public void Enable() => IsEnabled = true;
            public void Disable() => IsEnabled = false;
            public void Dispose() { IsEnabled = false; IsDisposed = true; }
        }
    }
}
