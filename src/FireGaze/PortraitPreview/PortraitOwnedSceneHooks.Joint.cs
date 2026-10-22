namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitOwnedSceneHooks
{
    private PortraitOwnedSceneJointIngress? jointIngress;
    private readonly object jointSourceAuthority = new();
    private PortraitOwnedSceneJointIngress.ActiveDispatch? activeJointDispatch;
    private JointSourceInvocation? jointSourceOriginal;
    private bool activeJointRetiring, activeJointSmoke;
    private nint jointRenderer;
    private OwnNativeEntry? ownNativeEntry;
    private readonly List<IDisposable> resumedJointPhases = [];
    internal bool IsOwnedExecutionIdentityCurrent(PortraitOwnedSceneLease current)
        => jointIngress is null ? IsNativeCallPhaseCurrent(current) : ReferenceEquals(lease, current) && !stopped && IsInstalled
            && phaseDepth > 0 && phaseThread == Environment.CurrentManagedThreadId && activeJointDispatch is { } dispatch
            && jointIngress.IsActiveDispatchIdentityCurrent(this, dispatch);
    private IDisposable? BeginOwnNativeEntry(PortraitOwnedSceneNativeKind operation, nint view)
    {
        if (jointIngress is null || lease is not { } current || activeJointDispatch is null) return null;
        if (!current.HasExecution(operation, view)
            || ownNativeEntry is { } previous && previous.Operation == operation)
        { InvalidateActiveJoint("Unexpected native view/operation cannot borrow the enclosing owned capability."); return null; }
        if (ownNativeEntry is { } parent && (parent.Lease != current || parent.Serial != current.ExecutionSerial))
        { InvalidateActiveJoint("An unrelated native invocation interrupted the owned call."); return null; }
        var entry = new OwnNativeEntry(this, ownNativeEntry, current, operation, view, current.ExecutionSerial, jointRenderer);
        ownNativeEntry = entry; return entry;
    }
    private bool IsProvedOwnNative(nint view, PortraitOwnedSceneNativeKind? operation = null)
        => ownNativeEntry is { } entry && entry.View == view && (!operation.HasValue || entry.Operation == operation)
            && entry.Thread == Environment.CurrentManagedThreadId && entry.Lease.ExecutionSerial == entry.Serial
            && entry.Lease.HasExecution(entry.Operation, view) && entry.Renderer == jointRenderer && jointRenderer != 0;
    internal bool TryBeginOwnedCounter(nint renderer, uint slot, out IDisposable? guard)
    {
        guard = null;
        if (ownNativeEntry is not { } entry || slot != entry.Lease.Slot || renderer != entry.Renderer
            || entry.Operation is not (PortraitOwnedSceneNativeKind.Setup or PortraitOwnedSceneNativeKind.Initialize
                or PortraitOwnedSceneNativeKind.Render or PortraitOwnedSceneNativeKind.Update
                or PortraitOwnedSceneNativeKind.Release or PortraitOwnedSceneNativeKind.Dtor)
            || !IsProvedOwnNative(entry.View) || environment is not IPortraitOwnedSceneActiveMutationEnvironment scoped) return false;
        try { guard = scoped.BeginOwnedCounterMutation(); return guard is not null; }
        catch (Exception) { InvalidateActiveJoint("Owned counter suspension failed."); return false; }
    }
    internal void CompleteOwnedCounter(bool returned)
    {
        if (!returned) { InvalidateActiveJoint("Owned counter Original threw; no read token can resume."); return; }
        if (!IsActiveJointDispatchCurrent) return; // An inner counter cannot reopen its outer mutation.
        if (lease is not { } current || environment is not IPortraitOwnedSceneActiveMutationEnvironment scoped)
        { InvalidateActiveJoint("Owned counter return lost its native owner."); return; }
        var resumed = scoped.ResumeOwnedCounterPhase(source, lastFrame, requestEpoch, jointRenderer);
        if (resumed is null) { InvalidateActiveJoint("Owned counter return could not issue a new root-checked read token."); return; }
        if (resumedJointPhases.Count >= 64)
        { resumed.Dispose(); InvalidateActiveJoint("Owned counter read-token limit reached."); return; }
        resumedJointPhases.Add(resumed);
        if (!environment.IsSourceCurrent(source) || !current.IsCurrent)
            InvalidateActiveJoint("Owned counter return failed source or pinned slot revalidation.");
    }
    internal void InvalidateActiveJoint(string reason)
    {
        if (activeJointDispatch is { } dispatch) dispatch.Invalid = true;
        StopUnsafe(reason);
    }
    private void RetireActiveExperiment()
    {
        if (jointIngress is null || passiveOnly || lease is null || stopped || !activeJointSmoke && !closing) return;
        activeJointRetiring = true;
        try { RetireBeforeNative(); }
        finally { activeJointRetiring = false; }
    }
    private sealed class OwnNativeEntry(PortraitOwnedSceneHooks owner, OwnNativeEntry? parent,
        PortraitOwnedSceneLease lease, PortraitOwnedSceneNativeKind operation, nint view, long serial, nint renderer) : IDisposable
    {
        internal readonly PortraitOwnedSceneLease Lease = lease;
        internal readonly PortraitOwnedSceneNativeKind Operation = operation;
        internal readonly nint View = view, Renderer = renderer;
        internal readonly long Serial = serial;
        internal readonly int Thread = Environment.CurrentManagedThreadId;
        public void Dispose()
        {
            if (!ReferenceEquals(owner.ownNativeEntry, this) || Thread != Environment.CurrentManagedThreadId)
            { owner.InvalidateActiveJoint("Owned native invocation scopes did not return in stack order."); return; }
            owner.ownNativeEntry = parent;
        }
    }
    internal bool IsActiveJointDispatchCurrent => activeJointDispatch is { } dispatch
        && jointIngress?.IsActiveDispatchCurrent(this, dispatch) == true;
    internal void MarkActiveRetirementOnly()
    { closing = true; activeJointRetiring = true; }
    internal bool IsActiveAdvanceAdmitted => !closing && activeJointDispatch is { RetirementOnly: false } && IsActiveJointDispatchCurrent;

    internal Guid Installation => installation;
    internal bool TryInstallJointPassive(IPortraitOwnedSceneJointHookFactory factory, PortraitOwnedSceneJointPlan plan,
        PortraitOwnedSceneJointIngress ingress, TimeSpan duration)
        => duration > TimeSpan.Zero && duration <= TimeSpan.FromSeconds(2)
            && TryInstallCore(factory, true, duration, plan, ingress);

    internal bool TryInstallJointActive(IPortraitOwnedSceneJointHookFactory factory, PortraitOwnedSceneJointPlan plan,
        PortraitOwnedSceneJointIngress ingress, TimeSpan duration, bool sameReturnSmoke = false)
    {
        activeJointSmoke = sameReturnSmoke;
        ingress.EnableActiveOwner(this);
        return duration > TimeSpan.Zero && duration <= TimeSpan.FromSeconds(2)
            && TryInstallCore(factory, false, duration, plan, ingress);
    }

    private static bool JointConsumersMatch(PortraitOwnedSceneJointPlan plan, IReadOnlyList<PortraitOwnedSceneNativeTarget> consumers)
        => plan.Targets.SelectMany(target => target.Roles.Where(role => role.Consumer.HasValue)
                .Select(role => new PortraitOwnedSceneNativeTarget(role.Consumer!.Value, target.Address)))
            .SequenceEqual(consumers);
    private void CreateJointHooks(IPortraitOwnedSceneJointHookFactory factory, PortraitOwnedSceneJointPlan plan,
        PortraitOwnedSceneJointIngress ingress)
    {
        foreach (var target in plan.Targets.Where(target => target.Roles.All(role => role.Consumer is null)))
        {
            if (closing) throw new InvalidOperationException("Joint installation closed before the next extra Create.");
            var phase = target.Roles.Single().Phase!.Value;
            unknownCreation = true;
            var hook = factory.CreateJoint(new(phase, target.Address, target.DelegateType), ingress)
                ?? throw new InvalidOperationException("Joint native Create returned no handle.");
            hooks.Add(hook); unknownCreation = false;
            allHooksPhysical &= hook is IPortraitOwnedScenePhysicalHook;
            Volatile.Write(ref publishedHooks, hooks.ToArray());
            if (hook.IsEnabled || hook.IsDisposed) throw new InvalidOperationException("Joint hook did not start disabled and live.");
        }
    }
    private JointSourceInvocation? BeginJointSource(nint receiver, uint frame)
    {
        try
        {
            if (jointIngress is null || !IsInstalled || closing && passiveOnly || stopped) return null;
            if (IsProvedOwnNative(receiver, PortraitOwnedSceneNativeKind.Render)) return null;
            if (activeJointDispatch is not null) InvalidateActiveJoint("Foreign nested Render interrupted the active source return.");
            var observed = jointIngress.BeginSource(receiver, frame);
            return observed is null ? null : new(this, jointSourceAuthority, installation, observed);
        }
        catch (Exception) { jointIngress?.Revoke(); return null; }
    }
    private void CompleteJointSource(JointSourceInvocation? invocation, bool returned)
    {
        if (invocation is null || jointIngress is null) return;
        try
        {
            var confirmed = jointIngress.EndSource(invocation.Observation, returned);
            activeJointRetiring = !passiveOnly && (closing || invocation.Observation.RetirementOnly);
            if (activeJointRetiring) closing = true;
            invocation.Open(jointSourceAuthority, confirmed);
            jointIngress.RecordSource(this, invocation);
            if (!passiveOnly)
            {
                using var dispatch = jointIngress.TryBeginActiveDispatch(this, invocation, activeJointRetiring);
                if (dispatch is null) return;
                activeJointDispatch = dispatch;
                try { AfterSourceRender(invocation.Observation.Receiver, invocation.Observation.Frame); }
                finally
                {
                    foreach (var scope in resumedJointPhases.AsEnumerable().Reverse()) scope.Dispose();
                    resumedJointPhases.Clear(); activeJointDispatch = null; jointRenderer = 0;
                }
            }
        }
        catch (Exception) { jointIngress.Revoke(); }
        finally
        {
            activeJointRetiring = false;
            invocation.Close(jointSourceAuthority);
            jointIngress.CloseSource(invocation.Observation);
            // A disposed Feature has no Framework Update left to retry TryDispose.
            // Only a successfully cleared owner can close here, after every native
            // scope returned. Physical gateways retain this callback until it exits.
            if (closing && !stopped && lease is null && advance is null && retire is null) TryDispose();
        }
    }
    // A caller cannot open this marker without the actual owner's private key.
    // It is historical evidence authority only, never a native read/scene lease.
    internal sealed class JointSourceInvocation
    {
        private readonly PortraitOwnedSceneHooks owner;
        private readonly object? authority;
        private int returning;
        internal JointSourceInvocation(PortraitOwnedSceneHooks owner, object key, Guid installation,
            PortraitOwnedSceneJointIngress.SourceObservation observation)
        {
            this.owner = owner; authority = ReferenceEquals(key, owner.jointSourceAuthority) ? key : null;
            Installation = installation; Observation = observation;
        }
        internal Guid Installation { get; }
        internal PortraitOwnedSceneJointIngress.SourceObservation Observation { get; }
        internal bool IsReturningFor(PortraitOwnedSceneHooks expected) => ReferenceEquals(owner, expected)
            && authority is not null && Volatile.Read(ref returning) == 1 && owner.IsInstalled && (!owner.closing || owner.activeJointRetiring) && !owner.stopped;
        internal void Open(object key, bool confirmed)
        { if (authority is not null && ReferenceEquals(key, authority)) Volatile.Write(ref returning, confirmed ? 1 : 2); }
        internal void Close(object key)
        { if (authority is not null && ReferenceEquals(key, authority)) Volatile.Write(ref returning, 2); }
    }
}
