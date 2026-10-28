using dnlib.DotNet;

namespace FireGaze.UIText;

/// <summary>
///     Display metadata admitted by its exact type, constructor and consumer
///     contract. Legacy short-name rules cannot authorize metadata writes.
/// </summary>
internal static class UITextAttributeContracts
{
    public static bool IsKnownType(string typeFullName) => typeFullName is
        "AG.EnumLocalization.Attributes.EnumLocAttribute"
        or "SonarPlugin.Attributes.EnumCheapLocAttribute"
        or "System.ComponentModel.DescriptionAttribute"
        or "System.ComponentModel.CategoryAttribute"
        or "System.ComponentModel.DataAnnotations.DisplayAttribute"
        or "BossMod.PropertyDisplayAttribute"
        or "BossMod.ConfigDisplayAttribute"
        or "BossMod.PropertyComboAttribute"
        or "SimpleTweaksPlugin.TweakSystem.TweakNameAttribute"
        or "SimpleTweaksPlugin.TweakSystem.TweakDescriptionAttribute"
        or "SimpleTweaksPlugin.TweakSystem.TweakConfigOptionAttribute"
        or "SimpleTweaksPlugin.TweakSystem.ChangelogAttribute"
        or "RotationSolver.Basic.Attributes.UIAttribute"
        or "RotationSolver.Basic.Attributes.RotationConfigAttribute"
        or "ECommons.Commands.CmdAttribute"
        or "ECommons.Commands.SubCmdAttribute";

    public static bool IsDisplayConstructorArgument(CustomAttribute attribute, IHasCustomAttribute host, int index)
    {
        if (index < 0 || index >= attribute.ConstructorArguments.Count)
        {
            return false;
        }

        return ContractOf(attribute, host) switch
        {
            Contract.EnumLocalization or Contract.CheapLocalization => index == 1,
            Contract.Description or Contract.Category or Contract.SimpleName
                or Contract.SimpleDescription or Contract.SimpleOption or Contract.RotationUI => index == 0,
            Contract.BossProperty => index is 0 or 2,
            Contract.BossComboArray => index == 0,
            Contract.BossComboPair => index is 0 or 1,
            // Only the proven change entries are display strings; version and
            // author remain unchanged in the changelog consumer.
            Contract.SimpleChangelog => index == 1,
            Contract.Command or Contract.SubCommand => index == 1,
            _ => false,
        };
    }

    public static bool IsDisplayNamedArgument(CustomAttribute attribute, IHasCustomAttribute host, CANamedArgument named)
    {
        if (named.Type?.FullName != "System.String" || named.Argument.Type?.FullName != "System.String")
        {
            return false;
        }

        var name = named.Name?.String;
        return ContractOf(attribute, host) switch
        {
            Contract.EnumLocalization => name == "Fallback",
            Contract.Display => name is "Name" or "ShortName" or "Description" or "Prompt" or "GroupName",
            Contract.BossConfig => name == "Name",
            Contract.SimpleOption or Contract.SimpleDerivedOption => name == "HelpText",
            Contract.RotationUI => name == "Description",
            Contract.RotationConfig => name is "Name" or "Tooltip",
            _ => false,
        };
    }

    private static Contract ContractOf(CustomAttribute attribute, IHasCustomAttribute host)
    {
        // A full type name and constructor shape are not sufficient identity:
        // another plugin can define the same metadata for a different consumer.
        // Keep the write boundary tied to the assembly that owns the proven
        // attribute contract before inspecting any display arguments.
        if (!IsTrustedAssembly(attribute))
        {
            return Contract.None;
        }

        switch (attribute.TypeFullName)
        {
            case "AG.EnumLocalization.Attributes.EnumLocAttribute":
                return HasShape(attribute, "System.String", "System.String") ? Contract.EnumLocalization : Contract.None;
            case "SonarPlugin.Attributes.EnumCheapLocAttribute":
                return HasShape(attribute, "System.String", "System.String") ? Contract.CheapLocalization : Contract.None;
            case "System.ComponentModel.DescriptionAttribute":
                // RotationConfigBase.ChangeType uses enum descriptions as input
                // values. A framework type name alone cannot prove display use.
                return HasShape(attribute, "System.String") && !IsEnumHost(host) ? Contract.Description : Contract.None;
            case "System.ComponentModel.CategoryAttribute":
                return HasShape(attribute, "System.String") ? Contract.Category : Contract.None;
            case "System.ComponentModel.DataAnnotations.DisplayAttribute":
                // GetName/GetDescription/etc. resolve these strings as resource
                // property names whenever ResourceType is supplied.
                return HasShape(attribute) && !attribute.NamedArguments.Any(named =>
                    named.Name == "ResourceType" && named.Argument.Value is not null) ? Contract.Display : Contract.None;
            case "BossMod.PropertyDisplayAttribute":
                return HasShape(attribute, "System.String", "System.UInt32", "System.String", "System.Boolean",
                    "System.String[]", "System.String", "System.String", "System.Type") ? Contract.BossProperty : Contract.None;
            case "BossMod.ConfigDisplayAttribute":
                return HasShape(attribute) ? Contract.BossConfig : Contract.None;
            case "BossMod.PropertyComboAttribute":
                // UICombo.Bool/Int selects by numeric index. Values contain
                // the false/true option labels, not serialized configuration.
                return HasShape(attribute, "System.String[]") ? Contract.BossComboArray
                    : HasShape(attribute, "System.String", "System.String") ? Contract.BossComboPair : Contract.None;
            case "SimpleTweaksPlugin.TweakSystem.TweakNameAttribute":
                return HasShape(attribute, "System.String") ? Contract.SimpleName : Contract.None;
            case "SimpleTweaksPlugin.TweakSystem.TweakDescriptionAttribute":
                return HasShape(attribute, "System.String") ? Contract.SimpleDescription : Contract.None;
            case "SimpleTweaksPlugin.TweakSystem.TweakConfigOptionAttribute":
                var keyIndex = HasShape(attribute, "System.String", "System.Int32", "System.String") ? 2
                    : HasShape(attribute, "System.String", "System.String", "System.Int32", "System.String") ? 3 : -1;
                if (keyIndex < 0)
                {
                    return Contract.None;
                }

                // Constructor: LocalizeKey = localizeKey ?? name. Translating
                // name with a null explicit key would change lookup behavior.
                return attribute.ConstructorArguments[keyIndex].Value is null ? Contract.SimpleDerivedOption : Contract.SimpleOption;
            case "SimpleTweaksPlugin.TweakSystem.ChangelogAttribute":
                return HasShape(attribute, "System.String", "System.String[]") ? Contract.SimpleChangelog : Contract.None;
            case "RotationSolver.Basic.Attributes.UIAttribute":
                return HasShape(attribute, "System.String") ? Contract.RotationUI : Contract.None;
            case "RotationSolver.Basic.Attributes.RotationConfigAttribute":
                return HasShape(attribute, "RotationSolver.Basic.Data.CombatType") ? Contract.RotationConfig : Contract.None;
            case "ECommons.Commands.CmdAttribute":
                return HasShape(attribute, "System.String", "System.String", "System.Boolean", "System.Boolean")
                    ? Contract.Command : Contract.None;
            case "ECommons.Commands.SubCmdAttribute":
                return HasShape(attribute, "System.String", "System.String") ? Contract.SubCommand : Contract.None;
            default:
                return Contract.None;
        }
    }

    private static bool IsTrustedAssembly(CustomAttribute attribute)
    {
        var assembly = attribute.Constructor?.DeclaringType?.DefinitionAssembly?.Name?.String;
        if (string.IsNullOrEmpty(assembly))
        {
            return false;
        }

        return attribute.TypeFullName switch
        {
            "AG.EnumLocalization.Attributes.EnumLocAttribute" => assembly == "AG.EnumLocalization",
            "SonarPlugin.Attributes.EnumCheapLocAttribute" => assembly == "SonarPlugin",
            "System.ComponentModel.DescriptionAttribute" or
                "System.ComponentModel.CategoryAttribute" => assembly is "System.ComponentModel.Primitives"
                    or "System.Private.CoreLib" or "mscorlib",
            "System.ComponentModel.DataAnnotations.DisplayAttribute" => assembly is "System.ComponentModel.Annotations"
                    or "System.ComponentModel.DataAnnotations",
            "BossMod.PropertyDisplayAttribute" or
                "BossMod.ConfigDisplayAttribute" or
                "BossMod.PropertyComboAttribute" => assembly is "BossMod" or "BossModReborn",
            "SimpleTweaksPlugin.TweakSystem.TweakNameAttribute" or
                "SimpleTweaksPlugin.TweakSystem.TweakDescriptionAttribute" or
                "SimpleTweaksPlugin.TweakSystem.TweakConfigOptionAttribute" or
                "SimpleTweaksPlugin.TweakSystem.ChangelogAttribute" => assembly == "SimpleTweaksPlugin",
            "RotationSolver.Basic.Attributes.UIAttribute" or
                "RotationSolver.Basic.Attributes.RotationConfigAttribute" => assembly == "RotationSolver.Basic",
            "ECommons.Commands.CmdAttribute" or
                "ECommons.Commands.SubCmdAttribute" => assembly == "ECommons",
            _ => false,
        };
    }

    private static bool HasShape(CustomAttribute attribute, params string[] parameters)
    {
        var constructor = attribute.Constructor;
        var signature = constructor.MethodSig;
        if (constructor.Name != ".ctor" || signature is null || !signature.HasThis
            || signature.RetType?.FullName != "System.Void" || signature.GenParamCount != 0
            || signature.ParamsAfterSentinel is { Count: > 0 }
            || signature.Params.Count != parameters.Length || attribute.ConstructorArguments.Count != parameters.Length)
        {
            return false;
        }

        for (var index = 0; index < parameters.Length; index++)
        {
            if (signature.Params[index].FullName != parameters[index]
                || attribute.ConstructorArguments[index].Type?.FullName != parameters[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEnumHost(IHasCustomAttribute host) => host switch
    {
        FieldDef field => field.DeclaringType?.IsEnum == true,
        TypeDef type => type.IsEnum,
        _ => false,
    };

    private enum Contract
    {
        None,
        EnumLocalization,
        CheapLocalization,
        Description,
        Category,
        Display,
        BossProperty,
        BossConfig,
        BossComboArray,
        BossComboPair,
        SimpleName,
        SimpleDescription,
        SimpleOption,
        SimpleDerivedOption,
        SimpleChangelog,
        RotationUI,
        RotationConfig,
        Command,
        SubCommand,
    }
}
