using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitNativePhaseEntryEvidence(PortraitNativeObservedPhase Phase, nint Address,
    uint FunctionRootRVA, int PrefixMatches, byte[] Prefix, Type DelegateType, nint SDKResolvedAddress,
    string? CompleteBodySHA256 = null);
internal readonly record struct PortraitNativePhaseBoundTarget(PortraitNativeObservedPhase Phase, nint Address, Type DelegateType);
internal sealed record PortraitNativePhaseEntryContract(PortraitNativeObservedPhase Phase, uint RVA, string Prefix, Type Delegate, bool SDKEntry,
    bool CompleteLeaf = false);

/// <summary>Detached evidence validation only. No resolver, memory reader, installer, or production admission.</summary>
internal static unsafe class PortraitNativePhaseObservationBinding
{
    internal const string EXE = "958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9";
    internal const string SDK = "5A7A8EA71E84D7410DBAD8A5391D4759C2A38B1A55F4F3BB745B22B556A6F778";
    // Observation only. The owned39 transport retains its independent frozen contract.
    internal const string CurrentObservationSDK = "5726E301EE5456D526F524E9F88659F38A8717CB527FE4A86D83D1ADE0AB9798";
    internal static bool SDKIdentityMatches(string hash, Guid mvid)
        => hash == SDK && mvid == Guid.Parse("c72f17a8-6ed1-4a30-a097-74137dc08f9e")
            || hash == CurrentObservationSDK && mvid == Guid.Parse("18eb3871-c94c-4c54-846b-2ee6a94a336d");
    internal static IReadOnlyList<PortraitNativePhaseEntryContract> Inventory { get; } = Array.AsReadOnly<PortraitNativePhaseEntryContract>([
        new(PortraitNativeObservedPhase.ManagerRender, 0x2BA030, "405357415441554883EC5865488B0425580000004C8BE98B1573689602488B1D", typeof(RenderManager.Delegates.Render), true),
        new(PortraitNativeObservedPhase.ManagerUpdateCandidate, 0x2B9A40, "40564883EC70488BF1440F295424208B894C830300440F28D183F9FF744C8BC1", typeof(PortraitNativeManagerUpdateCandidate), false),
        new(PortraitNativeObservedPhase.SceneCameraUpdate, 0x421D60, "4C8BDC49895B104989731849897B2055498DAB28FFFFFF4881ECD0010000488B", typeof(PortraitNativeSceneCameraUpdate), false),
        new(PortraitNativeObservedPhase.SourceCharaViewRender, 0x9CD4D0, "48897C242041564883EC2083790800448BF2488BF90F840A0300008B510C85D2", typeof(CharaView.Delegates.Render), true),
    ]);

    public static bool TryBind(nint moduleBase, string exeSHA256, string sdkSHA256,
        IReadOnlyList<PortraitNativePhaseEntryEvidence> evidence, out IReadOnlyList<PortraitNativePhaseBoundTarget> targets, out string reason)
        => TryBind(moduleBase, exeSHA256, sdkSHA256, evidence, PortraitNativePhaseTargetSet.PhaseObservation, out targets, out reason);

    internal static bool TryBind(nint moduleBase, string exeSHA256, string sdkSHA256,
        IReadOnlyList<PortraitNativePhaseEntryEvidence> evidence, PortraitNativePhaseTargetSet targetSet,
        out IReadOnlyList<PortraitNativePhaseBoundTarget> targets, out string reason)
    {
        targets = []; reason = "Native observation evidence does not match the audited EXE/SDK entry contracts.";
        var inventory = PortraitNativeCounterIngressContract.For(targetSet);
        if (nint.Size != 8 || moduleBase <= 0 || exeSHA256 != EXE || sdkSHA256 is not (SDK or CurrentObservationSDK) || evidence is null
            || inventory.Count == 0 || evidence.Count != inventory.Count || !SDKAbiMatches()
            || targetSet == PortraitNativePhaseTargetSet.CounterIngress && !PortraitNativeCounterIngressContract.ABIValid()
            || targetSet == PortraitNativePhaseTargetSet.ResourceIngress && !PortraitNativeResourceIngressContract.ABIValid()) return false;
        var bound = new PortraitNativePhaseBoundTarget[inventory.Count];
        for (var index = 0; index < inventory.Count; index++)
        {
            var expected = inventory[index];
            var entry = evidence[index];
            if (moduleBase > nint.MaxValue - (nint)expected.RVA) return false;
            var address = moduleBase + (nint)expected.RVA;
            if (entry is null || entry.Phase != expected.Phase || entry.Address != address || entry.FunctionRootRVA != expected.RVA
                || entry.PrefixMatches != 1 || entry.Prefix is null || !entry.Prefix.AsSpan().SequenceEqual(Convert.FromHexString(expected.Prefix))
                || entry.DelegateType != expected.Delegate || (expected.SDKEntry ? entry.SDKResolvedAddress != address : entry.SDKResolvedAddress != 0)
                || PortraitNativeResourceIngressContract.Find(expected.Phase) is { } resource && entry.CompleteBodySHA256 != resource.SHA256)
                return false;
            bound[index] = new(expected.Phase, address, expected.Delegate);
        }
        targets = Array.AsReadOnly(bound);
        reason = "Detached observation targets validated; no hooks installed and no scene admission granted.";
        return true;
    }

    public static bool SDKAbiMatches() => Signature<RenderManager.Delegates.Render>(typeof(RenderManager*))
        && Signature<CharaView.Delegates.Render>(typeof(CharaView*), typeof(uint))
        && Signature<PortraitNativeManagerUpdateCandidate>(typeof(RenderManager*), typeof(float))
        && Signature<PortraitNativeSceneCameraUpdate>(typeof(SceneCameraManager*));

    private static bool Signature<T>(params Type[] parameters) where T : Delegate
    {
        var invoke = typeof(T).GetMethod("Invoke")!;
        return invoke.ReturnType == typeof(void) && invoke.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters);
    }
}
