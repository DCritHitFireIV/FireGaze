namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitOwnedSceneHooks
{
    private PortraitOwnedSceneJointIngress? jointIngress;
    private readonly object jointSourceAuthority = new();
    internal Guid Installation => installation;
    internal bool TryInstallJointPassive(IPortraitOwnedSceneJointHookFactory factory, PortraitOwnedSceneJointPlan plan,
        PortraitOwnedSceneJointIngress ingress, TimeSpan duration)
        => duration > TimeSpan.Zero && duration <= TimeSpan.FromSeconds(2)
            && TryInstallCore(factory, true, duration, plan, ingress);

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
            if (jointIngress is null || !passiveOnly || !IsInstalled || closing || stopped) return null;
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
            invocation.Open(jointSourceAuthority, confirmed);
            jointIngress.RecordSource(this, invocation);
        }
        catch (Exception) { jointIngress.Revoke(); }
        finally
        {
            invocation.Close(jointSourceAuthority);
            jointIngress.CloseSource(invocation.Observation);
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
            && authority is not null && Volatile.Read(ref returning) == 1 && owner.IsInstalled && !owner.closing && !owner.stopped;
        internal void Open(object key, bool confirmed)
        { if (authority is not null && ReferenceEquals(key, authority)) Volatile.Write(ref returning, confirmed ? 1 : 2); }
        internal void Close(object key)
        { if (authority is not null && ReferenceEquals(key, authority)) Volatile.Write(ref returning, 2); }
    }
}
