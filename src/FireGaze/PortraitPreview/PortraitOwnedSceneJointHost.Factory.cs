using Dalamud.Plugin.Services;

namespace FireGaze.PortraitPreview;

internal delegate bool PortraitOwnedSceneJointResolve(PortraitNativePhaseTargetSet set,
    Func<nint, bool> reserved, out PortraitNativePhaseResolvedPlan? plan, out string reason);
internal interface IPortraitOwnedSceneJointHookFactory : IPortraitOwnedSceneQualifiedHookFactory
{
    bool TryQualifyJointTransport(PortraitOwnedSceneJointPlan plan, Func<nint, bool> reserved, out string reason);
    bool TryVerifyJointTargets(PortraitOwnedSceneJointPlan plan, out string reason);
    IPortraitOwnedSceneNativeHook CreateJoint(PortraitNativePhaseBoundTarget target, PortraitOwnedSceneJointIngress ingress);
}

/// <summary>Same official provider and original owned39 proof, plus the existing audited phase resolvers.</summary>
internal sealed class DalamudPortraitOwnedSceneJointHookFactory : IPortraitOwnedSceneJointHookFactory
{
    private readonly DalamudPortraitOwnedSceneHookFactory consumers;
    private readonly IPortraitNativePhaseHostFactory phases;
    private readonly PortraitOwnedSceneJointResolve resolve;
    private readonly List<JointEntry> retained = [];
    private PortraitOwnedSceneJointPlan? qualified;
    private PortraitNativePhaseResolvedPlan[] resolved = [];
    internal DalamudPortraitOwnedSceneJointHookFactory(IGameInteropProvider interop)
        : this(new DalamudPortraitNativePhaseHostFactory(interop), new PortraitOwnedSceneCurrentTransportProof()) { }
    private DalamudPortraitOwnedSceneJointHookFactory(DalamudPortraitNativePhaseHostFactory provider, IPortraitOwnedSceneTransportProof proof)
        : this(provider, proof, provider, ResolveCurrent) { }
    internal DalamudPortraitOwnedSceneJointHookFactory(IPortraitQualifiedNativeHookProvider provider,
        IPortraitOwnedSceneTransportProof proof, IPortraitNativePhaseHostFactory phases, PortraitOwnedSceneJointResolve resolve)
    { consumers = new(provider, proof); this.phases = phases; this.resolve = resolve; }
    public string Identity => consumers.Identity;
    public bool TryQualifyTransport(IReadOnlyList<PortraitOwnedSceneNativeTarget> targets, out string reason)
        => consumers.TryQualifyTransport(targets, out reason);
    public bool TryVerifyUnmodifiedTargets(IReadOnlyList<PortraitOwnedSceneNativeTarget> targets, out string reason)
        => consumers.TryVerifyUnmodifiedTargets(targets, out reason);
    public IPortraitOwnedSceneNativeHook<T> Create<T>(PortraitOwnedSceneNativeTarget target, T detour) where T : Delegate
        => consumers.Create(target, detour);

    public bool TryQualifyJointTransport(PortraitOwnedSceneJointPlan plan, Func<nint, bool> reserved, out string reason)
    {
        qualified = null; resolved = [];
        var candidates = new List<PortraitNativePhaseResolvedPlan>();
        reason = "Joint phase identities or exact bound inventories differ from the frozen plan.";
        foreach (var set in Enum.GetValues<PortraitNativePhaseTargetSet>())
        {
            if (!resolve(set, reserved, out var candidate, out reason) || !Valid(candidate, plan, set)) return false;
            candidates.Add(candidate!);
        }
        if (candidates.Select(p => (p.ModuleBase, p.ModuleSize, p.OnDiskEXESHA256, p.OnDiskSDKSHA256, p.SDKMVID)).Distinct().Count() != 1)
        { reason = "Joint resolvers did not observe the same module and SDK identity."; return false; }
        resolved = candidates.ToArray(); qualified = plan;
        return TryVerifyJointTargets(plan, out reason);
    }
    public bool TryVerifyJointTargets(PortraitOwnedSceneJointPlan plan, out string reason)
    {
        reason = "Joint phase qualification or live entries changed.";
        if (!ReferenceEquals(qualified, plan) || resolved.Length != 3) return false;
        foreach (var candidate in resolved)
            if (!phases.TryVerifyUnmodifiedTargets(candidate, out reason)) return false;
        return true;
    }
    public IPortraitOwnedSceneNativeHook CreateJoint(PortraitNativePhaseBoundTarget target, PortraitOwnedSceneJointIngress ingress)
    {
        if (qualified is null || !resolved.SelectMany(p => p.Targets).Contains(target)
            || target.Phase == PortraitNativeObservedPhase.SourceCharaViewRender || !TryVerifyJointTargets(qualified, out var reason))
            throw new InvalidOperationException("An extra joint entry was not qualified before Create.");
        var invoke = target.DelegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(p => p.ParameterType).ToArray();
        PortraitNativePhaseHostGateway? gateway;
        var created = target.Phase is PortraitNativeObservedPhase.ResourceCleanup or PortraitNativeObservedPhase.ResourceRecreate
            ? PortraitNativePhaseHostGateway.TryCreateResource((int)target.Phase, parameters, invoke.ReturnType, target.Address, out gateway, out reason)
            : PortraitNativePhaseHostGateway.TryCreate((int)target.Phase, parameters, target.Address, out gateway, out reason);
        if (!created) throw new InvalidOperationException(reason);
        var entry = new JointEntry(phases, target, gateway!, ingress, () => VerifyTarget(target));
        retained.Add(entry);
        try { entry.Create(); retained.Remove(entry); return entry; }
        catch { entry.BeginRetirement(out _); if (entry.TryCompleteRetirement(out _)) retained.Remove(entry); throw; }
    }
    private bool VerifyTarget(PortraitNativePhaseBoundTarget target)
    {
        var plan = resolved.SingleOrDefault(p => p.Targets.Contains(target));
        // Entries enabled earlier are now legitimately patched. Per-Enable
        // verification reads only this still-disabled target, under the same
        // qualified identity; the complete batch was checked before Enable.
        return qualified is not null && plan is not null
            && phases.TryVerifyUnmodifiedTargets(plan with { Targets = Array.AsReadOnly(new[] { target }) }, out _);
    }
    private static bool Valid(PortraitNativePhaseResolvedPlan? value, PortraitOwnedSceneJointPlan joint, PortraitNativePhaseTargetSet set)
        => value is not null && value.ModuleBase == joint.ModuleBase && value.ModuleSize == PortraitOwnedSceneCurrentTransportProof.FrozenImageSize
            && value.OnDiskEXESHA256 == PortraitNativePhaseObservationBinding.EXE && value.OnDiskSDKSHA256 == PortraitNativePhaseObservationBinding.SDK
            && value.SDKMVID != Guid.Empty && value.Targets.SequenceEqual(PortraitNativeCounterIngressContract.For(set)
                .Select(c => new PortraitNativePhaseBoundTarget(c.Phase, joint.ModuleBase + (nint)c.RVA, c.Delegate)))
            && value.Targets.All(t => joint.Targets.Any(j => j.Address == t.Address && j.DelegateType == t.DelegateType
                && j.Roles.Contains(new(null, t.Phase))));
    private static bool ResolveCurrent(PortraitNativePhaseTargetSet set, Func<nint, bool> reserved,
        out PortraitNativePhaseResolvedPlan? plan, out string reason)
        => new PortraitNativePhaseObservationResolver(new PortraitNativePhaseCurrentProcess(reserved), set).TryResolve(out plan, out reason);

    // Existing phase gateways supply the exact typed native detours (including AL)
    // and count the complete observer/Original/finally interval. This adapter joins
    // them to the owned owner's existing all-entry physical retirement protocol.
    private sealed class JointEntry(IPortraitNativePhaseHostFactory factory, PortraitNativePhaseBoundTarget target,
        PortraitNativePhaseHostGateway gateway, PortraitOwnedSceneJointIngress ingress, Func<bool> verify)
        : IPortraitOwnedSceneNativeHook, IPortraitOwnedScenePhysicalHook
    {
        private IPortraitNativePhaseHostHook? hook;
        private Delegate? direct;
        private bool attempted, retired;
        private int closing, operation;
        private string? retirementFailure;
        public bool IsEnabled => hook?.IsEnabled == true;
        public bool IsDisposed => retired;
        public int InFlight => gateway.ActiveCount;
        public bool RetirementFaulted => Volatile.Read(ref retirementFailure) is not null;
        internal void Create()
        {
            direct = factory.DirectTarget(target, gateway.DelegateType);
            attempted = true;
            hook = factory.Create(target, gateway.DelegateType, gateway.Detour);
            if (hook.Address != target.Address || hook.BackendName != "SafetyHook" || hook.IsEnabled || hook.IsDisposed)
                throw new InvalidOperationException("Joint official entry did not start disabled under the exact target.");
            gateway.PublishOriginal(hook.Original);
            if (target.Phase is PortraitNativeObservedPhase.ResourceCleanup or PortraitNativeObservedPhase.ResourceRecreate)
                gateway.SetResourceObserver(ingress.ObserveResource);
            else gateway.SetObserver(ingress.Observe);
        }
        public void Enable()
        {
            if (Interlocked.CompareExchange(ref operation, 1, 0) != 0) throw new InvalidOperationException("Joint hook operation is already active.");
            try
            {
                if (Volatile.Read(ref closing) != 0 || retired || !verify()) throw new InvalidOperationException("Joint target changed before Enable.");
                hook!.Enable();
                if (Volatile.Read(ref closing) != 0) throw new InvalidOperationException("Joint entry closed during Enable.");
            }
            finally { Volatile.Write(ref operation, 0); if (Volatile.Read(ref closing) != 0) BeginRetirement(out _); }
        }
        public void DetachForward() { Interlocked.Exchange(ref closing, 1); gateway.DetachObserver(); }
        public bool BeginRetirement(out string reason)
        {
            reason = "Waiting for the joint native callback to return."; DetachForward();
            if (Interlocked.CompareExchange(ref operation, 1, 0) != 0) return false;
            try
            {
                if (retirementFailure is { } fault) { reason = fault; return false; }
                if (hook is { IsEnabled: true, IsDisposed: false }) hook.Disable();
                if (hook is { IsEnabled: true }) throw new InvalidOperationException("Joint hook did not disable.");
                return true;
            }
            catch (Exception error) { reason = LatchRetirementFailure(error.Message); return false; }
            finally { Volatile.Write(ref operation, 0); }
        }
        public bool TryCompleteRetirement(out string reason)
        {
            reason = "Waiting for complete physical joint retirement.";
            if (retired) return true;
            if (Volatile.Read(ref closing) == 0 || Interlocked.CompareExchange(ref operation, 1, 0) != 0) return false;
            try
            {
                if (retirementFailure is { } fault) { reason = fault; return false; }
                if (hook is null && attempted) { reason = LatchRetirementFailure("Unknown official joint Create compensation."); return false; }
                if (!attempted)
                {
                    retired = gateway.TryAbandonUnpublishedOwnership();
                    if (!retired) reason = LatchRetirementFailure("Unpublished joint gateway release was not confirmed.");
                    return retired;
                }
                if (hook is { IsEnabled: true } || direct is null || !gateway.TryRetireTo(direct)) return false;
                hook!.Dispose();
                if (!hook.IsDisposed) throw new InvalidOperationException("Joint hook disposal was not confirmed.");
                gateway.ReleaseOwnership(); hook = null; direct = null; retired = true; return true;
            }
            catch (Exception error) { reason = LatchRetirementFailure(error.Message); return false; }
            finally { Volatile.Write(ref operation, 0); }
        }
        public void Disable() { if (!BeginRetirement(out var reason)) throw new InvalidOperationException(reason); }
        public void Dispose() { if (!BeginRetirement(out var reason) || !TryCompleteRetirement(out reason)) throw new InvalidOperationException(reason); }
        private string LatchRetirementFailure(string reason)
            => Interlocked.CompareExchange(ref retirementFailure, reason, null) ?? reason;
    }
}
