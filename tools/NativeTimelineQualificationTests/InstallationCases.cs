using System.Reflection;
using System.Reflection.Emit;
using Dalamud.Hooking;
using FireGaze.LocalPreview.Runtime;

internal static unsafe partial class Program
{
    private static IEnumerable<(string Name, System.Action Run)> InstallationCases(
        NativeTimelineModule module, NativeTimelineMetadata metadata, NonExecutableImage mapping)
    {
        foreach (var scenario in new[] { "success", "factory throws", "enable throws", "cleanup throws", "pre-factory drift", "eager backend" })
        {
            var captured = scenario;
            yield return ($"qualified production Hook installation: {scenario}", () => {
                var process = new ProcessFixture(module, metadata, mapping);
                var qualifier = ProcessBridge.CreateQualifier(process);
                object?[] qualify = [null, null];
                Check((bool)qualifier.GetType().GetMethod("TryQualify")!.Invoke(qualifier, qualify)!, "production qualification must accept actual readonly input");
                var plan = qualify[0]!;
                var interop = new TimelineHookInstallationTests.RecordingInterop();
                using var runtime = TimelineHookInstallationTests.Create(interop);
                Check(interop.Hooks.Count == 16, "non-game process must not request an unqualified optional native patch");
                var hook = HookBackendDouble.Create<System.Action>();
                var state = HookBackendDouble.Get(hook);
                Hook<System.Action>? bound = null;
                var factoryCalls = 0;
                var observedBeforeEnable = false;
                state.OnEnable = () => { observedBeforeEnable = ReferenceEquals(bound, hook); };
                state.FailEnable = captured is "enable throws" or "cleanup throws";
                state.FailDispose = captured == "cleanup throws";
                if (captured == "pre-factory drift") process.AlterRva = 0x606B10;
                if (captured == "eager backend") process.Metadata = metadata with { UsesDeferredHookBackend = false };
                Func<Hook<System.Action>> create = () => { factoryCalls++; if (captured == "factory throws") throw new InvalidOperationException("factory unavailable"); return hook; };
                System.Action<Hook<System.Action>?> bind = value => bound = value;
                Func<Hook<System.Action>> createPeer = () => HookBackendDouble.Create<System.Action>();
                System.Action<Hook<System.Action>?> bindPeer = _ => { };
                object?[] args = [qualifier, plan, create, bind, createPeer, bindPeer];
                var install = typeof(ActorPreviewRuntime).GetMethod("InstallQualifiedTimelineBoundaries", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .MakeGenericMethod(typeof(System.Action), typeof(System.Action));
                var result = (ValueTuple<OptionalNativeHookStatus, OptionalNativeHookStatus>)install.Invoke(runtime, args)!;
                var admitted = result.Item1.Installed;
                var owned = (List<IDisposable>)typeof(ActorPreviewRuntime).GetField("installedHooks", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
                Check(admitted == (captured == "success"), "only a fully enabled qualified observer becomes admitted");
                Check(factoryCalls == (captured is "pre-factory drift" or "eager backend" ? 0 : 1), "drift or an eager backend must deny before the native patch factory");
                Check(interop.Hooks.All(core => HookBackendDouble.Get(core).Enabled), "an optional failure cannot unwind any core input Hook");
                if (captured is "success" or "enable throws" or "cleanup throws")
                    Check(observedBeforeEnable, "the Original holder must be bound before Enable exposes its callback");
                Check(owned.Contains(hook) == (captured is "success" or "cleanup throws"), "uncertain cleanup retains callback and Original ownership until unload");
                Check(ReferenceEquals(bound, hook) == (captured is "success" or "cleanup throws"), "only a safely disposed failed hook loses its Original holder");
                if (captured != "success") Check(!string.IsNullOrEmpty(result.Item1.Reason), "denied installation has a diagnostic reason");
                Check(state.Disposals == (captured is "enable throws" or "cleanup throws" ? 1 : 0), "failed Enable attempts one immediate disposal");
                state.FailDispose = false;
                runtime.Dispose();
                Check(interop.Hooks.All(core => HookBackendDouble.Get(core).Disposals == 1), "core ownership unloads once");
                Check(state.Disposals == (captured is "success" or "cleanup throws" or "enable throws" ? 1 : 0),
                    "uncertain failed cleanup is process-rooted rather than retried on unload");
            });
        }
        foreach (var scenario in new[] { "both prepared before enable", "first factory leaves peer drift", "second factory leaves peer drift", "backend changes after first factory" })
        {
            var captured = scenario;
            yield return ($"qualified pair installation: {scenario}", () => {
                var process = new ProcessFixture(module, metadata, mapping);
                var qualifier = ProcessBridge.CreateQualifier(process);
                object?[] qualify = [null, null];
                Check((bool)qualifier.GetType().GetMethod("TryQualify")!.Invoke(qualifier, qualify)!, "pair fixture must qualify");
                var interop = new TimelineHookInstallationTests.RecordingInterop();
                using var runtime = TimelineHookInstallationTests.Create(interop);
                var first = HookBackendDouble.Create<System.Action>();
                var second = HookBackendDouble.Create<System.Action>();
                Hook<System.Action>? firstBound = null, secondBound = null;
                var factories = 0;
                var enables = new List<int>();
                HookBackendDouble.Get(first).OnEnable = () => {
                    enables.Add(factories);
                    Check(ReferenceEquals(firstBound, first) && ReferenceEquals(secondBound, second), "both Originals must exist before the first callback can be exposed");
                    process.AlterRva = 0x606B10; // Expected native entry patch after admission starts.
                };
                HookBackendDouble.Get(second).OnEnable = () => enables.Add(factories);
                Func<Hook<System.Action>> createFirst = () => {
                    factories++;
                    if (captured == "first factory leaves peer drift") process.AlterRva = 0x606C10;
                    if (captured == "backend changes after first factory") process.Metadata = metadata with { UsesDeferredHookBackend = false };
                    return first;
                };
                Func<Hook<System.Action>> createSecond = () => { factories++; if (captured == "second factory leaves peer drift") process.AlterRva = 0x606C10; return second; };
                System.Action<Hook<System.Action>?> bindFirst = value => firstBound = value;
                System.Action<Hook<System.Action>?> bindSecond = value => secondBound = value;
                var install = typeof(ActorPreviewRuntime).GetMethod("InstallQualifiedTimelineBoundaries", BindingFlags.NonPublic | BindingFlags.Instance);
                Check(install is not null, "production must stage both qualified factories before exposing either detour");
                object?[] args = [qualifier, qualify[0], createFirst, bindFirst, createSecond, bindSecond];
                var result = install!.MakeGenericMethod(typeof(System.Action), typeof(System.Action)).Invoke(runtime, args)!;
                var admitted = (ValueTuple<OptionalNativeHookStatus, OptionalNativeHookStatus>)result;
                Check(admitted.Item1.Installed == (captured == "both prepared before enable") && admitted.Item2.Installed == (captured == "both prepared before enable"),
                    "only a fully rechecked pair may enable; disabled factory drift cannot be blessed as an owned patch");
                Check(captured == "both prepared before enable" ? enables.SequenceEqual(new[] { 2, 2 }) : enables.Count == 0,
                    "both factories and the full final byte check must precede the first Enable");
                if (captured != "both prepared before enable")
                    Check(firstBound is null && secondBound is null, "rejected prepared hooks must release their bound holders after successful teardown");
            });
        }
    }

    // Test-only bridge implements the compiled runtime's private read-only
    // process interface. Both qualifiers run real production code on a complete
    // non-executable PE mapping; only the OS reader/native patch backend differ.
    public static class ProcessBridge
    {
        private static readonly Dictionary<object, ProcessFixture> readers = [];
        private static readonly Assembly runtime = typeof(ActorPreviewRuntime).Assembly;
        private static readonly Type moduleType = runtime.GetType("FireGaze.LocalPreview.Runtime.NativeTimelineModule")!;
        private static readonly Type metadataType = runtime.GetType("FireGaze.LocalPreview.Runtime.NativeTimelineMetadata")!;
        private static readonly Type bridgeType = Build();
        private static Type Build()
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("NativeTimelineProcessBridgeTests"), AssemblyBuilderAccess.Run);
            var access = typeof(System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute).GetConstructors().Single();
            foreach (var name in new[] { "FireGaze.LocalPreview", typeof(Program).Assembly.GetName().Name! })
                assembly.SetCustomAttribute(new CustomAttributeBuilder(access, [name]));
            var contract = runtime.GetType("FireGaze.LocalPreview.Runtime.INativeTimelineProcess")!;
            var builder = assembly.DefineDynamicModule("ProcessBridge").DefineType("ReadonlyProcessBridge", TypeAttributes.Public | TypeAttributes.Sealed);
            builder.AddInterfaceImplementation(contract);
            foreach (var method in contract.GetMethods())
            {
                var generated = builder.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
                    typeof(bool), method.GetParameters().Select(p => p.ParameterType).ToArray());
                var il = generated.GetILGenerator();
                if (method.Name == "TryReadMemory")
                {
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Call, typeof(ProcessBridge).GetMethod(nameof(Memory))!);
                }
                else
                {
                    il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Call, typeof(ProcessBridge).GetMethod(method.Name == "TryReadModule" ? nameof(Module) : nameof(Metadata))!);
                    il.Emit(OpCodes.Castclass, method.Name == "TryReadModule" ? moduleType : metadataType);
                    il.Emit(OpCodes.Stind_Ref); il.Emit(OpCodes.Ldc_I4_1);
                }
                il.Emit(OpCodes.Ret); builder.DefineMethodOverride(generated, method);
            }
            return builder.CreateType()!;
        }
        internal static object CreateQualifier(object process)
        {
            var bridge = Activator.CreateInstance(bridgeType)!; readers.Add(bridge, (ProcessFixture)process);
            return Activator.CreateInstance(runtime.GetType("FireGaze.LocalPreview.Runtime.NativeTimelineBoundaryQualifier")!, bridge)!;
        }
        public static object Module(object self)
        {
            readers[self].TryReadModule(out var value);
            return Activator.CreateInstance(moduleType, value.Name, value.BaseAddress, value.Size, value.FileImage)!;
        }
        public static object Metadata(object self)
        {
            readers[self].TryReadMetadata(out var value);
            return Activator.CreateInstance(metadataType, value.SdkImage, value.LoadedSdkMvid, value.DalamudImage, value.LoadedDalamudMvid,
                value.SdkTimeline, value.SdkActionTimeline, value.AllSdkAddresses, value.UsesDeferredHookBackend)!;
        }
        public static bool Memory(object self, nint address, Span<byte> destination) => readers[self].TryReadMemory(address, destination);
    }
}
