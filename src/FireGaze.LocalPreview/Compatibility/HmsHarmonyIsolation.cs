using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;

namespace FireGaze.LocalPreview.Compatibility;

/// <summary>
/// Local workaround for the audited Harmony/MonoMod generic-member cache collision between
/// separate HMS plugin load contexts. Never changes the shared reflection cache or library.
/// </summary>
public static class HmsHarmonyIsolation
{
    private static readonly Lazy<RuntimeLayout> Layout = new(ValidateLayout);

    public static void ValidateRuntime() => _ = Layout.Value;

    public static IEnumerable<CodeInstruction> PreserveGenericMembers(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var result = instructions.Select(i => new CodeInstruction(i)).ToList();
        for(int index = 0; index < result.Count; index++)
        {
            var instruction = result[index];
            if(instruction.operand is not MemberInfo member || member.DeclaringType is not { IsGenericType: true } declaring
                || !declaring.GetGenericArguments().Any(t => RefersTo(t, original.Module.Assembly))) continue;
            // Moving these prefixes to a call would change their receiver/memory semantics.
            if(index > 0 && result[index-1].opcode.OpCodeType == OpCodeType.Prefix)
                throw new InvalidOperationException("Unsupported prefixed HMS generic member: " + member);
            var shim = CreateShim(instruction.opcode, member, declaring);
            instruction.opcode = OpCodes.Call;
            instruction.operand = shim;
        }
        return result;
    }

    private static bool RefersTo(Type type, Assembly assembly) => type.Assembly == assembly
        || (type.HasElementType && RefersTo(type.GetElementType()!, assembly))
        || (type.IsGenericType && type.GetGenericArguments().Any(t => RefersTo(t, assembly)));

    private static DynamicMethod CreateShim(OpCode opcode, MemberInfo member, Type declaring)
    {
        Type returns;
        var parameters = new List<Type>();
        var receiver = declaring.IsValueType ? declaring.MakeByRefType() : declaring;
        switch(member)
        {
            case ConstructorInfo ctor when opcode == OpCodes.Newobj || opcode == OpCodes.Call:
                returns = opcode == OpCodes.Newobj ? declaring : typeof(void);
                if(opcode == OpCodes.Call) parameters.Add(receiver);
                parameters.AddRange(ctor.GetParameters().Select(p => p.ParameterType));
                break;
            case MethodInfo method when opcode == OpCodes.Call || opcode == OpCodes.Callvirt:
                if(method.ContainsGenericParameters || (method.CallingConvention & CallingConventions.VarArgs) != 0)
                    throw new InvalidOperationException("Unsupported HMS generic method: " + method);
                returns = method.ReturnType;
                if(!method.IsStatic) parameters.Add(receiver);
                parameters.AddRange(method.GetParameters().Select(p => p.ParameterType));
                break;
            case FieldInfo field when opcode == OpCodes.Ldfld || opcode == OpCodes.Ldflda || opcode == OpCodes.Stfld
                || opcode == OpCodes.Ldsfld || opcode == OpCodes.Ldsflda || opcode == OpCodes.Stsfld:
                bool write = opcode == OpCodes.Stfld || opcode == OpCodes.Stsfld;
                bool address = opcode == OpCodes.Ldflda || opcode == OpCodes.Ldsflda;
                returns = write ? typeof(void) : address ? field.FieldType.MakeByRefType() : field.FieldType;
                if(!field.IsStatic) parameters.Add(receiver);
                if(write) parameters.Add(field.FieldType);
                break;
            default: throw new InvalidOperationException("Unsupported HMS generic member instruction: " + opcode + " " + member);
        }
        // Native Reflection.Emit uses the exact runtime MemberInfo. A MonoMod DynamicMethodDefinition
        // would pass through the same broken string cache that this boundary is meant to avoid.
        var shim = new DynamicMethod("FireGaze_HmsMember_" + Guid.NewGuid().ToString("N"), returns,
            parameters.ToArray(), typeof(HmsHarmonyIsolation).Module, true);
        var il = shim.GetILGenerator();
        for(int index = 0; index < parameters.Count; index++) il.Emit(OpCodes.Ldarg, (short)index);
        switch(member)
        {
            case ConstructorInfo ctor: il.Emit(opcode, ctor); break;
            case MethodInfo method: il.Emit(opcode, method); break;
            case FieldInfo field: il.Emit(opcode, field); break;
        }
        il.Emit(OpCodes.Ret);
        return shim;
    }

    public static void Detach(Harmony harmony, IEnumerable<MethodBase> tracked)
    {
        var layout = Layout.Value;
        List<Exception>? failures = null;
        // Harmony's normal mutation order is PatchProcessor.locker -> PatchTools.detours.
        lock(layout.Locker)
        {
            foreach(var method in tracked.Distinct())
            {
                if(Harmony.GetPatchInfo(method)?.Owners.Contains(harmony.Id) != true) continue;
                try
                {
                    harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
                    lock(layout.Detours)
                    {
                        // Unpatch rebuilds a no-patch wrapper; disposing only our now-unowned
                        // detour restores the real method for a later HMS call/unload.
                        if(Harmony.GetPatchInfo(method)?.Owners.Any() == true) continue;
                        var value = layout.Detours[method];
                        if(value == null) continue;
                        if(!layout.DetourType.IsInstanceOfType(value) || value is not IDisposable detour
                            || !Equals(layout.Source.GetValue(value), method)
                            || layout.Applied.GetValue(value) is not true
                            || layout.Target.GetValue(value) is not MethodInfo target
                            || !Equals(Harmony.GetOriginalMethod(target), method))
                            throw new InvalidOperationException("HMS Harmony detour ownership/layout changed: " + method);
                        detour.Dispose();
                        layout.Detours.Remove(method);
                    }
                }
                catch(Exception ex) { (failures ??= new()).Add(ex); }
            }
        }
        if(failures != null) throw new AggregateException("HMS compatibility detach was incomplete", failures);
    }

    private static RuntimeLayout ValidateLayout()
    {
        var library = typeof(Harmony).Assembly;
        if(library.GetName().Version != new Version(2,4,2,0)
            || library.ManifestModule.ModuleVersionId != new Guid("dc2e7251-4b84-4883-90eb-eb05a041522c")
            || string.IsNullOrEmpty(library.Location)
            || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(library.Location))) != "FD77B88724F4104440DF0CF979A851D35EEC75EA3A7E86297D04ABE47C71AFF6")
            throw new InvalidOperationException("HMS compatibility requires the audited Harmony runtime");
        var detour = library.GetType("MonoMod.Core.ICoreDetour",true)!;
        var detourBase = library.GetType("MonoMod.Core.ICoreDetourBase",true)!;
        var field = library.GetType("HarmonyLib.PatchTools",true)!.GetField("detours",BindingFlags.Static | BindingFlags.NonPublic)!;
        var locker = typeof(PatchProcessor).GetField("locker",BindingFlags.Static | BindingFlags.NonPublic)!;
        if(field.FieldType != typeof(Dictionary<,>).MakeGenericType(typeof(MethodBase),detour)
            || locker.FieldType != typeof(object) || !field.IsInitOnly || !locker.IsInitOnly
            || field.GetValue(null) is not IDictionary values || locker.GetValue(null) is not { } gate
            || detour.GetProperty("Source") is not { PropertyType: var sourceType } source || sourceType != typeof(MethodBase)
            || detour.GetProperty("Target") is not { PropertyType: var targetType } target || targetType != typeof(MethodBase)
            || detourBase.GetProperty("IsApplied") is not { PropertyType: var appliedType } applied || appliedType != typeof(bool))
            throw new InvalidOperationException("HMS Harmony detour runtime layout changed");
        return new(gate, values, detour, source, target, applied);
    }

    private sealed record RuntimeLayout(object Locker, IDictionary Detours, Type DetourType, PropertyInfo Source, PropertyInfo Target, PropertyInfo Applied);
}
