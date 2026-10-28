using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace FireGaze.UIText;

// This describes an offline write. It grants neither write permission nor a
// retirement/load receipt. Unknown is deliberately the conservative default.
internal enum UITextIdentityEffect
{
    Unknown,
    DisplayOnly,
    NumericIdPreserved,
    NumericIdChanged,
}

internal sealed record UITextIdentitySite(string Location, string Original, string Replacement,
    UITextIdentityEffect Effect, string ConsumerEvidence);

internal static class UITextIdentityEffects
{
    public static UITextIdentitySite Literal(MethodDef method, Instruction instruction,
        UIStringExtractor.LiteralSite site, string original, string replacement)
    {
        var location = $"{method.FullName} IL_{instruction.Offset:X4}";
        if (site.PreserveID)
        {
            // Only an existing complete ### tail proves equality with the
            // original numeric ID, and only under the same seed/ID stack.
            // Adding ###source to a plain label changes its first numeric ID.
            var separator = original.IndexOf("###", StringComparison.Ordinal);
            var replacementSeparator = replacement.IndexOf("###", StringComparison.Ordinal);
            var stable = separator >= 0 && replacementSeparator >= 0
                && string.Equals(original[separator..], replacement[replacementSeparator..], StringComparison.Ordinal);
            return new(location, original, replacement, stable ? UITextIdentityEffect.NumericIdPreserved
                : UITextIdentityEffect.NumericIdChanged, site.Reason);
        }

        if (TryDirectDisplay(method, instruction, out var consumer))
            return new(location, original, replacement, UITextIdentityEffect.DisplayOnly, consumer);

        return new(location, original, replacement, UITextIdentityEffect.Unknown,
            "当前可写消费者尚无纯展示或 numeric ID 等值证明：" + site.Reason);
    }

    // Keep the exemption finite. A wrapper named DrawLabel/Text, a stored
    // value, or an unsupported branch is not proof of a display-only consumer.
    private static bool TryDirectDisplay(MethodDef method, Instruction producer, out string consumer)
    {
        consumer = string.Empty;
        var body = method.Body.Instructions;
        var start = body.IndexOf(producer);
        var targets = body.SelectMany(instruction => instruction.Operand switch
        {
            Instruction target => new[] { target },
            IList<Instruction> many => many,
            _ => Array.Empty<Instruction>(),
        }).ToHashSet();
        foreach (var handler in method.Body.ExceptionHandlers)
            foreach (var boundary in new[] { handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd, handler.FilterStart })
                if (boundary is not null) targets.Add(boundary);

        // Values below the producer are unknown but cannot contain this
        // producer. Crossing control-flow, storage or duplication is rejected.
        var stack = new List<bool> { true };
        for (var index = start + 1; index < body.Count && index <= start + 64; index++)
        {
            var instruction = body[index];
            if (targets.Contains(instruction) || instruction.OpCode.FlowControl is
                FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw) return false;
            instruction.CalculateStackUsage(out var pushes, out var pops);
            if (pops < 0 || pushes < 0) return false;
            var values = new bool[pops];
            for (var value = pops - 1; value >= 0; value--)
            {
                if (stack.Count == 0) continue;
                values[value] = stack[^1];
                stack.RemoveAt(stack.Count - 1);
            }
            if (values.Any(value => value))
            {
                if (instruction.OpCode.Code != Code.Call || instruction.Operand is not IMethod call
                    || call is MethodSpec || call.MethodSig is not { HasThis: false, GenParamCount: 0 } signature
                    || signature.CallingConvention != CallingConvention.Default) return false;
                if (IsNativeConversion(call))
                {
                    stack.Add(true);
                    continue;
                }
                if (stack.Any(value => value) || !IsNativeDisplay(call)) return false;
                for (var argument = 0; argument < values.Length; argument++)
                    if (values[argument] && (argument >= signature.Params.Count
                        || signature.Params[argument].FullName is not ("System.String" or "Dalamud.Bindings.ImGui.ImU8String"))) return false;
                consumer = call.FullName;
                return true;
            }
            for (var value = 0; value < pushes; value++) stack.Add(false);
        }
        return false;
    }

    private static bool IsNativeDisplay(IMethod method) =>
        method.DeclaringType.FullName is "ImGuiNET.ImGui" or "Dalamud.Bindings.ImGui.ImGui"
        && method.DeclaringType.DefinitionAssembly?.Name == (method.DeclaringType.FullName == "ImGuiNET.ImGui"
            ? "ImGui.NET" : "Dalamud.Bindings.ImGui")
        && method.MethodSig.RetType.FullName == "System.Void"
        && method.Name.String is "Text" or "TextUnformatted" or "TextWrapped" or "TextDisabled"
            or "TextColored" or "BulletText" or "SetTooltip" or "SetItemTooltip" or "SetTooltipUnformatted";

    private static bool IsNativeConversion(IMethod method) => method.Name == "op_Implicit"
        && method.DeclaringType.FullName == "Dalamud.Bindings.ImGui.ImU8String"
        && method.DeclaringType.DefinitionAssembly?.Name == "Dalamud.Bindings.ImGui"
        && method.MethodSig.Params.Count == 1 && method.MethodSig.Params[0].FullName == "System.String"
        && method.MethodSig.RetType.FullName == "Dalamud.Bindings.ImGui.ImU8String"
        && method.MethodSig.RetType.ToTypeDefOrRef()?.DefinitionAssembly?.Name == "Dalamud.Bindings.ImGui";
}
