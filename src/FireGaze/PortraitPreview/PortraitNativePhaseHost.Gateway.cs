using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace FireGaze.PortraitPreview;

/// <summary>
/// A default-context, noncollectible native gateway. Generated fields and IL
/// reference only BCL and noncollectible ABI types, never this plugin's types.
/// Once detached and redirected, its permanent native entry cannot root a plugin.
/// This class never installs a hook or invokes a native target during creation.
/// </summary>
internal sealed partial class PortraitNativePhaseHostGateway
{
    private static readonly Type ObserverType = typeof(Func<int, nint, uint, float, Action<bool>?>);
    private static readonly Type ResourceObserverType = typeof(Func<int, nint, Action<bool, byte>?>);
    private const string RegistryKey = "FireGaze.NativePhaseGateway.Registry.v1";
    private readonly object instance;
    private readonly object gate;
    private readonly FieldInfo original;
    private readonly FieldInfo observer;
    private readonly FieldInfo active;
    private readonly FieldInfo retired;
    private readonly FieldInfo owner;
    private readonly object ownerToken = new();

    private PortraitNativePhaseHostGateway(Type bridgeType, Type delegateType, object instance, Delegate detour)
    {
        BridgeType = bridgeType; DelegateType = delegateType; this.instance = instance; Detour = detour;
        gate = bridgeType.GetField("Gate")!.GetValue(instance)!;
        original = bridgeType.GetField("Original")!; observer = bridgeType.GetField("Observer")!;
        active = bridgeType.GetField("Active")!; retired = bridgeType.GetField("Retired")!;
        owner = bridgeType.GetField("Owner")!;
    }

    public Type BridgeType { get; }
    public Type DelegateType { get; }
    public Delegate Detour { get; }
    public int ActiveCount { get { lock (gate) return (int)active.GetValue(instance)!; } }

    public static bool TryCreate(int phase, Type[] parameters, nint targetAddress, out PortraitNativePhaseHostGateway? gateway, out string reason)
    {
        gateway = null; reason = "Native gateway ABI must use stable pointer types and an explicit phase or counter void signature.";
        if (targetAddress <= 0 || phase is < 0 or > 5 || parameters is null || parameters.Length != (phase is 1 or 3 or 4 or 5 ? 2 : 1)
            || !parameters[0].IsPointer || parameters.Any(type => !StableType(type))
            || (phase == 1 && parameters[1] != typeof(float)) || (phase is 3 or 4 or 5 && parameters[1] != typeof(uint))) return false;
        return TryCreateCore(phase, parameters, typeof(void), targetAddress, false, out gateway, out reason);
    }

    public static unsafe bool TryCreateResource(int phase, Type[] parameters, Type returnType, nint targetAddress,
        out PortraitNativePhaseHostGateway? gateway, out string reason)
    {
        gateway = null; reason = "Actual resource gateway requires audited RT receiver and exact void/byte ABI.";
        if (targetAddress <= 0 || phase is not (6 or 7) || parameters is null || parameters.Length != 1
            || parameters[0] != typeof(FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager*)
            || !StableType(parameters[0]) || returnType != (phase == 6 ? typeof(void) : typeof(byte))) return false;
        return TryCreateCore(phase, parameters, returnType, targetAddress, true, out gateway, out reason);
    }

    private static bool TryCreateCore(int phase, Type[] parameters, Type returnType, nint targetAddress, bool resource,
        out PortraitNativePhaseHostGateway? gateway, out string reason, bool forwarded = false, CallingConvention convention = CallingConvention.Winapi)
    {
        gateway = null;
        try
        {
            // The table, key strings and values use only default runtime types.
            // A shared interned key serializes its first creation across plugin
            // ALCs; a plugin-local static lock would not protect hot reloads.
            lock (string.Intern(RegistryKey))
            {
            var value = AppContext.GetData(RegistryKey);
            if (value is not null && value is not Dictionary<string, object[]>)
            { reason = "Stable native gateway registry has an incompatible owner."; return false; }
            var registry = (Dictionary<string, object[]>?)value ?? new(StringComparer.Ordinal);
            if (value is null) AppContext.SetData(RegistryKey, registry);
            var key = targetAddress.ToString("X") + "/" + phase + "/" + string.Join(";", parameters.Select(type => type.AssemblyQualifiedName));
            if (resource) key += "/resource-v1/" + (returnType == typeof(byte) ? "u1" : "void");
            if (forwarded) key += "/owned-forward-v1/" + (int)convention + "/" + returnType.AssemblyQualifiedName;
            if (registry.TryGetValue(key, out var existing))
            {
                var candidate = new PortraitNativePhaseHostGateway((Type)existing[0], (Type)existing[1], existing[2], (Delegate)existing[3]);
                if (candidate.observer.FieldType != (forwarded ? candidate.DelegateType : resource ? ResourceObserverType : ObserverType)
                    || candidate.DelegateType.GetMethod("Invoke")!.ReturnType != returnType)
                { reason = "Stable native gateway schema differs from its explicit entry ABI."; return false; }
                if (!candidate.TryAcquire()) { reason = "The native entry gateway is owned or an earlier direct call is still active."; return false; }
                gateway = candidate; reason = "Existing stable entry/ABI gateway acquired; no new dynamic assembly."; return true;
            }
            // Reflection.Emit otherwise inherits a collectible caller's contextual
            // reflection context on supported runtimes. This scope is essential.
            using var context = AssemblyLoadContext.Default.EnterContextualReflection();
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new("FireGaze.NativePhaseGateway." + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("Gateway");
            var delegateType = BuildDelegate(module, parameters, returnType, forwarded ? convention : resource ? CallingConvention.Cdecl : CallingConvention.Winapi);
            var bridge = module.DefineType("NativeGateway", TypeAttributes.Public | TypeAttributes.Sealed);
            var gateField = bridge.DefineField("Gate", typeof(object), FieldAttributes.Public);
            var originalField = bridge.DefineField("Original", delegateType, FieldAttributes.Public);
            var observerField = bridge.DefineField("Observer", forwarded ? delegateType : resource ? ResourceObserverType : ObserverType, FieldAttributes.Public);
            var activeField = bridge.DefineField("Active", typeof(int), FieldAttributes.Public);
            bridge.DefineField("Retired", typeof(bool), FieldAttributes.Public);
            bridge.DefineField("Owner", typeof(object), FieldAttributes.Public);
            var root = bridge.DefineField("Root", typeof(object), FieldAttributes.Public | FieldAttributes.Static);
            var nativeRoot = bridge.DefineField("NativeEntry", delegateType, FieldAttributes.Public);
            bridge.DefineDefaultConstructor(MethodAttributes.Public);
            if (forwarded) BuildForwardedInvoke(bridge, delegateType, parameters, returnType, gateField, originalField, observerField, activeField);
            else if (resource) BuildResourceInvoke(bridge, delegateType, parameters, returnType, phase, gateField, originalField, observerField, activeField);
            else BuildInvoke(bridge, delegateType, parameters, phase, gateField, originalField, observerField, activeField);
            var type = bridge.CreateType()!;
            if (type.Assembly.IsCollectible || AssemblyLoadContext.GetLoadContext(type.Assembly) != AssemblyLoadContext.Default)
            { reason = "The runtime did not place the native gateway in the stable default context."; return false; }
            var state = Activator.CreateInstance(type)!;
            type.GetField(gateField.Name)!.SetValue(state, new object());
            var detour = Delegate.CreateDelegate(delegateType, state, type.GetMethod("Invoke")!);
            type.GetField(nativeRoot.Name)!.SetValue(state, detour);
            // Late native dispatch can outlive the plugin. This root retains only
            // the stable bridge, direct target and a cleared observer after stop.
            type.GetField(root.Name)!.SetValue(null, state);
            var created = new PortraitNativePhaseHostGateway(type, delegateType, state, detour);
            if (!created.TryAcquire()) { reason = "The newly created stable gateway could not be acquired."; return false; }
            registry.Add(key, [type, delegateType, state, detour]); gateway = created;
            reason = "Stable native gateway created; no hook installed and no target invoked."; return true;
            }
        }
        catch (Exception error) { reason = "Stable native gateway creation failed: " + error.Message; return false; }
    }

    public void PublishOriginal(Delegate value)
    {
        ValidateStableOriginal(value);
        lock (gate)
        {
            CheckOwnership();
            if ((int)active.GetValue(instance)! != 0 || (original.GetValue(instance) is not null && !(bool)retired.GetValue(instance)!))
                throw new InvalidOperationException("A previous original is published or a direct call is still active.");
            original.SetValue(instance, value); retired.SetValue(instance, false);
        }
    }

    public void SetObserver(Func<int, nint, uint, float, Action<bool>?> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (observer.FieldType != ObserverType) throw new InvalidOperationException("This gateway requires a resource observer.");
        lock (gate)
        {
            CheckOwnership();
            if ((bool)retired.GetValue(instance)!) throw new InvalidOperationException("A retired native gateway cannot record again.");
            observer.SetValue(instance, value);
        }
    }

    public void SetResourceObserver(Func<int, nint, Action<bool, byte>?> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (observer.FieldType != ResourceObserverType) throw new InvalidOperationException("This gateway requires a phase observer.");
        lock (gate)
        {
            CheckOwnership();
            if ((bool)retired.GetValue(instance)!) throw new InvalidOperationException("A retired native gateway cannot record again.");
            observer.SetValue(instance, value);
        }
    }

    public void DetachObserver() { lock (gate) { if (ReferenceEquals(owner.GetValue(instance), ownerToken)) observer.SetValue(instance, null); } }

    // Call only after the official hook has been disabled. The zero-active test
    // and switch are atomic with native entry; already-dispatched late entries
    // will copy the direct target, never the old trampoline. No wait is performed.
    public bool TryRetireTo(Delegate directTarget)
    {
        ValidateStableOriginal(directTarget);
        lock (gate)
        {
            CheckOwnership();
            observer.SetValue(instance, null);
            if ((int)active.GetValue(instance)! != 0) return false;
            original.SetValue(instance, directTarget); retired.SetValue(instance, true); return true;
        }
    }

    public void ReleaseOwnership()
    {
        lock (gate)
        {
            CheckOwnership();
            if (!(bool)retired.GetValue(instance)! || observer.GetValue(instance) is not null)
                throw new InvalidOperationException("Only a detached direct-target gateway may release its generation owner.");
            owner.SetValue(instance, null);
        }
    }

    public bool TryAbandonUnpublishedOwnership()
    {
        lock (gate)
        {
            CheckOwnership();
            if (observer.GetValue(instance) is not null || (original.GetValue(instance) is not null && !(bool)retired.GetValue(instance)!)) return false;
            retired.SetValue(instance, true); owner.SetValue(instance, null); return true;
        }
    }

    private bool TryAcquire()
    {
        lock (gate)
        {
            if (owner.GetValue(instance) is not null || (int)active.GetValue(instance)! != 0) return false;
            owner.SetValue(instance, ownerToken); return true;
        }
    }

    private void CheckOwnership()
    { if (!ReferenceEquals(owner.GetValue(instance), ownerToken)) throw new InvalidOperationException("This gateway generation no longer owns the native entry."); }

    private void ValidateStableOriginal(Delegate value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.GetType() != DelegateType || value.GetInvocationList().Length != 1 || value.Target is not null
            || !StableType(value.Method.DeclaringType ?? typeof(object)))
            throw new ArgumentException("Original must be one targetless stable native ABI delegate, not a plugin closure.", nameof(value));
    }

    private static bool StableType(Type type)
    {
        if (type.HasElementType) return StableType(type.GetElementType()!);
        if (type.Assembly.IsCollectible) return false;
        return !type.IsGenericType || type.GetGenericArguments().All(StableType);
    }

    private static Type BuildDelegate(ModuleBuilder module, Type[] parameters, Type returnType, CallingConvention convention)
    {
        var type = module.DefineType("NativeEntry", TypeAttributes.Public | TypeAttributes.Sealed, typeof(MulticastDelegate));
        type.SetCustomAttribute(new CustomAttributeBuilder(typeof(UnmanagedFunctionPointerAttribute).GetConstructor([typeof(CallingConvention)])!, [convention]));
        type.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName,
            CallingConventions.Standard, [typeof(object), typeof(nint)]).SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        type.DefineMethod("Invoke", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
            returnType, parameters).SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        return type.CreateType()!;
    }

    private static void BuildResourceInvoke(TypeBuilder type, Type delegateType, Type[] parameters, Type returnType, int phase,
        FieldBuilder gate, FieldBuilder original, FieldBuilder observer, FieldBuilder active)
    {
        var method = type.DefineMethod("Invoke", MethodAttributes.Public, returnType, parameters);
        var il = method.GetILGenerator();
        var copiedOriginal = il.DeclareLocal(delegateType); var copiedObserver = il.DeclareLocal(ResourceObserverType);
        var completion = il.DeclareLocal(typeof(Action<bool, byte>)); var returned = il.DeclareLocal(typeof(bool));
        var result = il.DeclareLocal(typeof(byte));
        var monitorEnter = typeof(Monitor).GetMethod(nameof(Monitor.Enter), [typeof(object)])!;
        var monitorExit = typeof(Monitor).GetMethod(nameof(Monitor.Exit), [typeof(object)])!;
        LoadField(gate); il.Emit(OpCodes.Call, monitorEnter);
        il.BeginExceptionBlock();
        AddActive(1); LoadField(original); il.Emit(OpCodes.Stloc, copiedOriginal); LoadField(observer); il.Emit(OpCodes.Stloc, copiedObserver);
        il.BeginFinallyBlock(); LoadField(gate); il.Emit(OpCodes.Call, monitorExit); il.EndExceptionBlock();

        il.BeginExceptionBlock();
        il.BeginExceptionBlock();
        var noObserver = il.DefineLabel(); il.Emit(OpCodes.Ldloc, copiedObserver); il.Emit(OpCodes.Brfalse, noObserver);
        il.Emit(OpCodes.Ldloc, copiedObserver); il.Emit(OpCodes.Ldc_I4, phase); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Callvirt, ResourceObserverType.GetMethod("Invoke")!); il.Emit(OpCodes.Stloc, completion); il.MarkLabel(noObserver);
        il.BeginCatchBlock(typeof(Exception)); il.Emit(OpCodes.Pop); il.EndExceptionBlock();

        il.BeginExceptionBlock();
        il.Emit(OpCodes.Ldloc, copiedOriginal); il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, delegateType.GetMethod("Invoke")!);
        if (returnType == typeof(byte)) il.Emit(OpCodes.Stloc, result);
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, returned);
        il.BeginFinallyBlock();
        il.BeginExceptionBlock();
        var noCompletion = il.DefineLabel(); il.Emit(OpCodes.Ldloc, completion); il.Emit(OpCodes.Brfalse, noCompletion);
        il.Emit(OpCodes.Ldloc, completion); il.Emit(OpCodes.Ldloc, returned); il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Callvirt, typeof(Action<bool, byte>).GetMethod("Invoke")!); il.MarkLabel(noCompletion);
        il.BeginCatchBlock(typeof(Exception)); il.Emit(OpCodes.Pop); il.EndExceptionBlock(); il.EndExceptionBlock();

        il.BeginFinallyBlock();
        LoadField(gate); il.Emit(OpCodes.Call, monitorEnter);
        il.BeginExceptionBlock(); AddActive(-1);
        il.BeginFinallyBlock(); LoadField(gate); il.Emit(OpCodes.Call, monitorExit); il.EndExceptionBlock();
        il.EndExceptionBlock();
        if (returnType == typeof(byte)) il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Ret);

        void LoadField(FieldBuilder field) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); }
        void AddActive(int delta)
        { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldfld, active); il.Emit(OpCodes.Ldc_I4, delta); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stfld, active); }
    }

    private static void BuildInvoke(TypeBuilder type, Type delegateType, Type[] parameters, int phase,
        FieldBuilder gate, FieldBuilder original, FieldBuilder observer, FieldBuilder active)
    {
        var method = type.DefineMethod("Invoke", MethodAttributes.Public, typeof(void), parameters);
        var il = method.GetILGenerator();
        var copiedOriginal = il.DeclareLocal(delegateType); var copiedObserver = il.DeclareLocal(ObserverType);
        var completion = il.DeclareLocal(typeof(Action<bool>)); var returned = il.DeclareLocal(typeof(bool));
        var monitorEnter = typeof(Monitor).GetMethod(nameof(Monitor.Enter), [typeof(object)])!;
        var monitorExit = typeof(Monitor).GetMethod(nameof(Monitor.Exit), [typeof(object)])!;
        LoadField(gate); il.Emit(OpCodes.Call, monitorEnter);
        il.BeginExceptionBlock();
        AddActive(1); LoadField(original); il.Emit(OpCodes.Stloc, copiedOriginal); LoadField(observer); il.Emit(OpCodes.Stloc, copiedObserver);
        il.BeginFinallyBlock(); LoadField(gate); il.Emit(OpCodes.Call, monitorExit); il.EndExceptionBlock();

        il.BeginExceptionBlock();
        // Observer failure is isolated from the original, including a failing
        // completion callback when Original itself throws.
        il.BeginExceptionBlock();
        var noObserver = il.DefineLabel(); il.Emit(OpCodes.Ldloc, copiedObserver); il.Emit(OpCodes.Brfalse, noObserver);
        il.Emit(OpCodes.Ldloc, copiedObserver); il.Emit(OpCodes.Ldc_I4, phase); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Conv_I);
        if (phase is 3 or 4 or 5) il.Emit(OpCodes.Ldarg_2); else il.Emit(OpCodes.Ldc_I4_0);
        if (phase == 1) il.Emit(OpCodes.Ldarg_2); else il.Emit(OpCodes.Ldc_R4, 0f);
        il.Emit(OpCodes.Callvirt, ObserverType.GetMethod("Invoke")!); il.Emit(OpCodes.Stloc, completion); il.MarkLabel(noObserver);
        il.BeginCatchBlock(typeof(Exception)); il.Emit(OpCodes.Pop); il.EndExceptionBlock();

        il.BeginExceptionBlock();
        il.Emit(OpCodes.Ldloc, copiedOriginal);
        for (var index = 0; index < parameters.Length; index++) il.Emit(OpCodes.Ldarg, index + 1);
        il.Emit(OpCodes.Callvirt, delegateType.GetMethod("Invoke")!); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stloc, returned);
        il.BeginFinallyBlock();
        il.BeginExceptionBlock();
        var noCompletion = il.DefineLabel(); il.Emit(OpCodes.Ldloc, completion); il.Emit(OpCodes.Brfalse, noCompletion);
        il.Emit(OpCodes.Ldloc, completion); il.Emit(OpCodes.Ldloc, returned); il.Emit(OpCodes.Callvirt, typeof(Action<bool>).GetMethod("Invoke")!); il.MarkLabel(noCompletion);
        il.BeginCatchBlock(typeof(Exception)); il.Emit(OpCodes.Pop); il.EndExceptionBlock(); il.EndExceptionBlock();

        il.BeginFinallyBlock();
        LoadField(gate); il.Emit(OpCodes.Call, monitorEnter);
        il.BeginExceptionBlock(); AddActive(-1);
        il.BeginFinallyBlock(); LoadField(gate); il.Emit(OpCodes.Call, monitorExit); il.EndExceptionBlock();
        il.EndExceptionBlock(); il.Emit(OpCodes.Ret);

        void LoadField(FieldBuilder field) { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); }
        void AddActive(int delta)
        { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldfld, active); il.Emit(OpCodes.Ldc_I4, delta); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stfld, active); }
    }
}
