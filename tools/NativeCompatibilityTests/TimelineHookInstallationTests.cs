using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
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
    ];

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
