using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

// Consumer kinds can occur at several frozen addresses (for example the twelve
// destructors); an observation phase has exactly one audited native entry.
internal readonly record struct PortraitOwnedSceneJointRole(
    PortraitOwnedSceneNativeKind? Consumer, PortraitNativeObservedPhase? Phase);
internal readonly record struct PortraitOwnedSceneJointInput(
    nint Address, Type DelegateType, PortraitOwnedSceneJointRole Role);

internal sealed class PortraitOwnedSceneJointTarget
{
    internal PortraitOwnedSceneJointTarget(nint address, Type delegateType,
        IEnumerable<PortraitOwnedSceneJointRole> roles)
    { Address = address; DelegateType = delegateType; Roles = Array.AsReadOnly(roles.ToArray()); }
    internal nint Address { get; }
    internal Type DelegateType { get; }
    internal IReadOnlyList<PortraitOwnedSceneJointRole> Roles { get; }
}

/// <summary>
/// Detached typed manifest only. No memory reads, address reservation, hooks or
/// production admission. Only the frozen factory can create a plan; TryMerge
/// checks detached candidate conflicts without qualifying a production plan.
/// </summary>
internal sealed unsafe class PortraitOwnedSceneJointPlan
{
    private const int ConsumerCount = 39, ObservationCount = 8, PhysicalCount = 46;
    private static readonly PortraitOwnedSceneJointRole ConsumerRender = new(PortraitOwnedSceneNativeKind.Render, null);
    private static readonly PortraitOwnedSceneJointRole SourceRender = new(null, PortraitNativeObservedPhase.SourceCharaViewRender);

    private PortraitOwnedSceneJointPlan(nint moduleBase, IReadOnlyList<PortraitOwnedSceneJointTarget> targets)
    { ModuleBase = moduleBase; Targets = targets; }
    internal nint ModuleBase { get; }
    internal IReadOnlyList<PortraitOwnedSceneJointTarget> Targets { get; }
    internal bool ProductionAdmission => false;

    internal static bool TryCreate(nint moduleBase, out PortraitOwnedSceneJointPlan? plan, out string reason)
    {
        plan = null;
        reason = "Joint plan requires a positive non-overflowing 64-bit module base and the frozen ABI.";
        if (nint.Size != 8 || moduleBase <= 0) return false;
        if (!PortraitOwnedSceneHooks.NativeAbiMatchesSDK(out reason)) return false;
        if (!PortraitNativePhaseObservationBinding.SDKAbiMatches()
            || !PortraitNativeCounterIngressContract.ABIValid() || !PortraitNativeResourceIngressContract.ABIValid())
        { reason = "Joint observation ABI differs from the audited contract."; return false; }

        // Ask the existing frozen inventory for RVAs, then add the module base
        // with an explicit overflow check; never copy its native address list.
        var consumers = PortraitOwnedSceneHooks.FrozenTargets(0);
        var observations = PortraitNativePhaseObservationBinding.Inventory
            .Concat(PortraitNativeCounterIngressContract.Inventory)
            .Concat(PortraitNativeResourceIngressContract.Inventory).ToArray();
        if (consumers.Count != ConsumerCount || observations.Length != ObservationCount)
        { reason = "Frozen joint inventory changed; the audited union must be reviewed."; return false; }
        var inputs = new List<PortraitOwnedSceneJointInput>(ConsumerCount + ObservationCount);
        foreach (var consumer in consumers)
        {
            var role = new PortraitOwnedSceneJointRole(consumer.Kind, null);
            if (!TryAddress(moduleBase, consumer.Address, out var address) || !TryDelegate(role, out var type))
            { reason = "Frozen consumer address or delegate is invalid for the joint plan."; return false; }
            inputs.Add(new(address, type, role));
        }
        foreach (var observation in observations)
        {
            if (!TryAddress(moduleBase, (nint)observation.RVA, out var address))
            { reason = "Frozen observation address overflows the joint module base."; return false; }
            inputs.Add(new(address, observation.Delegate, new(null, observation.Phase)));
        }
        if (!TryMerge(inputs, out var targets, out reason)) return false;
        if (targets.Count != PhysicalCount || targets.Sum(target => target.Roles.Count) != ConsumerCount + ObservationCount
            || targets.Count(target => target.Roles.Count == 2 && target.Roles.Contains(ConsumerRender)
                && target.Roles.Contains(SourceRender)) != 1)
        { reason = "Frozen joint union is not the audited 46-target shared-Render manifest."; return false; }
        plan = new(moduleBase, targets);
        reason = "Detached 46-target typed union built; no process identity or hook installation was qualified.";
        return true;
    }

    internal static bool TryMerge(IReadOnlyList<PortraitOwnedSceneJointInput>? inputs,
        out IReadOnlyList<PortraitOwnedSceneJointTarget> targets, out string reason)
    {
        targets = [];
        reason = "Joint target inventory is absent or exceeds the audited logical binding count.";
        if (inputs is null || inputs.Count is <= 0 or > ConsumerCount + ObservationCount) return false;
        var builders = new List<TargetBuilder>();
        var addresses = new Dictionary<nint, TargetBuilder>();
        var phases = new HashSet<PortraitNativeObservedPhase>();
        foreach (var input in inputs)
        {
            if (input.Address <= 0 || !TryDelegate(input.Role, out var expected) || input.DelegateType != expected)
            { reason = "Joint role has an invalid address or differs from its exact audited delegate type."; return false; }
            if (input.Role.Phase is { } phase && !phases.Add(phase))
            { reason = "One observed phase appears more than once in the joint inventory."; return false; }
            if (addresses.TryGetValue(input.Address, out var existing))
            {
                if (existing.DelegateType != input.DelegateType)
                { reason = "One native address has incompatible exact delegate types."; return false; }
                if (existing.Roles.Contains(input.Role))
                { reason = "A joint native role was duplicated instead of independently audited."; return false; }
                if (existing.Roles.Count != 1 || !SharedRender(existing.Roles[0], input.Role))
                { reason = "One native address has incompatible role semantics; only source/consumer Render may share."; return false; }
                existing.Roles.Add(input.Role);
            }
            else
            {
                var builder = new TargetBuilder(input.Address, input.DelegateType, input.Role);
                addresses.Add(input.Address, builder); builders.Add(builder);
            }
        }
        targets = Array.AsReadOnly(builders.Select(builder => new PortraitOwnedSceneJointTarget(
            builder.Address, builder.DelegateType, builder.Roles)).ToArray());
        reason = "Detached joint inputs merged by address, exact delegate identity and compatible role semantics.";
        return true;
    }

    private static bool SharedRender(PortraitOwnedSceneJointRole first, PortraitOwnedSceneJointRole second)
        => first == ConsumerRender && second == SourceRender || first == SourceRender && second == ConsumerRender;

    private static bool TryAddress(nint moduleBase, nint rva, out nint address)
    {
        address = 0;
        if (moduleBase <= 0 || rva <= 0 || moduleBase > nint.MaxValue - rva) return false;
        address = moduleBase + rva; return true;
    }

    private static bool TryDelegate(PortraitOwnedSceneJointRole role, out Type type)
    {
        type = null!;
        if (role.Consumer.HasValue == role.Phase.HasValue) return false;
        if (role.Phase is { } phase)
        {
            var contract = PortraitNativeCounterIngressContract.Find(phase);
            if (contract is null) return false;
            type = contract.Delegate; return true;
        }
        type = role.Consumer switch {
            PortraitOwnedSceneNativeKind.Dtor => typeof(CharaView.Delegates.Dtor),
            PortraitOwnedSceneNativeKind.Release => typeof(CharaView.Delegates.Release),
            PortraitOwnedSceneNativeKind.Initialize => typeof(CharaView.Delegates.Initialize),
            PortraitOwnedSceneNativeKind.Update => typeof(CharaView.Delegates.Update),
            PortraitOwnedSceneNativeKind.Setup => typeof(CharaViewPortrait.Delegates.Setup),
            PortraitOwnedSceneNativeKind.Render => typeof(CharaView.Delegates.Render),
            PortraitOwnedSceneNativeKind.CreateClientObject => typeof(ClientObjectManager.Delegates.CreateBattleCharacter),
            PortraitOwnedSceneNativeKind.DeleteClientObject => typeof(ClientObjectManager.Delegates.DeleteObjectByIndex),
            PortraitOwnedSceneNativeKind.ConfigureContext => typeof(ConfigureContext),
            PortraitOwnedSceneNativeKind.ConfigureDefaultContext => typeof(ConfigureDefaultContext),
            PortraitOwnedSceneNativeKind.CleanupContext => typeof(CleanupContext),
            PortraitOwnedSceneNativeKind.ConstructContext => typeof(ConstructContext),
            PortraitOwnedSceneNativeKind.DestroyContext => typeof(DestroyContext),
            _ => null!,
        };
        return type is not null;
    }

    private sealed class TargetBuilder(nint address, Type delegateType, PortraitOwnedSceneJointRole role)
    {
        internal nint Address { get; } = address;
        internal Type DelegateType { get; } = delegateType;
        internal List<PortraitOwnedSceneJointRole> Roles { get; } = [role];
    }
}
