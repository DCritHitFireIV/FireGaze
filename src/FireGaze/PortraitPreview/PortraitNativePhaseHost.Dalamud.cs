using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitNativeBootQualificationResult(
    bool Qualified, PortraitNativeBootIdentity? Identity, string Reason);

/// <summary>Official provider only. No Harmony, MonoMod, custom patching or backend configuration changes.</summary>
internal sealed class DalamudPortraitNativePhaseHostFactory(IGameInteropProvider interop) : IPortraitNativePhaseHostFactory, IPortraitQualifiedNativeHookProvider
{
    private const string DalamudSHA = "CE0C3C8004CAC1E9AA96D0DC749463F5F1A32E9B49988419BF320545BF942C6A";
    private bool qualified;
    private PortraitNativeBootIdentity? bootIdentity;
    public string Identity { get; private set; } = "Unqualified; no native hooks created.";

    public bool TryQualifyStartDisabled(out string reason)
    {
        qualified = false;
        bootIdentity = null;
        reason = "The current official provider/Boot identity or StartDisabled SafetyHook selection is unavailable.";
        try
        {
            var assembly = typeof(Hook<>).Assembly;
            if (!TryEvaluateQualification(
                    assembly.IsCollectible,
                    () => interop.GetType().Assembly == assembly,
                    () => interop.GetType().FullName == "Dalamud.Hooking.Internal.GameInteropProviderPluginScoped",
                    () => HashMatches(assembly.Location, DalamudSHA),
                    () => LoadedMVIDMatchesDisk(assembly),
                    () => UsesSafetyHook(assembly),
                    () =>
                    {
                        var result = PortraitNativeBootImageQualifier.TryQualifyCurrentProcess(out var identity, out var bootReason);
                        return new(result, identity, bootReason);
                    },
                    out bootIdentity, out reason)) return false;
            qualified = true;
            Identity = "SafetyHook StartDisabled; loaded Boot code/standard-loader exports match audited image; loaded Dalamud MVID " + assembly.ManifestModule.ModuleVersionId;
            reason = Identity; return true;
        }
        catch (Exception error) { reason = "Official native backend qualification unavailable: " + error.GetType().Name; return false; }

    }

    internal static bool TryEvaluateQualification(
        bool assemblyCollectible,
        Func<bool> providerAssemblyMatch,
        Func<bool> providerTypeMatch,
        Func<bool> dalamudDiskHashMatch,
        Func<bool> loadedMvidMatchesDisk,
        Func<bool> safetyHookSelected,
        Func<PortraitNativeBootQualificationResult> bootQualifier,
        out PortraitNativeBootIdentity? bootIdentity,
        out string reason)
    {
        bootIdentity = null;
        var checks = new QualificationChecks { AssemblyCollectible = assemblyCollectible };
        try
        {
            if (assemblyCollectible) return Reject(checks, out reason);
            checks.ProviderAssembly = providerAssemblyMatch();
            if (checks.ProviderAssembly != true) return Reject(checks, out reason);
            checks.ProviderType = providerTypeMatch();
            if (checks.ProviderType != true) return Reject(checks, out reason);
            checks.DalamudDiskHash = dalamudDiskHashMatch();
            if (checks.DalamudDiskHash != true) return Reject(checks, out reason);
            checks.LoadedMvid = loadedMvidMatchesDisk();
            if (checks.LoadedMvid != true) return Reject(checks, out reason);
            checks.BackendSelection = safetyHookSelected();
            if (checks.BackendSelection != true) return Reject(checks, out reason);
            var boot = bootQualifier();
            checks.BootReason = boot.Reason;
            bootIdentity = boot.Identity;
            if (!boot.Qualified)
            {
                reason = boot.Reason + " [qualification diagnostics: " + checks.Format() + "]";
                return false;
            }
            reason = checks.Format();
            return true;
        }
        catch (Exception error)
        {
            bootIdentity = null;
            reason = "Official native backend qualification unavailable: " + error.GetType().Name;
            return false;
        }

        static bool Reject(QualificationChecks checks, out string reason)
        {
            reason = "Official native qualification rejected: " + checks.Format();
            return false;
        }
    }

    private sealed class QualificationChecks
    {
        public bool? AssemblyCollectible { get; init; }
        public bool? ProviderAssembly { get; set; }
        public bool? ProviderType { get; set; }
        public bool? DalamudDiskHash { get; set; }
        public bool? LoadedMvid { get; set; }
        public bool? BackendSelection { get; set; }
        public string? BootReason { get; set; }

        public string Format() => string.Join("; ",
            "assemblyCollectible=" + State(AssemblyCollectible),
            "providerAssembly=" + State(ProviderAssembly),
            "providerType=" + State(ProviderType),
            "dalamudDiskHash=" + State(DalamudDiskHash),
            "loadedMvid=" + State(LoadedMvid),
            "backendSelection=" + State(BackendSelection),
            "boot=" + (BootReason is null ? "not-evaluated" : BootReason));

        private static string State(bool? value) => value switch
        {
            true => "true",
            false => "false",
            _ => "not-evaluated",
        };
    }

    public bool TryVerifyUnmodifiedTargets(PortraitNativePhaseResolvedPlan plan, out string reason)
    {
        reason = "The audited native backend or an observation entry changed before official Hook creation/enable.";
        return QualificationPreserved() && plan.Targets.All(VerifyEntry);
    }

    public Delegate DirectTarget(PortraitNativePhaseBoundTarget target, Type delegateType)
        => DirectTarget(target.Address, delegateType);

    public Delegate DirectTarget(nint address, Type delegateType)
        => Marshal.GetDelegateForFunctionPointer(address, delegateType);

    public IPortraitNativePhaseHostHook Create(PortraitNativePhaseBoundTarget target, Type delegateType, Delegate detour)
        => CreateQualifiedHook(target.Address, delegateType, detour, () => VerifyEntry(target));

    public IPortraitNativePhaseHostHook CreateQualifiedHook(nint address, Type delegateType, Delegate detour, Func<bool> verifyEntry)
    {
        if (!QualificationPreserved() || !verifyEntry()
            || detour.GetType() != delegateType || delegateType.Assembly.IsCollectible)
            throw new InvalidOperationException("Official StartDisabled hook qualification was not preserved.");
        var create = typeof(IGameInteropProvider).GetMethods().Single(method => method.Name == nameof(IGameInteropProvider.HookFromAddress)
            && method.IsGenericMethodDefinition && method.GetParameters() is var parameters && parameters.Length == 3 && parameters[0].ParameterType == typeof(nint));
        object hook;
        try { hook = create.MakeGenericMethod(delegateType).Invoke(interop, [address, detour, IGameInteropProvider.HookBackend.Automatic])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        return new OfficialHook(hook, verifyEntry, QualificationPreserved);
    }

    public bool QualificationPreserved() => qualified && bootIdentity is not null && UsesSafetyHook(typeof(Hook<>).Assembly)
        && PortraitNativeBootImageQualifier.TryQualifyCurrentProcess(out var current, out _) && current == bootIdentity;

    private static bool UsesSafetyHook(Assembly assembly)
        => assembly.GetType("Dalamud.Configuration.Internal.EnvironmentConfiguration", false)?
            .GetProperty("DalamudUseSafetyHook", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;

    private static bool HashMatches(string path, string expected)
    { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)) == expected; }

    private static bool LoadedMVIDMatchesDisk(Assembly assembly)
    {
        using var input = File.OpenRead(assembly.Location); using var pe = new PEReader(input);
        if (!pe.HasMetadata) return false;
        var metadata = pe.GetMetadataReader(); return metadata.GetGuid(metadata.GetModuleDefinition().Mvid) == assembly.ManifestModule.ModuleVersionId;
    }

    private static bool VerifyEntry(PortraitNativePhaseBoundTarget target)
    {
        var contract = PortraitNativeCounterIngressContract.Find(target.Phase);
        if (contract is null || target.DelegateType != contract.Delegate) return false;
        if (PortraitNativeResourceIngressContract.Find(target.Phase) is { } resource)
            return PortraitNativeResourceIngressContract.TryReadCompleteBody(new PortraitNativePhaseCurrentProcess(_ => false),
                target.Address - (nint)contract.RVA, resource, out _);
        var prefix = Convert.FromHexString(contract.Prefix); var observed = new byte[prefix.Length];
        return new PortraitNativePhaseCurrentProcess(_ => false).TryReadMemory(target.Address, observed) && observed.AsSpan().SequenceEqual(prefix);
    }

    private sealed class OfficialHook(object hook, Func<bool> verifyEntry, Func<bool> qualificationPreserved) : IPortraitNativePhaseHostHook, IPortraitNativeHookInspection
    {
        private readonly Type type = hook.GetType();
        public nint Address => (nint)type.GetProperty("Address")!.GetValue(hook)!;
        public string BackendName => (string)type.GetProperty("BackendName")!.GetValue(hook)!;
        public bool IsEnabled => (bool)type.GetProperty("IsEnabled")!.GetValue(hook)!;
        public bool IsDisposed => (bool)type.GetProperty("IsDisposed")!.GetValue(hook)!;
        public Delegate Original => (Delegate)type.GetProperty("Original")!.GetValue(hook)!;
        public bool TryInspect(Func<object, bool> inspect) => inspect(hook);
        public void Enable()
        {
            if (!qualificationPreserved() || !verifyEntry()) throw new InvalidOperationException("The native backend or observation entry changed before enable.");
            type.GetMethod("Enable")!.Invoke(hook, null);
        }
        public void Disable() => type.GetMethod("Disable")!.Invoke(hook, null);
        public void Dispose() => ((IDisposable)hook).Dispose();
    }
}
