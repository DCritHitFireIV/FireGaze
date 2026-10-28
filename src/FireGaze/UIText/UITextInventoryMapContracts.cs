using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace FireGaze.UIText;

/// <summary>
/// Finite metadata proof for the CN InventoryTools 1.15.0.5 null-name fallback.
/// The source archive SHA256 is 95A95F8C8BC93060D0EECADA1A19A3FB1F8582C09377171EA9378BD4BB8FC39F.
/// This admits one producer, not a general record, getter or equality rule.
/// </summary>
internal static class UITextInventoryMapContracts
{
    private const string Entry = "InventoryTools.Compendium.Models.MapLinkEntry";
    private const string Producer = "InventoryTools.Compendium.Types.SatisfactionNpcCompendiumType";
    private const string Builder = "InventoryTools.Compendium.Services.CompendiumViewBuilder";
    private const string View = "InventoryTools.Compendium.Sections.MapLinkViewSection";
    private const string Views = "InventoryTools.Compendium.Sections.MapLinksViewSection";
    private const string Menu = "InventoryTools.Compendium.MenuProviders.MapLinkEntryMenuProvider";
    private const string ReferenceShape = "A2A12B6783162CE4B5AF5286A88CB6F816DDD63E697CBF6DAE31E056455AC8AF";

    // These fingerprints include method signatures with assembly scopes, all
    // instructions, local types, branches, EH, type/field/method flags and every
    // member of each listed type. Only audited display Ldstr operands are masked.
    // The closed reference fingerprint additionally binds every module-wide
    // declaration and complete method body referencing these model/view/menu
    // types through its signature, locals, EH or operands. Later object calls
    // inside an existing reference caller cannot escape this fingerprint.
    private static readonly (string Type, string Shape)[] TypeShapes =
    [
        (Entry, "4B388AE17F95B76461B25EA7D6E108FEAD4D1FC7B0CBE560AA03002BE414F4A2"),
        ("InventoryTools.Compendium.Sections.Options.MapLinkViewSectionOptions", "401DB1435D9DEBF31FF8D8F591E41E05DDD491D564D676CC3FCC21E4845D8A25"),
        ("InventoryTools.Compendium.Sections.Options.MapLinksViewSectionOptions", "C1398C3BF508C5B19D953F104824205F7B9303D9685F07AEC6FF5D11E5C0DE75"),
        (View, "3732A02B4FC13715597DB0CC8D2F9FAD4B90D3BDFF430638AD580657D0669F5F"),
        (Views, "30B185FE902AED736569DF9FCA1C84EEDC0AC36A7AC3F0CCE0D5178B0412C772"),
        (Menu, "692B002F9EE9D96253B8BD50588598D12CCB21989C7B29C6B805323A98057EB0"),
        ("InventoryTools.Compendium.Models.MenuProvider`1", "521A3CAA5ACDB9E4F1B32E7223C48744D96DD5EA2278FEA0CCCF4EE39143939C"),
        ("InventoryTools.Compendium.Interfaces.IMenuProvider`1", "65B01B19551A86137EE649EE884A645E98DCA267AF970C15744F81A7FE55417B"),
    ];

    private static readonly (string Type, string Method, string Shape)[] MethodShapes =
    [
        (Producer, "BuildViewFields", "8466D4CFCAB941B8177C792B2CC7BF10E0B4EA28018E02A5B826A3B350FC2D8B"),
        (Builder, ".ctor", "0DBD86ECBA10959D4F6000429705EE9DAD3A531055E56C2AA707F0162D21B870"),
        (Builder, "AddMapLinkSectionSection", "204CE092A597722ACFC4EA86F448E163A44B5A9001ABD374C83FC274BFDE1626"),
    ];

    internal sealed record Contract(MethodDef Method, Instruction Label, string Reason);

    public static IReadOnlyList<Contract> Find(ModuleDefMD module)
    {
        if (module.Assembly?.FullName != "InventoryTools, Version=1.15.0.5, Culture=neutral, PublicKeyToken=null"
            || module.Mvid != Guid.Parse("b90e93f1-84e1-4185-8bf1-6b45c1113d9a")) return [];

        var types = module.GetTypes().ToDictionary(type => type.FullName, StringComparer.Ordinal);
        foreach (var (name, expected) in TypeShapes)
            if (!types.TryGetValue(name, out var type) || HashType(type) != expected) return [];

        MethodDef? producer = null;
        foreach (var (owner, name, expected) in MethodShapes)
        {
            if (!types.TryGetValue(owner, out var type)) return [];
            var methods = type.Methods.Where(method => method.Name == name).ToArray();
            if (methods.Length != 1 || HashMethod(methods[0]) != expected) return [];
            if (owner == Producer) producer = methods[0];
        }
        if (HashReferences(module) != ReferenceShape || producer is null) return [];

        var label = producer.Body.Instructions[231];
        if (label.OpCode.Code != Code.Ldstr || label.Operand is not string text || text.Length == 0) return [];

        // Pinned bodies prove:
        // GetName(row) ?? label -> MapLinkEntry ctor's Name argument, independently
        // of Subtitle and Location -> options.MapLink -> view factory -> DrawSection.
        // get_Name is used by CalcTextSize and TextUnformatted; DrawMenu uses Text.
        // Open/Draw use separate PopupName, while map/teleport messages read Location.
        // The only external record equality call compares with null. Its generated
        // hashing/equality bodies remain unchanged and gain no general exemption.
        return [new Contract(producer, label,
            "→ 已核对的 InventoryTools CN MapLinkEntry.Name 显示链（位置、菜单身份与消息参数保持独立）")];
    }

    private static bool MaskDisplay(MethodDef method, int index) => method.Name.String switch
    {
        "BuildViewFields" when method.DeclaringType.FullName == Producer =>
            index is 14 or 18 or 32 or 92 or 192 or 198 or 231 or 241 or 255,
        "BuildViewFields" when method.DeclaringType.FullName == "InventoryTools.Compendium.Types.ENpcCompendiumType" =>
            index is 38 or 73,
        "BuildViewFields" when method.DeclaringType.FullName == "InventoryTools.Compendium.Types.LeveCompendiumType" =>
            index is 23 or 37 or 70 or 80 or 95 or 111 or 127 or 149 or 180 or 218 or 291 or 481,
        "BuildViewFields" when method.DeclaringType.FullName == "InventoryTools.Compendium.Types.QuestCompendiumType" =>
            index is 20 or 45 or 46 or 74 or 105 or 123 or 138 or 153 or 217 or 233 or 283 or 370,
        "DrawSection" when method.DeclaringType.FullName == View => index is 158 or 172,
        "DrawSection" when method.DeclaringType.FullName == Views => index is 170 or 184,
        "DrawMenu" when method.DeclaringType.FullName == Menu => index is 9 or 24,
        _ => false,
    };

    private static string? HashType(TypeDef type)
    {
        var shape = new StringBuilder();
        Part(shape, TypeReference(type));
        Part(shape, Number((int)type.Attributes));
        Part(shape, type.BaseType is null ? null : TypeReference(type.BaseType));
        foreach (var implementation in type.Interfaces) Part(shape, TypeReference(implementation.Interface));
        foreach (var field in type.Fields)
        {
            if (field.FieldSig?.Type is null) return null;
            Part(shape, field.Name);
            Part(shape, Number((int)field.Attributes));
            Part(shape, TypeSignature(field.FieldSig.Type));
        }
        foreach (var method in type.Methods)
            if (!AppendMethod(shape, method)) return null;
        return HasInvalidMetadata(shape) ? null : Hash(shape);
    }

    private static string? HashMethod(MethodDef method)
    {
        var shape = new StringBuilder();
        return AppendMethod(shape, method) && !HasInvalidMetadata(shape) ? Hash(shape) : null;
    }

    private static bool AppendMethod(StringBuilder shape, MethodDef method)
    {
        if (method.MethodSig is not { RetType: not null } signature || signature.Params.Any(parameter => parameter is null)) return false;
        Part(shape, MethodReference(method));
        Part(shape, Number((int)method.Attributes));
        Part(shape, Number((int)method.ImplAttributes));
        Part(shape, Number((int)signature.CallingConvention));
        Part(shape, Number((int)signature.GenParamCount));
        Part(shape, TypeSignature(signature.RetType));
        foreach (var parameter in signature.Params) Part(shape, TypeSignature(parameter));
        if (!method.HasBody) { Part(shape, "no-body"); return true; }
        var body = method.Body;
        Part(shape, body.InitLocals.ToString());
        foreach (var local in body.Variables)
        {
            if (local.Type is null) return false;
            Part(shape, TypeSignature(local.Type));
        }
        Part(shape, "instructions");
        for (var index = 0; index < body.Instructions.Count; index++)
        {
            var instruction = body.Instructions[index];
            if (!TryOperand(method, instruction, index, out var operand)) return false;
            Part(shape, instruction.OpCode.Code.ToString());
            Part(shape, operand);
        }
        foreach (var handler in body.ExceptionHandlers)
        {
            Part(shape, handler.HandlerType.ToString());
            foreach (var instruction in new[] { handler.TryStart, handler.TryEnd, handler.HandlerStart, handler.HandlerEnd, handler.FilterStart })
                Part(shape, Number(body.Instructions.IndexOf(instruction)));
            Part(shape, handler.CatchType is null ? null : TypeReference(handler.CatchType));
        }
        return true;
    }

    private static bool TryOperand(MethodDef method, Instruction instruction, int index, out string value)
    {
        value = string.Empty;
        if (instruction.OpCode.Code == Code.Ldstr)
        {
            if (instruction.Operand is not string text) return false;
            value = MaskDisplay(method, index) ? "<display>" : text;
            return true;
        }
        switch (instruction.Operand)
        {
            case null:
                return instruction.OpCode.OperandType == OperandType.InlineNone;
            case Instruction branch:
                var branchIndex = method.Body.Instructions.IndexOf(branch);
                if (branchIndex < 0) return false;
                value = "@" + Number(branchIndex);
                return true;
            case IList<Instruction> branches:
                var targets = branches.Select(branch => method.Body.Instructions.IndexOf(branch)).ToArray();
                if (targets.Any(target => target < 0)) return false;
                value = "@[" + string.Join(",", targets.Select(Number)) + "]";
                return true;
            // MemberRef implements both IField and IMethod. Select its actual
            // non-null signature before deciding which member it represents.
            case IField field when field.FieldSig is { Type: not null } && field.DeclaringType is not null:
                value = "F|" + FieldReference(field);
                return true;
            case IMethod called when called.MethodSig is { RetType: not null } signature && called.DeclaringType is not null:
                if (signature.Params.Any(parameter => parameter is null)) return false;
                value = "M|" + MethodReference(called);
                return true;
            case IMethod or IField:
                return false;
            case ITypeDefOrRef type:
                value = "T|" + TypeReference(type);
                return true;
            case Local local when local.Type is not null:
                value = "L|" + Number(local.Index) + "|" + TypeSignature(local.Type);
                return true;
            case Parameter parameter when parameter.Type is not null:
                value = "P|" + Number(parameter.Index) + "|" + TypeSignature(parameter.Type);
                return true;
            case float floatValue:
                value = floatValue.ToString("R", CultureInfo.InvariantCulture);
                return true;
            case double doubleValue:
                value = doubleValue.ToString("R", CultureInfo.InvariantCulture);
                return true;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                value = ((IFormattable)instruction.Operand).ToString(null, CultureInfo.InvariantCulture);
                return true;
            default:
                return false;
        }
    }

    private static string? HashReferences(ModuleDefMD module)
    {
        var shape = new StringBuilder();
        foreach (var type in module.GetTypes())
        {
            if (Touches(type.FullName) || Touches(type.BaseType is null ? null : TypeReference(type.BaseType)))
                Part(shape, "TYPE|" + TypeReference(type) + "|" + Number((int)type.Attributes) + "|" + (type.BaseType is null ? null : TypeReference(type.BaseType)));
            foreach (var implementation in type.Interfaces)
                if (Touches(TypeReference(implementation.Interface))) Part(shape, "INTERFACE|" + TypeReference(type) + "|" + TypeReference(implementation.Interface));
            foreach (var field in type.Fields)
                if (Touches(FieldReference(field))) Part(shape, "FIELD|" + FieldReference(field) + "|" + Number((int)field.Attributes));
            foreach (var method in type.Methods)
            {
                if (!TouchesMethod(method)) continue;
                Part(shape, "METHOD");
                if (!AppendMethod(shape, method)) return null;
            }
        }
        return HasInvalidMetadata(shape) ? null : Hash(shape);
    }

    private static bool TouchesMethod(MethodDef method) => Touches(MethodReference(method))
        || method.HasBody && (method.Body.Variables.Any(local => Touches(TypeSignature(local.Type)))
            || method.Body.ExceptionHandlers.Any(handler => Touches(handler.CatchType is null ? null : TypeReference(handler.CatchType)))
            || method.Body.Instructions.Any(instruction => Touches(instruction.Operand switch
            {
                IField field when field.FieldSig is not null => FieldReference(field),
                IMethod called when called.MethodSig is not null => MethodReference(called),
                ITypeDefOrRef referenced => TypeReference(referenced),
                _ => null,
            })));

    // Read metadata identity directly. dnlib's FullName/AQN for GenericVar may
    // resolve its owner's generic parameter and change !0 into a name such as
    // T or TExtendedRow. A resolver must not change this proof's source shape.
    private const string InvalidMetadata = "<invalid-metadata>";

    private static string TypeSignature(TypeSig? type, int depth = 0)
    {
        if (type is null || depth > 64) return InvalidMetadata;
        string Nested(TypeSig? nested) => TypeSignature(nested, depth + 1);
        var code = Number((int)type.ElementType);
        return type switch
        {
            GenericVar generic => "var:" + generic.Number.ToString(CultureInfo.InvariantCulture),
            GenericMVar generic => "mvar:" + generic.Number.ToString(CultureInfo.InvariantCulture),
            TypeDefOrRefSig reference => code + ":" + TypeReference(reference.TypeDefOrRef, depth + 1),
            GenericInstSig generic => code + ":" + Nested(generic.GenericType) + "<" + string.Join(";", generic.GenericArguments.Select(Nested)) + ">",
            ArraySig array => code + ":" + Nested(array.Next) + ":" + array.Rank.ToString(CultureInfo.InvariantCulture)
                + ":" + string.Join(",", array.Sizes) + ":" + string.Join(",", array.LowerBounds),
            ModifierSig modifier => code + ":" + TypeReference(modifier.Modifier, depth + 1) + ":" + Nested(modifier.Next),
            FnPtrSig function => code + ":" + CallingSignature(function.MethodSig, depth + 1),
            ValueArraySig array => code + ":" + array.Size.ToString(CultureInfo.InvariantCulture) + ":" + Nested(array.Next),
            ModuleSig module => code + ":" + module.Index.ToString(CultureInfo.InvariantCulture) + ":" + Nested(module.Next),
            SentinelSig => code,
            NonLeafSig wrapper => code + ":" + Nested(wrapper.Next),
            _ => InvalidMetadata,
        };
    }

    private static string TypeReference(ITypeDefOrRef? type, int depth = 0)
    {
        if (type is null || depth > 64) return InvalidMetadata;
        return type switch
        {
            TypeSpec specification => "spec:" + TypeSignature(specification.TypeSig, depth + 1),
            TypeRef reference => "ref:" + reference.Namespace + "." + reference.Name + "@" + Scope(reference.ResolutionScope, depth + 1),
            TypeDef definition when definition.DeclaringType is not null => "def:" + TypeReference(definition.DeclaringType, depth + 1) + "/" + definition.Name,
            TypeDef definition => "def:" + definition.Namespace + "." + definition.Name + "@" + Scope(definition.Module, depth + 1),
            _ => InvalidMetadata,
        };
    }

    private static string Scope(object? scope, int depth)
    {
        if (depth > 64) return InvalidMetadata;
        return scope switch
        {
            AssemblyRef assembly => "assembly:" + assembly.FullName,
            TypeRef parent => TypeReference(parent, depth + 1),
            ModuleRef module => "module-ref:" + module.Name,
            ModuleDef module when module.Assembly is not null => "module:" + module.Name + "@" + module.Assembly.FullName,
            _ => InvalidMetadata,
        };
    }

    private static string CallingSignature(MethodSig? signature, int depth = 0)
    {
        if (signature is null || depth > 64) return InvalidMetadata;
        return Number((int)signature.CallingConvention) + ":" + signature.GenParamCount.ToString(CultureInfo.InvariantCulture)
            + ":" + TypeSignature(signature.RetType, depth + 1)
            + "(" + string.Join(";", signature.Params.Select(parameter => TypeSignature(parameter, depth + 1))) + ")"
            + "sentinel(" + (signature.ParamsAfterSentinel is null ? "" : string.Join(";", signature.ParamsAfterSentinel.Select(parameter => TypeSignature(parameter, depth + 1)))) + ")";
    }

    private static string MethodReference(IMethod method, int depth = 0)
    {
        if (depth > 64) return InvalidMetadata;
        if (method is MethodSpec specification)
            return "method-spec:" + (specification.Method is null ? InvalidMetadata : MethodReference(specification.Method, depth + 1))
                + "<" + (specification.GenericInstMethodSig is null ? InvalidMetadata
                    : string.Join(";", specification.GenericInstMethodSig.GenericArguments.Select(argument => TypeSignature(argument, depth + 1)))) + ">";
        return TypeReference(method.DeclaringType, depth + 1) + "::" + method.Name + "|" + CallingSignature(method.MethodSig, depth + 1);
    }

    private static string FieldReference(IField field) => TypeReference(field.DeclaringType) + "::" + field.Name + "|" + TypeSignature(field.FieldSig?.Type);
    private static bool HasInvalidMetadata(StringBuilder shape) => shape.ToString().Contains(InvalidMetadata, StringComparison.Ordinal);

    private static bool Touches(string? value) => value is not null
        && TypeShapes.Any(item => value.Contains(item.Type, StringComparison.Ordinal));

    private static void Part(StringBuilder target, string? value)
    {
        value ??= string.Empty;
        target.Append(Number(value.Length)).Append(':').Append(value).Append('\n');
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Hash(StringBuilder value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
}
