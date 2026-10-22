using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Compatibility;
using FireGaze.LocalPreview.Runtime;

// Substitute only the native patch backend. The real runtime constructor,
// signature gate, ownership list and disposal path remain production code.
static class TimelineHookInstallationTests
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    public static IEnumerable<(string Name, Action Run)> Cases() => [
        ("SDK return mismatch never reaches either timeline hook factory; core hooks remain enabled", RejectedBeforeFactory),
        ("optional hook factory failure preserves installed core hooks", FactoryFailure),
        ("optional hook Enable failure disposes only the rejected hook", EnableFailure),
        ("optional hook cleanup failure cannot disable core or later optional hooks", CleanupFailure),
        ("optional hook signature gate rejects argument and marshalling mismatches", SignatureMismatch),
        ("core bridge alone cannot admit complete HMS handoff", HandoffMissingObservers),
        ("one active timeline cannot admit complete HMS handoff", HandoffPartialObservers),
        ("two active SDK timeline hooks admit HMS handoff until one is disabled", HandoffSdkLifetime),
        ("qualified resolved timelines admit only after both are published", HandoffResolvedAdmission),
        ("released resolved hook revokes HMS handoff dynamically", HandoffResolvedLifetime),
        ("native closing withdraws complete HMS handoff before hook disposal", HandoffClosing),
        ("runtime disposal withdraws complete HMS handoff", HandoffDisposed),
        ("actual adapter refuses either single observer from the real runtime", HandoffAdapterPartial),
        ("actual adapter follows real runtime readiness loss and recovery", HandoffAdapterLifetime),
        ("actual adapter without HMS accepts the real degraded runtime", HandoffAdapterWithoutHms),
    ];

    // These cases read the real production runtime. Only the native patch backend
    // is substituted; no fake readiness boolean supplies the expected result.
    static void HandoffMissingObservers()
    {
        using var runtime = Create(new RecordingInterop());
        Equal(runtime.Capabilities.InputBridge, true, "core bridge is available");
        Equal(Handoff(runtime).Installed, false, "missing observers cannot prove HMS input handoff");
    }
    static void HandoffPartialObservers()
    {
        foreach (var field in new[] { "timelinePlay", "actionTimelinePlay" })
        {
            using var runtime = Create(new RecordingInterop());
            InstallObserver(runtime, field);
            Equal(Handoff(runtime).Installed, false, "each native entry needs its own observer");
        }
    }
    static void HandoffSdkLifetime()
    {
        using var runtime = Create(new RecordingInterop());
        var timeline = InstallObserver(runtime, "timelinePlay");
        InstallObserver(runtime, "actionTimelinePlay");
        Equal(Handoff(runtime).Installed, true, "both SDK routes enabled");
        HookBackendDouble.Get(timeline).Enabled = false;
        Equal(Handoff(runtime).Installed, false, "disabled hook cannot retain readiness");
        HookBackendDouble.Get(timeline).Enabled = true;
        Equal(Handoff(runtime).Installed, true, "reenabled pair supplies current readiness");
    }
    static void HandoffResolvedAdmission()
    {
        using var runtime = Create(new RecordingInterop());
        InstallObserver(runtime, "resolvedTimelinePlay");
        InstallObserver(runtime, "resolvedActionTimelinePlay");
        Equal(Handoff(runtime).Installed, false, "prepared hooks are not admitted observers");
        typeof(ActorPreviewRuntime).GetField("resolvedTimelineAdmitted", Private)!.SetValue(runtime, true);
        Equal(Handoff(runtime).Installed, false, "one admitted entry is incomplete");
        typeof(ActorPreviewRuntime).GetField("resolvedActionTimelineAdmitted", Private)!.SetValue(runtime, true);
        Equal(Handoff(runtime).Installed, true, "both qualified internal entries admitted");
    }
    static void HandoffResolvedLifetime()
    {
        using var runtime = Create(new RecordingInterop());
        InstallObserver(runtime, "resolvedTimelinePlay");
        var action = InstallObserver(runtime, "resolvedActionTimelinePlay");
        foreach (var field in new[] { "resolvedTimelineAdmitted", "resolvedActionTimelineAdmitted" })
            typeof(ActorPreviewRuntime).GetField(field, Private)!.SetValue(runtime, true);
        Equal(Handoff(runtime).Installed, true, "admitted resolved routes available");
        action.Dispose();
        Equal(Handoff(runtime).Installed, false, "released hook cannot retain readiness");
    }
    static void HandoffClosing()
    {
        using var runtime = Create(new RecordingInterop());
        InstallObserver(runtime, "timelinePlay"); InstallObserver(runtime, "actionTimelinePlay");
        Equal(Handoff(runtime).Installed, true, "precondition");
        typeof(ActorPreviewRuntime).GetField("nativeClosing", Private)!.SetValue(runtime, true);
        Equal(Handoff(runtime).Installed, false, "closing callbacks bypass input handoff");
    }
    static void HandoffDisposed()
    {
        using var runtime = Create(new RecordingInterop());
        InstallObserver(runtime, "timelinePlay"); InstallObserver(runtime, "actionTimelinePlay");
        Equal(Handoff(runtime).Installed, true, "precondition");
        runtime.Dispose();
        Equal(Handoff(runtime).Installed, false, "disposed runtime cannot hand off");
    }
    static void HandoffAdapterPartial()
    {
        foreach (var field in new[] { "timelinePlay", "actionTimelinePlay" })
        {
            using var runtime = Create(new RecordingInterop());
            InstallObserver(runtime, field);
            using var hms = new AuditedHms(runtime);
            Equal(hms.Adapter.Refresh().Allowed, false, "a single real observer cannot authorize HMS trials");
            Equal(typeof(ActorPreviewRuntime).GetField("hmsVisibilityProtection", Private)!.GetValue(runtime), false,
                "degraded route cannot retain HMS clone protection");
        }
    }
    static void HandoffAdapterLifetime()
    {
        using var runtime = Create(new RecordingInterop());
        var timeline = InstallObserver(runtime, "timelinePlay");
        InstallObserver(runtime, "actionTimelinePlay");
        using var hms = new AuditedHms(runtime);
        Equal(hms.Adapter.Refresh().Allowed, true, "full actual pair permits audited HMS");
        Equal(typeof(ActorPreviewRuntime).GetField("hmsVisibilityProtection", Private)!.GetValue(runtime), true, "protection active");
        HookBackendDouble.Get(timeline).Enabled = false;
        Equal(hms.Adapter.Refresh().Allowed, false, "actual disabled observer revokes adapter admission");
        Equal(typeof(ActorPreviewRuntime).GetField("hmsVisibilityProtection", Private)!.GetValue(runtime), false, "protection withdrawn");
        HookBackendDouble.Get(timeline).Enabled = true;
        Equal(hms.Adapter.Refresh().Allowed, true, "actual current readiness reopens admission");
    }
    static void HandoffAdapterWithoutHms()
    {
        using var runtime = Create(new RecordingInterop());
        using var hms = new AuditedHms(runtime, loaded: false);
        Equal(hms.Adapter.Refresh().Allowed, true, "standalone does not require either optional HMS observer");
    }
    sealed class AuditedHms : IDisposable
    {
        readonly AssemblyLoadContext context = new("HMS real runtime handoff " + Guid.NewGuid(), true);
        public HmsOriginalAdapter Adapter { get; }
        public AuditedHms(ActorPreviewRuntime runtime, bool loaded = true)
        {
            context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncherCN/installedPlugins/HMSync/1.0.4.2/HMSync.dll");
            var target = context.LoadFromAssemblyPath(path);
            var root = target.GetType("HMSync.HMSyncPlugin", true)!;
            var instance = RuntimeHelpers.GetUninitializedObject(root);
            foreach (var name in new[] { "zoneLoad", "relay", "actorVisibility" })
            {
                var field = root.GetField(name, Private)!;
                var owner = RuntimeHelpers.GetUninitializedObject(field.FieldType);
                field.SetValue(instance, owner);
                if (name == "actorVisibility") field.FieldType.GetField("active", Private)!.SetValue(owner, true);
            }
            var exposedType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.ExposedPlugin", true)!;
            var localType = exposedType.GetFields(Private)
                .Single(f => f.FieldType.FullName == "Dalamud.Plugin.Internal.Types.LocalPlugin").FieldType;
            var local = RuntimeHelpers.GetUninitializedObject(localType);
            localType.GetField("instance", Private)!.SetValue(local, instance);
            localType.GetField("<DllFile>k__BackingField", Private)!.SetValue(local, new FileInfo(path));
            var exposed = Proxy.Create<IExposedPlugin>((method, _) => method.Name switch {
                "get_IsLoaded" => loaded, "get_InternalName" => "HMSync", "get_Name" => "HMapSync", _ => null });
            ((Proxy)(object)exposed).plugin = local;
            var host = Proxy.Create<IDalamudPluginInterface>((method, _) => method.Name == "get_InstalledPlugins" ? new[] { exposed } : null);
            Adapter = new(host, Proxy.Create<ICommandManager>((_, _) => null), runtime,
                Proxy.Create<IPluginLog>((_, _) => null), () => null);
        }
        public void Dispose() { Adapter.Dispose(); context.Unload(); }
    }
    static (bool Installed, string Reason) Handoff(ActorPreviewRuntime runtime)
    {
        var property = typeof(ActorPreviewRuntime).GetProperty("HmsInputHandoffStatus");
        if (property is null) throw new Exception("core InputBridge cannot prove both HMS timeline routes: missing runtime readiness");
        var value = property.GetValue(runtime)!;
        return ((bool)value.GetType().GetProperty("Installed")!.GetValue(value)!,
            (string)value.GetType().GetProperty("Reason")!.GetValue(value)!);
    }
    static IDisposable InstallObserver(ActorPreviewRuntime runtime, string fieldName)
    {
        var field = typeof(ActorPreviewRuntime).GetField(fieldName, Private)!;
        return (IDisposable)typeof(TimelineHookInstallationTests).GetMethod(nameof(InstallObserverTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(field.FieldType.GetGenericArguments()[0]).Invoke(null, [runtime, field])!;
    }
    static IDisposable InstallObserverTyped<T>(ActorPreviewRuntime runtime, FieldInfo field) where T : Delegate
    {
        // The real installer gate and ownership path run with matching audited
        // delegate metadata; only the patch implementation itself is a double.
        var result = Install<T, T>(runtime, () => HookBackendDouble.Create<T>());
        if (result.Hook is not IDisposable hook) throw new Exception("test backend could not install observer: " + result.Reason);
        field.SetValue(runtime, hook);
        return hook;
    }

    static void RejectedBeforeFactory()
    {
        var interop = new RecordingInterop();
        using var runtime = Create(interop);
        Equal(interop.Hooks.Count, 16, "only the sixteen core hooks may reach native backend");
        Equal(interop.Hooks.All(h => HookBackendDouble.Get(h).Enabled), true, "core hooks stay enabled");
        Equal(runtime.Capabilities.InputBridge, true, "optional rejection cannot disable core input bridge");
        foreach (var name in new[] { "TimelinePlayStatus", "ActionTimelinePlayStatus" })
        {
            var property = typeof(ActorPreviewRuntime).GetProperty(name);
            if (property is null) throw new Exception("missing readable degradation status: " + name);
            var status = property.GetValue(runtime)!;
            Equal((bool)status.GetType().GetProperty("Installed")!.GetValue(status)!, false, name);
            var reason = (string)status.GetType().GetProperty("Reason")!.GetValue(status)!;
            if (!reason.Contains("signature", StringComparison.OrdinalIgnoreCase)
                || !reason.Contains("Boolean") || !reason.Contains("Void"))
                throw new Exception("degradation must explain actual SDK return mismatch: " + reason);
        }
        runtime.Dispose();
        Equal(interop.Hooks.All(h => HookBackendDouble.Get(h).Disposals == 1), true, "core ownership disposed once");
        foreach (var name in new[] { "TimelinePlayStatus", "ActionTimelinePlayStatus" })
        {
            var status = typeof(ActorPreviewRuntime).GetProperty(name)!.GetValue(runtime)!;
            Equal((bool)status.GetType().GetProperty("Installed")!.GetValue(status)!, false, "disposed observer unavailable");
            Equal((string)status.GetType().GetProperty("Reason")!.GetValue(status)!, "Runtime disposed.", "disposed state readable");
        }
    }

    static void FactoryFailure()
    {
        var interop = new RecordingInterop(); using var runtime = Create(interop);
        var calls = 0;
        var result = Install<Action, Action>(runtime, () => { calls++; throw new InvalidOperationException("factory unavailable"); });
        Equal(result.Hook, null, "factory failure yields no hook"); Equal(calls, 1, "one factory call");
        if (!result.Reason.Contains("factory unavailable")) throw new Exception("factory failure not readable");
        Equal(runtime.Capabilities.InputBridge, true, "core input bridge retained");
        Equal(interop.Hooks.All(h => HookBackendDouble.Get(h).Enabled), true, "core hooks retained");
        var later = HookBackendDouble.Create<Action>();
        Equal(Install<Action, Action>(runtime, () => later).Hook, later, "next optional install can succeed");
        runtime.Dispose(); Equal(HookBackendDouble.Get(later).Disposals, 1, "successful optional hook owned by runtime");
    }

    static void EnableFailure() => FailedEnable(false);
    static void CleanupFailure() => FailedEnable(true);
    static void FailedEnable(bool cleanupFails)
    {
        var interop = new RecordingInterop(); using var runtime = Create(interop);
        var hook = HookBackendDouble.Create<Action>();
        HookBackendDouble.Get(hook).FailEnable = true;
        HookBackendDouble.Get(hook).FailDispose = cleanupFails;
        var result = Install<Action, Action>(runtime, () => hook);
        Equal(result.Hook, null, "failed enable yields no hook");
        Equal(HookBackendDouble.Get(hook).Disposals, 1, "rejected hook disposed immediately once");
        if (!result.Reason.Contains("enable unavailable")) throw new Exception("enable failure not readable");
        Equal(interop.Hooks.All(h => HookBackendDouble.Get(h).Enabled), true, "core hooks retained");
        var later = HookBackendDouble.Create<Action>();
        Equal(Install<Action, Action>(runtime, () => later).Hook, later, "independent optional hook survives previous failure");
        Equal(Owned(runtime).Contains(hook), false, "rejected hook absent from runtime ownership");
        Equal(Owned(runtime).Contains(later), true, "successful optional hook owned");
        runtime.Dispose();
        Equal(HookBackendDouble.Get(hook).Disposals, 1, "rejected hook not disposed again on unload");
        Equal(HookBackendDouble.Get(later).Disposals, 1, "independent hook disposed on unload");
    }

    [return: MarshalAs(UnmanagedType.U1)] delegate bool U1Result(int value);
    [return: MarshalAs(UnmanagedType.Bool)] delegate bool BoolResult(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CdeclCall();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void StdCall();
    static void SignatureMismatch()
    {
        using var runtime = Create(new RecordingInterop());
        var calls = 0;
        var result = Install<Action<int>, Action<short>>(runtime, () => { calls++; return HookBackendDouble.Create<Action<int>>(); });
        Equal(result.Hook, null, "argument mismatch rejected");
        var marshal = Install<U1Result, BoolResult>(runtime, () => { calls++; return HookBackendDouble.Create<U1Result>(); });
        Equal(marshal.Hook, null, "return marshalling mismatch rejected");
        var convention = Install<CdeclCall, StdCall>(runtime, () => { calls++; return HookBackendDouble.Create<CdeclCall>(); });
        Equal(convention.Hook, null, "calling convention mismatch rejected");
        Equal(calls, 0, "incompatible delegates never reach native backend");
    }

    static (object? Hook, string Reason) Install<T, TSdk>(ActorPreviewRuntime runtime, Func<Hook<T>> create)
        where T : Delegate where TSdk : Delegate
    {
        var method = typeof(ActorPreviewRuntime).GetMethod("InstallOptionalTimelineHook", Private);
        if (method is null) throw new Exception("production optional timeline install boundary is absent");
        object?[] args = [create, null];
        var hook = method.MakeGenericMethod(typeof(T), typeof(TSdk)).Invoke(runtime, args);
        return (hook, (string)args[1]!);
    }
    static List<IDisposable> Owned(ActorPreviewRuntime runtime)
        => (List<IDisposable>)typeof(ActorPreviewRuntime).GetField("installedHooks", Private)!.GetValue(runtime)!;
    internal static ActorPreviewRuntime Create(RecordingInterop interop) => new(
        Proxy.Create<IFramework>((m, _) => m.Name == "get_IsInFrameworkUpdateThread" ? true : null),
        Proxy.Create<IClientState>((m, _) => m.Name == "get_TerritoryType" ? 1u : m.ReturnType == typeof(bool) ? false : null),
        Proxy.Create<IObjectTable>((_, _) => null), Proxy.Create<ICondition>((_, _) => false),
        null!, interop, Proxy.Create<IPluginLog>((_, _) => null));
    static void Equal<T>(T actual, T expected, string message)
    { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"{message}: expected {expected}; got {actual}"); }

    internal sealed unsafe class RecordingInterop : IGameInteropProvider
    {
        public List<IDisposable> Hooks { get; } = [];
        public int ThrowAtRecord { get; set; }
        public bool FailAllDispose { get; set; }
        Hook<T> Record<T>() where T : Delegate
        {
            if (ThrowAtRecord == Hooks.Count + 1) throw new InvalidOperationException("core factory unavailable");
            var hook = HookBackendDouble.Create<T>(); HookBackendDouble.Get(hook).FailDispose = FailAllDispose;
            Hooks.Add(hook); return hook;
        }
        public Hook<T> HookFromSignature<T>(string signature, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate => Record<T>();
        public Hook<T> HookFromAddress<T>(nint address, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate => Record<T>();
        public Hook<T> HookFromAddress<T>(nuint address, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate => Record<T>();
        public Hook<T> HookFromAddress<T>(void* address, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate => Record<T>();
        public void InitializeFromAttributes(object self) => throw new NotSupportedException();
        public Hook<T> HookFromFunctionPointerVariable<T>(nint address, T detour) where T : Delegate => throw new NotSupportedException();
        public Hook<T> HookFromImport<T>(ProcessModule? module, string moduleName, string functionName, uint hintOrOrdinal, T detour) where T : Delegate => throw new NotSupportedException();
        public Hook<T> HookFromSymbol<T>(string moduleName, string exportName, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate => throw new NotSupportedException();
    }
    public class Proxy : DispatchProxy
    {
        public object? plugin;
        Func<MethodInfo, object?[]?, object?> handler = null!;
        public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        { var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).handler = handler; return proxy; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => handler(method!, args);
    }
}

// Test process only: derive a backend from the installed SDK without native
// patches. Access suppression lives exclusively in this dynamic test assembly.
public static class HookBackendDouble
{
    public sealed class State { public bool Enabled, FailEnable, FailDispose; public int Disposals; public Action? OnEnable; }
    static readonly ConditionalWeakTable<object, State> states = new();
    static readonly Dictionary<Type, Type> types = [];
    static readonly ModuleBuilder module = BuildModule();
    static ModuleBuilder BuildModule()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TimelineHookBackendTests"), AssemblyBuilderAccess.Run);
        var attr = typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructors().Single();
        foreach (var name in new[] { "Dalamud", "FireGaze.LocalPreview", typeof(HookBackendDouble).Assembly.GetName().Name! })
            assembly.SetCustomAttribute(new CustomAttributeBuilder(attr, [name]));
        return assembly.DefineDynamicModule("Backend");
    }
    public static State Get(object hook) => states.GetOrCreateValue(hook);
    public static void Enable(object hook) { var s = Get(hook); s.OnEnable?.Invoke(); if (s.FailEnable) throw new InvalidOperationException("enable unavailable"); s.Enabled = true; }
    public static void Dispose(object hook) { var s = Get(hook); s.Disposals++; s.Enabled = false; if (s.FailDispose) throw new InvalidOperationException("dispose unavailable"); }
    public static bool Enabled(object hook) => Get(hook).Enabled;
    public static Hook<T> Create<T>() where T : Delegate
    {
        if (!types.TryGetValue(typeof(T), out var type))
        {
            var parent = typeof(Hook<T>);
            var builder = module.DefineType("Backend" + types.Count, TypeAttributes.Public | TypeAttributes.Sealed, parent);
            var ctor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var il = ctor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Call, parent.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()); il.Emit(OpCodes.Ret);
            foreach (var (name, helper, returns) in new[] { ("Enable", nameof(Enable), typeof(void)), ("Dispose", nameof(Dispose), typeof(void)), ("get_IsEnabled", nameof(Enabled), typeof(bool)) })
            {
                var method = builder.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, returns, Type.EmptyTypes);
                il = method.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(HookBackendDouble).GetMethod(helper)!);
                if (name == "Dispose") { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, parent.GetMethod("Dispose")!); }
                il.Emit(OpCodes.Ret); builder.DefineMethodOverride(method, parent.GetMethod(name)!);
            }
            type = builder.CreateType()!; types.Add(typeof(T), type);
        }
        return (Hook<T>)Activator.CreateInstance(type)!;
    }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class IgnoresAccessChecksToAttribute(string assemblyName) : Attribute
    { public string AssemblyName { get; } = assemblyName; }
}
