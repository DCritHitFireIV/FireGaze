using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;

internal static unsafe class ShutdownPassThroughTests
{
    internal static int Calls;
    public static void Original() => Calls++;
    internal static IEnumerable<(string Name, System.Action Run)> Cases(GameDataFixture data)
    {
        foreach (var closedField in new[] { "disposed", "nativeClosing" })
        foreach (var (field, method) in new[] {
            ("useAction", "UseActionDetour"), ("useLocation", "UseLocationDetour"), ("useCompanion", "UseCompanionDetour"),
            ("actionStatus", "ActionStatusDetour"), ("canEmote", "CanEmoteDetour"), ("canAgentEmote", "CanAgentEmoteDetour"),
            ("executeEmote", "ExecuteEmoteDetour"), ("executeAgentEmote", "ExecuteAgentEmoteDetour"), ("playEmote", "PlayEmoteDetour"),
            ("mountSetup", "MountSetupDetour"), ("weaponVisibility", "WeaponVisibilityDetour"), ("extraWeaponVisibility", "ExtraWeaponVisibilityDetour"),
            ("characterUpdate", "CharacterUpdateDetour"), ("characterDisable", "CharacterDisableDetour"), ("companionSetup", "CompanionSetupDetour"),
            ("ornamentSetup", "OrnamentSetupDetour"), ("timelinePlay", "TimelinePlayDetour"), ("actionTimelinePlay", "ActionTimelinePlayDetour"),
            ("resolvedTimelinePlay", "ResolvedTimelinePlayDetour"), ("resolvedActionTimelinePlay", "ResolvedActionTimelinePlayDetour") })
        {
            var hookField = field; var detourMethod = method;
            var closing = closedField;
            yield return ($"{closing} {method} only forwards Original after services are revoked", () => {
                using var fixture = new NativeFixture(data);
                NativeFixture.Set(fixture.Runtime, closing, true);
                NativeFixture.Set(fixture.Runtime, "framework", Unavailable<IFramework>());
                NativeFixture.Set(fixture.Runtime, "objects", Unavailable<IObjectTable>());
                var fieldInfo = NativeFixture.RuntimeType.GetField(hookField, NativeFixture.All)!;
                var delegateType = fieldInfo.FieldType.GetGenericArguments()[0];
                var signature = delegateType.GetMethod("Invoke")!;
                var original = new DynamicMethod("StoppedOriginal", signature.ReturnType, signature.GetParameters().Select(p => p.ParameterType).ToArray(), typeof(ShutdownPassThroughTests).Module, true);
                var il = original.GetILGenerator();
                il.Emit(OpCodes.Call, typeof(ShutdownPassThroughTests).GetMethod(nameof(Original))!);
                if (signature.ReturnType != typeof(void)) il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Ret);
                var backend = typeof(Dalamud.Hooking.Hook<>).Assembly.GetType("Dalamud.Hooking.Internal.FunctionPointerVariableHook`1")!.MakeGenericType(delegateType);
                var hook = RuntimeHelpers.GetUninitializedObject(backend);
                NativeFixture.Set(hook, "originalDelegate", original.CreateDelegate(delegateType));
                fieldInfo.SetValue(fixture.Runtime, hook);
                if (hookField.StartsWith("resolved", StringComparison.Ordinal))
                    NativeFixture.Set(fixture.Runtime, hookField == "resolvedTimelinePlay" ? "resolvedTimelineAdmitted" : "resolvedActionTimelineAdmitted", false);
                var detour = NativeFixture.RuntimeType.GetMethod(detourMethod, NativeFixture.All)!;
                var arguments = detour.GetParameters().Select(parameter => Argument(parameter.ParameterType)).ToArray();
                Calls = 0;
                var result = detour.Invoke(fixture.Runtime, arguments);
                if (Calls != 1 || fixture.Accepted != 0 || fixture.ExternalEmotes != 0) throw new InvalidOperationException("closing callback must forward exactly once with no accepted-input or history mutation");
                if (detour.ReturnType == typeof(bool) && result is not true) throw new InvalidOperationException("original Boolean result was lost");
                if (detour.ReturnType == typeof(uint) && !Equals(result, 1u)) throw new InvalidOperationException("original status result was lost");
            });
        }
    }
    private static object Argument(Type type) => type.IsPointer ? Pointer.Box(null, type)
        : type == typeof(bool) ? false : type.IsEnum ? Enum.ToObject(type, 1) : Convert.ChangeType(1, type);
    private static T Unavailable<T>() where T : class
    {
        var proxy = DispatchProxy.Create<T, ServiceProxy>();
        ((ServiceProxy)(object)proxy).Call = (_, _) => throw new InvalidOperationException("revoked service must never be consulted");
        return proxy;
    }
}
