using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace FireGaze.PortraitPreview;

internal interface IPortraitOwnedSceneTransportProof
{
    bool TryValidate(IReadOnlyList<PortraitOwnedSceneNativeTarget> targets, out string reason);
    bool VerifyEntry(PortraitOwnedSceneNativeTarget target);
}
internal interface IPortraitOwnedSceneQualifiedHookFactory : IPortraitOwnedSceneHookFactory
{
    string Identity { get; }
    bool TryQualifyTransport(IReadOnlyList<PortraitOwnedSceneNativeTarget> targets, out string reason);
    bool TryVerifyUnmodifiedTargets(IReadOnlyList<PortraitOwnedSceneNativeTarget> targets, out string reason);
}
internal interface IPortraitOwnedScenePhysicalHook
{
    int InFlight { get; }
    bool RetirementFaulted { get; }
    void DetachForward();
    bool BeginRetirement(out string reason);
    bool TryCompleteRetirement(out string reason);
}

internal sealed class DalamudPortraitOwnedSceneHookFactory : IPortraitOwnedSceneQualifiedHookFactory
{
    private readonly IPortraitQualifiedNativeHookProvider provider;
    private readonly IPortraitOwnedSceneTransportProof proof;
    // Retain even a partially constructed entry when official Create throws.
    // The outer owner retains this factory until the entire reservation drains.
    private readonly List<object> entries = [];
    private IReadOnlyList<PortraitOwnedSceneNativeTarget>? targets;
    internal DalamudPortraitOwnedSceneHookFactory(IGameInteropProvider interop)
        : this(new DalamudPortraitNativePhaseHostFactory(interop), new UnavailableProof()) { }
    internal DalamudPortraitOwnedSceneHookFactory(IPortraitQualifiedNativeHookProvider provider, IPortraitOwnedSceneTransportProof proof)
    { this.provider = provider; this.proof = proof; }
    public string Identity => provider.Identity;
    public bool TryQualifyTransport(IReadOnlyList<PortraitOwnedSceneNativeTarget> inventory, out string reason)
    {
        targets = null;
        if (!provider.TryQualifyStartDisabled(out reason) || !proof.TryValidate(inventory, out reason)) return false;
        targets = inventory.ToArray(); return true;
    }
    public bool TryVerifyUnmodifiedTargets(IReadOnlyList<PortraitOwnedSceneNativeTarget> inventory, out string reason)
    {
        reason = "Official owned backend or exact native entry qualification changed.";
        return provider.QualificationPreserved() && targets is not null && inventory.SequenceEqual(targets)
            && inventory.All(proof.VerifyEntry);
    }
    public IPortraitOwnedSceneNativeHook<T> Create<T>(PortraitOwnedSceneNativeTarget target, T detour) where T : Delegate
    {
        if (targets is null || !targets.Contains(target) || !provider.QualificationPreserved() || !proof.VerifyEntry(target))
            throw new InvalidOperationException("Owned transport was not qualified before native Create.");
        if (!PortraitNativePhaseHostGateway.TryCreateForwarded((int)target.Kind, typeof(T), target.Address, out var gateway, out var reason))
            throw new InvalidOperationException(reason);
        var entry = new NativeHook<T>(provider, proof, target, gateway!); entries.Add(entry);
        try { entry.Create(detour); entries.Remove(entry); return entry; }
        catch
        {
            entry.BeginRetirement(out _);
            if (entry.TryCompleteRetirement(out _)) entries.Remove(entry);
            throw;
        }
    }

    private sealed class NativeHook<T>(IPortraitQualifiedNativeHookProvider provider, IPortraitOwnedSceneTransportProof proof,
        PortraitOwnedSceneNativeTarget target, PortraitNativePhaseHostGateway gateway) : IPortraitOwnedSceneNativeHook<T>, IPortraitOwnedScenePhysicalHook where T : Delegate
    {
        private IPortraitNativePhaseHostHook? hook;
        private Delegate? direct;
        private T? original;
        private bool retired, createAttempted;
        private int closing, nativeOperation;
        public T Original => original ?? throw new InvalidOperationException("The owned native trampoline is unavailable or retired.");
        public bool IsEnabled => hook?.IsEnabled == true;
        public bool IsDisposed => retired;
        public int InFlight => gateway.ActiveCount;
        public bool RetirementFaulted { get; private set; }
        internal void Create(T dispatcher)
        {
            direct = provider.DirectTarget(target.Address, gateway.DelegateType);
            createAttempted = true;
            hook = provider.CreateQualifiedHook(target.Address, gateway.DelegateType, gateway.Detour, () => proof.VerifyEntry(target));
            if (hook.Address != target.Address || hook.BackendName != "SafetyHook" || hook.IsEnabled || hook.IsDisposed)
                throw new InvalidOperationException("Official owned hook backend/address/start-disabled state differs from qualification.");
            gateway.PublishOriginal(hook.Original);
            original = AdaptOriginal(hook.Original);
            gateway.SetForward(dispatcher);
        }
        public void Enable()
        {
            if (Interlocked.CompareExchange(ref nativeOperation, 1, 0) != 0)
                throw new InvalidOperationException("An owned native Hook operation is already active.");
            try
            {
                if (Volatile.Read(ref closing) != 0 || retired || !provider.QualificationPreserved() || !proof.VerifyEntry(target) || original is null)
                    throw new InvalidOperationException("Official owned entry qualification changed before Enable.");
                hook!.Enable();
                if (Volatile.Read(ref closing) != 0) throw new InvalidOperationException("Owned hook was closed during Enable.");
            }
            finally
            {
                Volatile.Write(ref nativeOperation, 0);
                if (Volatile.Read(ref closing) != 0) BeginRetirement(out _);
            }
        }
        public void Disable() { if (!BeginRetirement(out var reason)) throw new InvalidOperationException(reason); }
        public void Dispose() { if (!BeginRetirement(out var reason) || !TryCompleteRetirement(out reason)) throw new InvalidOperationException(reason); }
        public void DetachForward()
        { Interlocked.Exchange(ref closing, 1); gateway.DetachObserver(); }
        public bool BeginRetirement(out string reason)
        {
            reason = "Owned forward detached; waiting asynchronously for every native callback.";
            DetachForward();
            if (Interlocked.CompareExchange(ref nativeOperation, 1, 0) != 0) return false;
            try
            {
                if (hook is { IsDisposed: false, IsEnabled: true }) hook.Disable();
                if (hook is { IsEnabled: true }) throw new InvalidOperationException("Official owned hook did not disable.");
                RetirementFaulted = false; return true;
            }
            catch (Exception error) { reason = error.Message; RetirementFaulted = true; return false; }
            finally { Volatile.Write(ref nativeOperation, 0); }
        }
        public bool TryCompleteRetirement(out string reason)
        {
            reason = "Waiting for physical owned callbacks before trampoline retirement.";
            if (retired) return true;
            if (Volatile.Read(ref closing) == 0 || Interlocked.CompareExchange(ref nativeOperation, 1, 0) != 0) return false;
            try
            {
            if (hook is null && createAttempted) { RetirementFaulted = true; reason = "Official Create returned no handle; native compensation is unknown."; return false; }
            if (!createAttempted)
            { retired = gateway.TryAbandonUnpublishedOwnership(); RetirementFaulted = !retired; return retired; }
            if (hook is { IsEnabled: true } || direct is null || !gateway.TryRetireTo(direct)) return false;
            try
            {
                hook?.Dispose();
                if (hook is { IsDisposed: false }) throw new InvalidOperationException("Official owned hook disposal was not confirmed.");
                // Original/dispatcher references clear only after the active callback
                // finally completed and the permanent entry switched to direct.
                original = null; hook = null; direct = null;
                gateway.ReleaseOwnership(); retired = true; RetirementFaulted = false; return true;
            }
            catch (Exception error) { reason = error.Message; RetirementFaulted = true; return false; }
            }
            finally { Volatile.Write(ref nativeOperation, 0); }
        }
        private static T AdaptOriginal(Delegate stableOriginal)
        {
            // A native Marshal delegate does not expose a rebindable managed
            // Method. Emit one exact typed call, with no boxing/object[]/Invoke.
            var invoke = stableOriginal.GetType().GetMethod("Invoke")!;
            var parameters = invoke.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
            var method = new DynamicMethod("OwnedExactOriginal", invoke.ReturnType, [stableOriginal.GetType(), .. parameters], typeof(T).Module, true);
            var il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0);
            for (var index = 0; index < parameters.Length; index++) il.Emit(OpCodes.Ldarg, index + 1);
            il.Emit(OpCodes.Callvirt, invoke); il.Emit(OpCodes.Ret);
            return (T)method.CreateDelegate(typeof(T), stableOriginal);
        }
    }
    private sealed class UnavailableProof : IPortraitOwnedSceneTransportProof
    {
        public bool TryValidate(IReadOnlyList<PortraitOwnedSceneNativeTarget> targets, out string reason)
        { reason = "The owned 39-entry root/leaf and current loaded-byte proof has not been connected."; return false; }
        public bool VerifyEntry(PortraitOwnedSceneNativeTarget target) => false;
    }
}
