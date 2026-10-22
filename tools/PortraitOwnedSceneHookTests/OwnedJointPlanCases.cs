using System.Runtime.InteropServices;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

internal static unsafe class OwnedJointPlanCases
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RenderLookalike(CharaView* view, uint frame);

    public static int Run()
    {
#if !HAS_OWNED_JOINT_PLAN
        Console.Error.WriteLine("FAIL joint target plan is absent: consumers and live mutation ingress cannot share one typed physical inventory");
        return 1;
#else
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + message);
            checks++;
            Console.WriteLine("PASS " + message);
        }
        var imageBase = 0x140000000L;
        var module = checked((nint)imageBase);
        var consumerRender = new PortraitOwnedSceneJointRole(PortraitOwnedSceneNativeKind.Render, null);
        var sourceRender = new PortraitOwnedSceneJointRole(null, PortraitNativeObservedPhase.SourceCharaViewRender);
        var add = new PortraitOwnedSceneJointRole(null, PortraitNativeObservedPhase.CounterAdd);
        var sub = new PortraitOwnedSceneJointRole(null, PortraitNativeObservedPhase.CounterSub);
        var renderType = typeof(CharaView.Delegates.Render);
        var renderAddress = module + 0x9CD4D0;
        var consumers = PortraitOwnedSceneHooks.FrozenTargets(module);
        Check(consumers.Count == 39, "joint plan consumes the real frozen 39-consumer inventory");
        Check(PortraitOwnedSceneJointPlan.TryCreate(module, out var plan, out var reason) && plan is not null,
            "frozen typed union is constructed without a resolver, hook factory or native read: " + reason);
        Check(!plan!.ProductionAdmission && plan.Targets.Count == 46,
            "46 detached physical targets grant no production scene admission");
        Check(plan.Targets.Select(target => target.Address).Distinct().Count() == 46
            && plan.Targets.Sum(target => target.Roles.Count) == 47,
            "47 audited logical roles have exactly 46 unique native addresses");
        Check(consumers.All(consumer => plan.Targets.SingleOrDefault(target => target.Address == consumer.Address)
                is { } target && target.Roles.Contains(new(consumer.Kind, null))),
            "every frozen consumer survives union under its original address and role");
        var phases = PortraitNativePhaseObservationBinding.Inventory
            .Concat(PortraitNativeCounterIngressContract.Inventory).Concat(PortraitNativeResourceIngressContract.Inventory).ToArray();
        Check(phases.Length == 8 && phases.All(phase => plan.Targets.SingleOrDefault(target => target.Address == module + (nint)phase.RVA)
                is { } target && target.DelegateType == phase.Delegate && target.Roles.Contains(new(null, phase.Phase))),
            "all four phase, two counter and two resource contracts retain their exact audited delegate types");
        var shared = plan.Targets.Single(target => target.Address == renderAddress);
        Check(shared.DelegateType == renderType && shared.Roles.Count == 2
            && shared.Roles.Contains(consumerRender) && shared.Roles.Contains(sourceRender),
            "source and consumer Render share one exact typed physical target");
        Check(plan.Targets.Where(target => target.Roles.Count > 1).SequenceEqual(new[] { shared }),
            "only audited shared Render has multiple observation roles");
        Check(plan.Targets.Single(target => target.Roles.Contains(new(null, PortraitNativeObservedPhase.ResourceRecreate)))
                .DelegateType.GetMethod("Invoke")!.ReturnType == typeof(byte),
            "resource recreate preserves its byte return rather than a void adapter");
        Check(PortraitOwnedSceneJointPlan.TryCreate(module, out var repeated, out _)
            && plan.Targets.Select(Describe).SequenceEqual(repeated!.Targets.Select(Describe)),
            "repeated frozen construction has a deterministic manifest");

        var inputs = new[] {
            new PortraitOwnedSceneJointInput(renderAddress, renderType, consumerRender),
            new PortraitOwnedSceneJointInput(renderAddress, renderType, sourceRender),
        };
        Check(PortraitOwnedSceneJointPlan.TryMerge(inputs, out var merged, out _) && merged.Count == 1
            && merged[0].Roles.Count == 2 && merged[0].DelegateType == renderType,
            "detached merger combines compatible shared Render roles exactly once");
        Check(PortraitOwnedSceneJointPlan.TryMerge(inputs.Reverse().ToArray(), out var reverse, out _)
            && reverse.Count == 1 && reverse[0].Roles.OrderBy(role => role.Consumer.HasValue)
                .SequenceEqual(merged[0].Roles.OrderBy(role => role.Consumer.HasValue)),
            "shared Render union is independent of input role order");
        inputs[0] = inputs[0] with { Address = module + 1 };
        Check(merged[0].Address == renderAddress && merged[0].Roles.Count == 2,
            "published targets retain detached immutable input values");
        var frozenRoles = (IList<PortraitOwnedSceneJointRole>)merged[0].Roles;
        var readOnly = false;
        try { frozenRoles[0] = add; } catch (NotSupportedException) { readOnly = true; }
        Check(readOnly, "published role collections cannot be mutated by a caller");

        void Reject(PortraitOwnedSceneJointInput[]? values, string message)
            => Check(!PortraitOwnedSceneJointPlan.TryMerge(values, out var result, out var failure)
                && result.Count == 0 && !string.IsNullOrWhiteSpace(failure), message);
        Reject([new(renderAddress, renderType, consumerRender), new(renderAddress, renderType, consumerRender)],
            "duplicate logical roles are rejected instead of silently deduplicated");
        Reject([new(renderAddress, renderType, sourceRender), new(renderAddress + 1, renderType, sourceRender)],
            "one observation role cannot bind to multiple physical targets");
        Reject([new(renderAddress, renderType, consumerRender), new(renderAddress, typeof(RenderLookalike), sourceRender)],
            "same-signature lookalike delegate cannot replace audited SDK Render identity");
        Reject([new(module + 0x2BA770, typeof(PortraitNativeSlotCounter), add),
            new(module + 0x2BA770, typeof(PortraitNativeSlotCounter), sub)],
            "different counter semantics at one address conflict even with an identical ABI");
        Reject([new(module + 0x2DBFE0, typeof(PortraitNativeResourceCleanup), new(null, PortraitNativeObservedPhase.ResourceRecreate))],
            "void resource delegate cannot replace the exact byte-return recreate contract");
        Reject([new(renderAddress, renderType, new(PortraitOwnedSceneNativeKind.Render, PortraitNativeObservedPhase.SourceCharaViewRender))],
            "a malformed role cannot combine consumer and observer identities");
        Reject([new(renderAddress, renderType, new(null, null))], "an empty role is rejected");
        Reject([new(renderAddress, renderType, new((PortraitOwnedSceneNativeKind)999, null))], "unknown consumer role is rejected");
        Reject([new(renderAddress, renderType, new(null, (PortraitNativeObservedPhase)999))], "unknown observation role is rejected");
        Reject([new(0, renderType, consumerRender)], "zero physical address is rejected");
        Reject([new(-1, renderType, consumerRender)], "negative physical address is rejected");
        Reject([new(renderAddress, null!, consumerRender)], "missing delegate type is rejected");
        Reject([], "empty detached inventory is rejected");
        Reject(null, "missing detached inventory is rejected");
        foreach (var invalidBase in new[] { (nint)0, (nint)(-1), nint.MaxValue })
            Check(!PortraitOwnedSceneJointPlan.TryCreate(invalidBase, out var invalid, out var failure)
                && invalid is null && !string.IsNullOrWhiteSpace(failure),
                "invalid or overflowing module base produces no frozen plan: " + invalidBase);
        Console.WriteLine($"{checks} joint target plan checks passed.");
        return 0;

        static string Describe(PortraitOwnedSceneJointTarget target)
            => $"{target.Address:X}:{target.DelegateType.AssemblyQualifiedName}:{string.Join(',', target.Roles)}";
#endif
    }
}
