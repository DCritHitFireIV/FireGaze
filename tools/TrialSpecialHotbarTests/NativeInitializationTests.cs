using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Interaction;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

internal static unsafe class NativeInitializationTests
{
    public static void Run(Action<bool, string> check)
    {
        // This is the actual installed SDK structure, not a reduced test layout. The
        // marshaler rejects some of its nested native fields even when offsets are valid.
        var module = (RaptureHotbarModule*)NativeMemory.AllocZeroed((nuint)sizeof(RaptureHotbarModule));
        try
        {
            var petOffset = (byte*)(&module->PetHotbar) - (byte*)module;
            Console.WriteLine($"Actual FFCS: {typeof(RaptureHotbarModule).Assembly.GetName().Version}; " +
                $"PetHotbar=0x{petOffset:X}; slot=0x{sizeof(RaptureHotbarModule.HotbarSlot):X}");
        }
        finally { NativeMemory.Free(module); }

        var interop = new NoNativeInterop();
        Exception? observedFailure = null;
        var lifecycleCalls = 0;
        var lifecycle = NativeTestProxy.Create<IAddonLifecycle>((_, _) => { lifecycleCalls++; return null; });
        var logger = NativeTestProxy.Create<IPluginLog>((method, args) =>
        {
            if (args is not null)
                foreach (var arg in args)
                    if (arg is Exception ex) observedFailure = ex;
            return null;
        });
        var bridge = new TrialNativeSpecialHotbar(null!, null!, lifecycle, interop, logger, (_, _) => false);
        if (interop.Requests != 1 || !ReferenceEquals(observedFailure, interop.Failure))
            Console.WriteLine($"Native initialization diagnostic: requests={interop.Requests}, failure={observedFailure}");
        check(interop.Requests == 1 && ReferenceEquals(observedFailure, interop.Failure),
            "actual SDK layout lets the production constructor reach the native hook boundary");
        check(!bridge.Available && !bridge.Active && bridge.InputsConsumed == 0,
            "native backend initialization failure leaves the trial bar unavailable and input unclaimed");
        check(lifecycleCalls == 0,
            "failed native hook construction registers no addon callbacks");
    }

    private sealed class NoNativeInterop : IGameInteropProvider
    {
        public int Requests { get; private set; }
        public Exception Failure { get; } = new InvalidOperationException("test host deliberately has no game hook backend");
        public Hook<T> HookFromSignature<T>(string signature, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate
        {
            Requests++;
            // No Hook is constructed or enabled. Only this external boundary is
            // substituted; the production constructor and actual SDK layout run.
            throw Failure;
        }
        public void InitializeFromAttributes(object self) => throw new NotSupportedException();
        public Hook<T> HookFromFunctionPointerVariable<T>(nint address, T detour) where T : Delegate => throw new NotSupportedException();
        public Hook<T> HookFromImport<T>(ProcessModule? module, string moduleName, string functionName, uint hintOrOrdinal, T detour) where T : Delegate => throw new NotSupportedException();
        public Hook<T> HookFromSymbol<T>(string moduleName, string exportName, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate => throw new NotSupportedException();
        public Hook<T> HookFromAddress<T>(nint address, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate => throw new NotSupportedException();
        public Hook<T> HookFromAddress<T>(nuint address, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate => throw new NotSupportedException();
        public Hook<T> HookFromAddress<T>(void* address, T detour, IGameInteropProvider.HookBackend backend = IGameInteropProvider.HookBackend.Automatic) where T : Delegate => throw new NotSupportedException();
    }
}

public class NativeTestProxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?>? handle;
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var result = DispatchProxy.Create<T, NativeTestProxy>();
        ((NativeTestProxy)(object)result).handle = handler;
        return result;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => handle!(targetMethod!, args);
}
