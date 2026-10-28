using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace FireGaze.UIText;

/// <summary>
/// A finite ImU8String label contract, shared by extraction and writing.
/// Recognizes prefix + (condition ? "*" : String.Empty) consumed exclusively
/// by the existing InputText overload. It does not admit arbitrary interpolation.
/// </summary>
internal static class UITextDynamicLabelContracts
{
    private const string BindingAssembly = "Dalamud.Bindings.ImGui";
    private const string BuilderType = BindingAssembly + ".ImU8String";
    private const string ImGuiType = BindingAssembly + ".ImGui";
    private const string CallbackType = ImGuiType + "/ImGuiInputTextCallbackDelegate";

    internal sealed record Contract(MethodDef Method, Instruction Label,
        Instruction RequiredMarker, Instruction OptionalEmpty, Instruction InputTextCall,
        string OriginalBase, bool IsPatched, Instruction? InsertedPop,
        Instruction? InsertedOptionalIdentity);

    public static IReadOnlyList<Contract> FindOriginal(MethodDef method) => Find(method, patched: false);
    public static IReadOnlyList<Contract> FindPatched(MethodDef method) => Find(method, patched: true);

    public static bool TryApply(Contract contract, string translated)
    {
        var current = FindOriginal(contract.Method).SingleOrDefault(candidate => ReferenceEquals(candidate.Label, contract.Label));
        var display = translated.Trim();
        if (current is null || current.OriginalBase != contract.OriginalBase || display.Length == 0
            || display.Contains("##", StringComparison.Ordinal)
            || display == current.OriginalBase || !CanInsertIdentity(current)) return false;

        // Each branch has the same complete-label policy as static labels:
        // translation*###original* or translation###original. This does not
        // claim equality with ImGui's numeric ID before the first translation.
        var body = current.Method.Body.Instructions;
        var emptyIndex = body.IndexOf(current.OptionalEmpty);
        var pop = Instruction.Create(OpCodes.Pop);
        var hidden = Instruction.Create(OpCodes.Ldstr, "###" + current.OriginalBase);
        current.Label.Operand = display;
        current.RequiredMarker.Operand = "*###" + current.OriginalBase + "*";
        body.Insert(emptyIndex + 1, pop);
        body.Insert(emptyIndex + 2, hidden);
        return true;
    }

    public static bool TryRevert(Contract contract, string expectedOriginal, string expectedTranslation)
    {
        var current = FindPatched(contract.Method).SingleOrDefault(candidate => ReferenceEquals(candidate.Label, contract.Label));
        if (current is null || current.OriginalBase != expectedOriginal
            || current.Label.Operand is not string display || display != expectedTranslation.Trim()
            || current.InsertedPop is null || current.InsertedOptionalIdentity is null) return false;

        // The original Empty field and all original control-flow targets remain
        // present. Only our two exact added instructions are removed; no field
        // scope, callback, argument or earlier condition is reconstructed.
        current.Label.Operand = expectedOriginal;
        current.RequiredMarker.Operand = "*";
        current.Method.Body.Instructions.Remove(current.InsertedOptionalIdentity);
        current.Method.Body.Instructions.Remove(current.InsertedPop);
        return true;
    }

    private static IReadOnlyList<Contract> Find(MethodDef method, bool patched)
    {
        var result = new List<Contract>();
        if (!method.HasBody) return result;
        var body = method.Body.Instructions;
        for (var index = 5; index + 14 < body.Count; index++)
        {
            var label = body[index];
            if (label.OpCode.Code != Code.Ldstr || label.Operand is not string prefix
                || prefix.Length == 0 || prefix.Contains("##", StringComparison.Ordinal)) continue;
            var builder = AddressedLocal(body[index - 1], method);
            if (builder is null || !IsNativeType(builder.Type, BuilderType)
                || AddressedLocal(body[index - 5], method) != builder
                || !body[index - 4].IsLdcI4() || body[index - 4].GetLdcI4Value() < 0
                || !body[index - 3].IsLdcI4() || body[index - 3].GetLdcI4Value() != 1
                || !IsConstructor(body[index - 2])
                || !IsAppendLiteral(body[index + 1])
                || AddressedLocal(body[index + 2], method) != builder) continue;

            // Keep condition evaluation exactly once. Its linear region must
            // independently leave one value above the existing builder address.
            var branchIndex = index + 3;
            var depth = 0;
            while (branchIndex < body.Count && branchIndex <= index + 19
                && body[branchIndex].OpCode.Code is not (Code.Brtrue or Code.Brtrue_S))
            {
                var instruction = body[branchIndex];
                if (!IsConditionInstruction(instruction)) break;
                instruction.CalculateStackUsage(out var pushes, out var pops);
                if (pops < 0 || pops > depth) break;
                depth += pushes - pops;
                branchIndex++;
            }
            if (branchIndex >= body.Count || depth != 1
                || body[branchIndex].OpCode.Code is not (Code.Brtrue or Code.Brtrue_S)) continue;

            var emptyIndex = branchIndex + 1;
            var extra = patched ? 2 : 0;
            var jumpIndex = emptyIndex + 1 + extra;
            var markerIndex = jumpIndex + 1;
            var formattedIndex = markerIndex + 1;
            var inputIndex = formattedIndex + 6;
            if (inputIndex >= body.Count || !IsStringEmpty(body[emptyIndex])
                || body[jumpIndex].OpCode.Code is not (Code.Br or Code.Br_S)
                || !ReferenceEquals(body[jumpIndex].Operand, body[formattedIndex])
                || !ReferenceEquals(body[branchIndex].Operand, body[markerIndex])
                || body[markerIndex].OpCode.Code != Code.Ldstr
                || body[markerIndex].Operand is not string marker
                || !IsAppendFormatted(body[formattedIndex])
                || LoadedLocal(body[formattedIndex + 1], method) != builder
                || !IsStringReferenceLoad(body[formattedIndex + 2], method)
                || !body[formattedIndex + 3].IsLdcI4()
                || !body[formattedIndex + 4].IsLdcI4()
                || !IsCallbackLoad(body[formattedIndex + 5], method)
                || !IsInputText(body[inputIndex])) continue;

            string original;
            Instruction? insertedPop = null;
            Instruction? insertedIdentity = null;
            if (patched)
            {
                if (body[emptyIndex + 1].OpCode.Code != Code.Pop
                    || body[emptyIndex + 2].OpCode.Code != Code.Ldstr
                    || body[emptyIndex + 2].Operand is not string hidden
                    || !hidden.StartsWith("###", StringComparison.Ordinal)) continue;
                original = hidden[3..];
                if (!IsOriginalBase(original) || marker != "*###" + original + "*") continue;
                insertedPop = body[emptyIndex + 1];
                insertedIdentity = body[emptyIndex + 2];
            }
            else
            {
                original = prefix;
                if (!IsOriginalBase(original) || marker != "*") continue;
            }

            // The local is constructed, appended twice and read exactly once.
            // This rules out aliases, copies, additional suffixes, other UI
            // consumers, opaque calls and IPC uses of the same builder value.
            var accesses = body.Where(instruction => instruction.GetLocal(method.Body.Variables) == builder).ToArray();
            if (accesses.Length != 4 || !accesses.Contains(body[index - 5])
                || !accesses.Contains(body[index - 1]) || !accesses.Contains(body[index + 2])
                || !accesses.Contains(body[formattedIndex + 1])
                || !HasClosedControlFlow(method, index - 5, inputIndex, branchIndex, markerIndex, jumpIndex, formattedIndex)) continue;

            result.Add(new Contract(method, label, body[markerIndex], body[emptyIndex], body[inputIndex],
                original, patched, insertedPop, insertedIdentity));
        }
        return result;
    }

    private static bool HasClosedControlFlow(MethodDef method, int start, int end,
        int conditional, int marker, int jump, int formatted)
    {
        var body = method.Body.Instructions;
        var internalTargets = body.Skip(start + 1).Take(end - start).ToHashSet();
        foreach (var instruction in body)
        {
            if (instruction.Operand is Instruction target && internalTargets.Contains(target))
            {
                if (ReferenceEquals(instruction, body[conditional]) && ReferenceEquals(target, body[marker])) continue;
                if (ReferenceEquals(instruction, body[jump]) && ReferenceEquals(target, body[formatted])) continue;
                return false;
            }
            if (instruction.Operand is IList<Instruction> targets && targets.Any(internalTargets.Contains)) return false;
        }
        foreach (var handler in method.Body.ExceptionHandlers)
        {
            if (new[] { handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd, handler.FilterStart }
                .Any(boundary => boundary is not null && internalTargets.Contains(boundary))) return false;
        }
        return true;
    }

    private static bool CanInsertIdentity(Contract contract)
    {
        // Adding Pop/Ldstr uses no extra stack depth. Preserve every branch
        // opcode as well; reject a shape whose existing short branch would
        // overflow instead of silently rewriting unrelated branch opcodes.
        var body = contract.Method.Body.Instructions;
        var offsets = new Dictionary<Instruction, int>();
        var offset = 0;
        foreach (var instruction in body)
        {
            offsets.Add(instruction, offset);
            offset += instruction.GetSize();
            if (ReferenceEquals(instruction, contract.OptionalEmpty)) offset += 6;
        }
        foreach (var instruction in body)
        {
            if (instruction.OpCode.OperandType != OperandType.ShortInlineBrTarget
                || instruction.Operand is not Instruction target) continue;
            var distance = offsets[target] - offsets[instruction] - instruction.GetSize();
            if (distance is < sbyte.MinValue or > sbyte.MaxValue) return false;
        }
        return true;
    }

    private static bool IsConditionInstruction(Instruction instruction) => instruction.OpCode.Code is
        Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3
        or Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3
        or Code.Ldfld or Code.Ldsfld or Code.Call or Code.Callvirt;

    private static bool IsOriginalBase(string text) => text.Length > 0
        && text == text.Trim() && !text.Contains("##", StringComparison.Ordinal);

    private static Local? AddressedLocal(Instruction instruction, MethodDef method) =>
        instruction.OpCode.Code is Code.Ldloca or Code.Ldloca_S ? instruction.GetLocal(method.Body.Variables) : null;

    private static Local? LoadedLocal(Instruction instruction, MethodDef method) =>
        instruction.OpCode.Code is Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3
            ? instruction.GetLocal(method.Body.Variables) : null;

    private static bool IsStringReferenceLoad(Instruction instruction, MethodDef method)
    {
        if (AddressedLocal(instruction, method) is { } local) return IsCoreType(local.Type, "System.String");
        return IsArgumentLoad(instruction) && instruction.GetParameter(method.Parameters)?.Type is ByRefSig reference
            && IsCoreType(reference.Next, "System.String");
    }

    private static bool IsCallbackLoad(Instruction instruction, MethodDef method) => instruction.OpCode.Code == Code.Ldnull
        || IsArgumentLoad(instruction) && instruction.GetParameter(method.Parameters) is { } argument
            && IsNativeType(argument.Type, CallbackType);

    private static bool IsArgumentLoad(Instruction instruction) => instruction.OpCode.Code is
        Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3;

    private static bool IsConstructor(Instruction instruction) => NativeCall(instruction, BuilderType, ".ctor") is { } call
        && call.MethodSig is { HasThis: true, GenParamCount: 0, Params.Count: 2 }
        && call.MethodSig.CallingConvention == CallingConvention.HasThis
        && IsCoreType(call.MethodSig.RetType, "System.Void")
        && call.MethodSig.Params.All(parameter => IsCoreType(parameter, "System.Int32"));

    private static bool IsAppendLiteral(Instruction instruction) => NativeCall(instruction, BuilderType, "AppendLiteral") is { } call
        && call.MethodSig is { HasThis: true, GenParamCount: 0, Params.Count: 1 }
        && call.MethodSig.CallingConvention == CallingConvention.HasThis
        && IsCoreType(call.MethodSig.RetType, "System.Void") && IsCoreType(call.MethodSig.Params[0], "System.String");

    private static bool IsAppendFormatted(Instruction instruction) => instruction.OpCode.Code == Code.Call
        && instruction.Operand is MethodSpec specification
        && IsNativeMethod(specification.Method, BuilderType, "AppendFormatted")
        && specification.GenericInstMethodSig is { GenericArguments.Count: 1 }
        && IsCoreType(specification.GenericInstMethodSig.GenericArguments[0], "System.String")
        && specification.Method.MethodSig is { HasThis: true, GenParamCount: 1, Params.Count: 1 } signature
        && signature.CallingConvention == (CallingConvention.HasThis | CallingConvention.Generic)
        && IsCoreType(signature.RetType, "System.Void") && signature.Params[0] is GenericMVar { Number: 0 };

    private static bool IsInputText(Instruction instruction) => NativeCall(instruction, ImGuiType, "InputText") is { } call
        && call.MethodSig is { HasThis: false, GenParamCount: 0, Params.Count: 5 } signature
        && signature.CallingConvention == CallingConvention.Default
        && IsCoreType(signature.RetType, "System.Boolean") && IsNativeType(signature.Params[0], BuilderType)
        && signature.Params[1] is ByRefSig reference && IsCoreType(reference.Next, "System.String")
        && IsCoreType(signature.Params[2], "System.Int32")
        && IsNativeType(signature.Params[3], BindingAssembly + ".ImGuiInputTextFlags")
        && IsNativeType(signature.Params[4], CallbackType);

    private static IMethod? NativeCall(Instruction instruction, string type, string name) =>
        instruction.OpCode.Code == Code.Call && instruction.Operand is IMethod call
            && call is not MethodSpec && IsNativeMethod(call, type, name) ? call : null;

    private static bool IsNativeMethod(IMethod call, string type, string name) => call.Name == name
        && call.DeclaringType is TypeRef reference && reference.FullName == type
        && reference.DefinitionAssembly?.Name == BindingAssembly;

    private static bool IsNativeType(TypeSig signature, string name) => signature.FullName == name
        && signature.ToTypeDefOrRef() is TypeRef reference && reference.DefinitionAssembly?.Name == BindingAssembly;

    private static bool IsCoreType(TypeSig signature, string name) => signature.FullName == name
        && signature.DefinitionAssembly?.Name?.String is "System.Runtime" or "System.Private.CoreLib" or "mscorlib";

    private static bool IsStringEmpty(Instruction instruction) => instruction.OpCode.Code == Code.Ldsfld
        && instruction.Operand is IField field && field.Name == "Empty"
        && field.DeclaringType is TypeRef reference && reference.FullName == "System.String"
        && reference.DefinitionAssembly?.Name?.String is "System.Runtime" or "System.Private.CoreLib" or "mscorlib"
        && field.FieldSig is { } signature && IsCoreType(signature.Type, "System.String");
}
