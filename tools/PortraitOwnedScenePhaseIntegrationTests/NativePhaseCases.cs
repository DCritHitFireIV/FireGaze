using FireGaze.PortraitPreview;

internal static class NativePhaseCases
{
    static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public static unsafe int Run()
    {
#if !NATIVE_PHASE
        Console.Error.WriteLine("FAIL no real native phase source can qualify scoped reader access or retire old root generations");
        return 1;
#else
        var passed = 0; var failed = 0;
        foreach (var (name, run) in new (string, Action)[] {
            ("construction and default admission perform no native read", DefaultDenied),
            ("native frame and roots are frozen only for their scope", ScopedFrame),
            ("wrong thread can neither capture nor prolong a scope", WrongThread),
            ("root replacement during sampling invalidates the active token", RootReplacement),
            ("same-address reset invalidates and advances manager generation", SameAddressReset),
            ("new session epoch invalidates an otherwise identical source", SessionChanged),
            ("build or Framework qualification loss revokes the current phase", QualificationChanged),
            ("source scope cannot recursively create another scheduling phase", Reentry),
            ("unreadable roots do not leave bootstrap memory admitted", UnreadableRoots),
            ("real reader accepts only the live production phase token", RealReader),
        })
        {
            try { run(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
        }
        Console.WriteLine($"NativePhaseCases: {passed} passed; {failed} failed");
        return failed == 0 ? 0 : 1;
#endif
    }

#if NATIVE_PHASE
    static void DefaultDenied()
    {
        using var f = new Fixture();
        var source = new PortraitOwnedSceneSlotNativePhaseSource(f.Memory);
        Check(f.Memory.Reads == 0 && !source.IsReadScopeActive, "construction touched native memory");
        Check(source.EnterSource(f.Phase.Source, 7, 1) is null && !source.TryCapture(out _), "default production admission opened");
        Check(f.Memory.Reads == 0, "denied entry touched native memory");
    }
    static void ScopedFrame()
    {
        using var f = new Fixture();
        var source = new PortraitOwnedSceneSlotNativePhaseSource(f.Memory, () => true, () => true, () => 9);
        PortraitOwnedSceneSlotPhase first = default;
        using (var scope = source.EnterSource(f.Phase.Source, 0, 12))
        {
            Check(scope is not null && source.TryCapture(out first), "qualified native entry rejected");
            Check(first.NativeFrame == 0 && first.Source == f.Phase.Source && first.Roots == f.Phase.Roots
                && first.SessionEpoch == 9 && first.ManagerGeneration > 0 && first.PhaseToken > 0,
                "source/native frame/root identity changed");
        }
        Check(!source.TryCapture(out _) && !source.IsReadScopeActive, "phase remained live after source return");
        using var next = source.EnterSource(f.Phase.Source, uint.MaxValue, 12);
        Check(next is not null && source.TryCapture(out var second)
            && second.PhaseToken > first.PhaseToken && second.ManagerGeneration == first.ManagerGeneration
            && second.NativeFrame == uint.MaxValue, "new native frame/token did not preserve root generation");
    }
    static void WrongThread()
    {
        using var f = new Fixture();
        var source = new PortraitOwnedSceneSlotNativePhaseSource(f.Memory, () => true, () => true, () => 1);
        var scope = source.EnterSource(f.Phase.Source, 4, 1);
        Check(scope is not null, "scope missing");
        Task.Run(() => { Check(!source.TryCapture(out _) && !source.IsReadScopeActive, "cross-thread access admitted"); scope!.Dispose(); }).GetAwaiter().GetResult();
        Check(!source.TryCapture(out _), "wrong-thread disposal did not revoke escaped scope");
    }
    static void RootReplacement()
    {
        using var f = new Fixture();
        var source = new PortraitOwnedSceneSlotNativePhaseSource(f.Memory, () => true, () => true, () => 1);
        using var scope = source.EnterSource(f.Phase.Source, 4, 1);
        Check(scope is not null && source.TryCapture(out _), "scope missing");
        f.Memory.Roots = f.Memory.Roots with { RenderTargets = (nint)98765 };
        Check(!source.TryCapture(out _) && !source.IsReadScopeActive, "new root silently adopted into old phase");
    }
    static void SameAddressReset()
    {
        using var f = new Fixture();
        var source = new PortraitOwnedSceneSlotNativePhaseSource(f.Memory, () => true, () => true, () => 1);
        var first = source.EnterSource(f.Phase.Source, 4, 1);
        PortraitOwnedSceneSlotPhase previous = default;
        Check(first is not null && source.TryCapture(out previous), "scope missing");
        source.InvalidateManagers();
        Check(!source.TryCapture(out _), "reset left old token live");
        using var next = source.EnterSource(f.Phase.Source, 5, 1);
        Check(next is not null && source.TryCapture(out var current) && current.ManagerGeneration > previous.ManagerGeneration,
            "same roots after reset reused manager generation");
        first!.Dispose();
        Check(source.TryCapture(out _), "stale scope disposal cleared the new token");
    }
    static void SessionChanged()
    {
        using var f = new Fixture(); long session = 3;
        var source = new PortraitOwnedSceneSlotNativePhaseSource(f.Memory, () => true, () => true, () => session);
        using var scope = source.EnterSource(f.Phase.Source, 4, 1);
        session++;
        Check(!source.TryCapture(out _) && !source.IsReadScopeActive, "old session remained admitted");
    }
    static void QualificationChanged()
    {
        using var f = new Fixture(); bool framework = true, build = true;
        var source = new PortraitOwnedSceneSlotNativePhaseSource(f.Memory, () => framework, () => build, () => 1);
        using (var scope = source.EnterSource(f.Phase.Source, 4, 1))
        { build = false; Check(!source.TryCapture(out _) && !source.IsReadScopeActive, "build loss did not revoke"); }
        build = true;
        using (var scope = source.EnterSource(f.Phase.Source, 4, 1))
        { framework = false; Check(!source.TryCapture(out _) && !source.IsReadScopeActive, "Framework loss did not revoke"); }
    }
    static void Reentry()
    {
        using var f = new Fixture();
        var source = new PortraitOwnedSceneSlotNativePhaseSource(f.Memory, () => true, () => true, () => 1);
        using var scope = source.EnterSource(f.Phase.Source, 4, 1);
        Check(source.EnterSource(f.Phase.Source, 4, 1) is null, "recursive source scheduled another phase");
        using var borrowed = source.EnterRetirement(f.Phase.Source, 4, 1);
        PortraitOwnedSceneSlotPhase before = default;
        Check(borrowed is not null && source.TryCapture(out before), "retirement could not borrow live phase");
        borrowed!.Dispose();
        Check(source.TryCapture(out var after) && before == after, "borrowed retirement closed its owner's phase");
    }
    static void UnreadableRoots()
    {
        using var f = new Fixture(); f.Memory.Roots = default;
        var source = new PortraitOwnedSceneSlotNativePhaseSource(f.Memory, () => true, () => true, () => 1);
        Check(source.EnterSource(f.Phase.Source, 4, 1) is null && !source.IsReadScopeActive,
            "failed root bootstrap left memory access enabled");
    }
    static void RealReader()
    {
        using var f = new Fixture(); f.SetIdle(6);
        var source = new PortraitOwnedSceneSlotNativePhaseSource(f.Memory, () => true, () => true, () => 1);
        var reader = new PortraitOwnedSceneSlotReader(f.Memory, source);
        PortraitOwnedSceneSlotPhase phase;
        using (var scope = source.EnterSource(f.Phase.Source, 77, 1))
        { Check(source.TryCapture(out phase) && reader.TryReadIdle(6, phase, out _), "real reader rejected native scope"); }
        Check(!reader.TryReadIdle(6, phase, out _), "real reader accepted expired source token");
    }
#endif
}
