using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace FireGaze.UIText;

internal static class UITextTeleporterSearchContracts
{
    private const string AssemblyName = "TeleporterPlugin";
    private const string AssemblyFullName = "TeleporterPlugin, Version=2.0.3.0, Culture=neutral, PublicKeyToken=null";
    private const string SourceMvid = "78c38b36-d11c-44ee-bb97-33bd6141668d";
    private const string Original = "Search##aetheryteFilter";
    private const string VisibleText = "Search";
    private const string MethodName = "System.Void TeleporterPlugin.Gui.ConfigWindow::DrawAliasTab()";
    private const string FilterField = "System.String TeleporterPlugin.Gui.ConfigWindow::m_AetheryteFilter";
    private const string BindingAssembly = "Dalamud.Bindings.ImGui";
    private const string ImGui = BindingAssembly + ".ImGui";
    private const string ImU8 = BindingAssembly + ".ImU8String";

    public static IReadOnlyDictionary<Instruction, string> FindOriginal(ModuleDef module)
    {
        if (module.Assembly?.FullName != AssemblyFullName || module.Mvid != Guid.Parse(SourceMvid))
            return new Dictionary<Instruction, string>();

        var method = module.GetTypes().SelectMany(type => type.Methods)
            .SingleOrDefault(candidate => candidate.FullName == MethodName);
        if (method is null || !method.HasBody) return new Dictionary<Instruction, string>();
        if (module.GetTypes().SelectMany(type => type.Methods).Where(candidate => candidate.HasBody)
            .SelectMany(candidate => candidate.Body.Instructions)
            .Count(instruction => instruction.OpCode.Code == Code.Ldstr && Equals(instruction.Operand, Original)) != 1)
            return new Dictionary<Instruction, string>();
        var il = method.Body.Instructions;
        if (il.Count <= 157 || il[133].OpCode.Code != Code.Ldstr || !Equals(il[133].Operand, Original)
            || !IsCall(il[134], BindingAssembly, ImU8, "op_Implicit", hasThis: false, ImU8, "System.String")
            || !IsField(il[135], Code.Ldsflda, FilterField)
            || !IsInt(il[136], 512) || !IsInt(il[137], 0) || il[138].OpCode.Code != Code.Ldnull
            || !IsCall(il[139], BindingAssembly, ImGui, "InputText", hasThis: false, "System.Boolean",
                ImU8, "System.String&", "System.Int32", "Dalamud.Bindings.ImGui.ImGuiInputTextFlags",
                ImGui + "/ImGuiInputTextCallbackDelegate")
            || il[140].OpCode.Code != Code.Pop
            || !IsField(il[151], Code.Ldsfld, FilterField) || !IsField(il[155], Code.Ldsfld, FilterField)
            || !IsCall(il[152], "System.Runtime", "System.String", "IsNullOrEmpty", hasThis: false,
                "System.Boolean", "System.String")
            || !IsInt(il[156], 5)
            || !IsCall(il[157], "System.Runtime", "System.String", "Contains", hasThis: true,
                "System.Boolean", "System.String", "System.StringComparison")
            || !ReferenceEquals(il[150].Operand, il[154].Operand)
            || !ReferenceEquals(il[153].Operand, il[159]) || il[158].OpCode.Code != Code.Brfalse_S)
            return new Dictionary<Instruction, string>();

        return new Dictionary<Instruction, string> { [il[133]] = VisibleText };
    }

    public static bool TryApply(ModuleDef module, Instruction label, string translated)
    {
        var current = FindOriginal(module);
        var display = UITextText.ForTranslation(translated);
        var suffix = Original[VisibleText.Length..];
        if (display.EndsWith(suffix, StringComparison.Ordinal))
            display = display[..^suffix.Length].TrimEnd();
        if (!current.TryGetValue(label, out var providerText) || providerText != VisibleText
            || display.Length == 0 || display.Contains("##", StringComparison.Ordinal)
            || display == VisibleText || label.Operand is not string source || source != Original)
            return false;

        // Preserve the complete original label as the stable ImGui source ID.
        label.Operand = display + "###" + Original;
        return true;
    }

    private static bool IsInt(Instruction instruction, int value) => instruction.IsLdcI4()
        && instruction.GetLdcI4Value() == value;

    private static bool IsField(Instruction instruction, Code opcode, string fullName) =>
        instruction.OpCode.Code == opcode && instruction.Operand is IField field
        && field.FullName == fullName && field.DeclaringType.DefinitionAssembly?.Name == AssemblyName;

    private static bool IsCall(Instruction instruction, string assembly, string owner, string name, bool hasThis,
        string returns, params string[] parameters) =>
        instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is IMethod method
        && method.DeclaringType.FullName == owner && method.DeclaringType.DefinitionAssembly?.Name == assembly
        && method.Name == name && method.MethodSig is { GenParamCount: 0 } signature
        && signature.HasThis == hasThis && !signature.Generic && signature.RetType.FullName == returns
        && signature.Params.Select(type => type.FullName).SequenceEqual(parameters, StringComparer.Ordinal);
}
