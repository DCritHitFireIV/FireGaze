using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitNativePhaseHostGateway
{
    // Stable numeric ABI identifiers; this code never references collectible owned dispatcher
    // delegate types. The exact signatures are shared only by the fixed owned native inventory.
    public static bool TryCreateForwarded(int abi, Type delegateType, nint address,
        out PortraitNativePhaseHostGateway? gateway, out string reason)
    {
        gateway = null; reason = "The forwarded native ABI is outside the exact owned inventory.";
        if (address <= 0 || !TryForwardedABI(abi, out var parameters, out var result, out var convention)
            || delegateType.GetMethod("Invoke") is not { } invoke || invoke.ReturnType != result
            || !invoke.GetParameters().Select(value => value.ParameterType).SequenceEqual(parameters)
            || (delegateType.GetCustomAttribute<UnmanagedFunctionPointerAttribute>()?.CallingConvention ?? CallingConvention.Winapi) != convention
            || parameters.Any(type => !StableType(type)) || !StableType(result)) return false;
        return TryCreateCore(abi, parameters, result, address, false, out gateway, out reason, true, convention);
    }

    public void SetForward(Delegate callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (observer.FieldType != DelegateType || callback.GetInvocationList().Length != 1)
            throw new ArgumentException("Only this exact forwarded native ABI accepts a single typed dispatcher.");
        var value = callback.Target is null ? Delegate.CreateDelegate(DelegateType, callback.Method)
            : Delegate.CreateDelegate(DelegateType, callback.Target, callback.Method);
        lock (gate)
        {
            CheckOwnership();
            if ((bool)retired.GetValue(instance)!) throw new InvalidOperationException("A retired native gateway cannot dispatch a plugin.");
            observer.SetValue(instance, value);
        }
    }

    private static unsafe bool TryForwardedABI(int abi, out Type[] parameters, out Type result, out CallingConvention convention)
    {
        convention = abi >= 8 ? CallingConvention.Cdecl : CallingConvention.Winapi;
        result = abi switch { 0 => typeof(CharaView*), 6 => typeof(uint), 8 or 9 => typeof(byte), 11 or 12 => typeof(nint), _ => typeof(void) };
        parameters = abi switch
        {
            0 => [typeof(CharaView*), typeof(byte)],
            1 => [typeof(CharaView*)],
            2 => [typeof(CharaView*), typeof(AgentInterface*), typeof(uint), typeof(nint)],
            3 => [typeof(CharaView*), typeof(uint), typeof(Character*)],
            4 => [typeof(CharaViewPortrait*), typeof(uint), typeof(CharaViewCharacterData*), typeof(AgentInterface*), typeof(int), typeof(ulong)],
            5 => [typeof(CharaView*), typeof(uint)],
            6 => [typeof(ClientObjectManager*), typeof(uint), typeof(byte)],
            7 => [typeof(ClientObjectManager*), typeof(ushort), typeof(byte)],
            8 => [typeof(nint), typeof(uint), typeof(uint)],
            9 => [typeof(nint), typeof(uint)],
            10 or 11 => [typeof(nint)],
            12 => [typeof(nint), typeof(byte)],
            _ => [],
        };
        return parameters.Length != 0;
    }

    private static void BuildForwardedInvoke(TypeBuilder type, Type delegateType, Type[] parameters, Type returnType,
        FieldBuilder gate, FieldBuilder original, FieldBuilder forward, FieldBuilder active)
    {
        var il = type.DefineMethod("Invoke", MethodAttributes.Public, returnType, parameters).GetILGenerator();
        var copied = il.DeclareLocal(delegateType);
        var result = returnType == typeof(void) ? null : il.DeclareLocal(returnType);
        var monitorEnter = typeof(Monitor).GetMethod(nameof(Monitor.Enter), [typeof(object)])!;
        var monitorExit = typeof(Monitor).GetMethod(nameof(Monitor.Exit), [typeof(object)])!;
        Load(gate); il.Emit(OpCodes.Call, monitorEnter);
        il.BeginExceptionBlock();
        AddActive(1);
        Load(forward); il.Emit(OpCodes.Stloc, copied);
        var copiedForward = il.DefineLabel(); il.Emit(OpCodes.Ldloc, copied); il.Emit(OpCodes.Brtrue, copiedForward);
        Load(original); il.Emit(OpCodes.Stloc, copied); il.MarkLabel(copiedForward);
        il.BeginFinallyBlock(); Load(gate); il.Emit(OpCodes.Call, monitorExit); il.EndExceptionBlock();
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Ldloc, copied);
        for (var index = 0; index < parameters.Length; index++) il.Emit(OpCodes.Ldarg, index + 1);
        il.Emit(OpCodes.Callvirt, delegateType.GetMethod("Invoke")!);
        if (result is not null) il.Emit(OpCodes.Stloc, result);
        il.BeginFinallyBlock();
        il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Stloc, copied);
        Load(gate); il.Emit(OpCodes.Call, monitorEnter);
        il.BeginExceptionBlock(); AddActive(-1);
        il.BeginFinallyBlock(); Load(gate); il.Emit(OpCodes.Call, monitorExit); il.EndExceptionBlock();
        il.EndExceptionBlock();
        if (result is not null) il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);
        void Load(FieldBuilder field) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); }
        void AddActive(int delta)
        { il.Emit(OpCodes.Ldarg_0); Load(active); il.Emit(OpCodes.Ldc_I4, delta); il.Emit(OpCodes.Add_Ovf); il.Emit(OpCodes.Stfld, active); }
    }
}
