using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using FireGaze;

internal static class LocalPreviewSettingsTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static async Task Run(Action<bool, string> check)
    {
        var featureType = typeof(Configuration).Assembly.GetType("FireGaze.LocalPreview.LocalPreviewFeature")!;
        var kindType = typeof(Configuration).Assembly.GetType("FireGaze.LocalPreview.Interaction.PreviewKind")!;
        foreach (var name in new[] { "Mount", "Minion", "Accessory", "Emote" })
        {
            var (feature, config, framework) = Fixture(featureType);
            var preference = typeof(Configuration).GetProperty("LocalPreview" + name + "Enabled")!;
            framework.BeforeDispatch = () => check((bool)preference.GetValue(config)!, name + " preference remains unchanged before framework dispatch");
            await Task.Run(() => featureType.GetMethod("SetCategory", Instance)!.Invoke(feature, [Enum.Parse(kindType, name), false]));
            check(framework.Schedules == 1 && framework.Saves == 1 && !(bool)preference.GetValue(config)!,
                name + " disabled preference is saved once through the framework dispatcher");

            framework.InsideFramework = true;
            featureType.GetMethod("SetCategory", Instance)!.Invoke(feature, [Enum.Parse(kindType, name), true]);
            check(framework.Schedules == 1 && framework.Saves == 2 && (bool)preference.GetValue(config)!,
                name + " UI setting applies immediately when already on the framework thread");
        }

        var (invalidFeature, invalidConfig, invalidFramework) = Fixture(featureType);
        await Task.Run(() => featureType.GetMethod("SetCategory", Instance)!.Invoke(invalidFeature, [Enum.ToObject(kindType, 99), false]));
        check(invalidFramework.Saves == 0 && invalidConfig.LocalPreviewMountEnabled && invalidConfig.LocalPreviewMinionEnabled &&
            invalidConfig.LocalPreviewAccessoryEnabled && invalidConfig.LocalPreviewEmoteEnabled,
            "an unknown category cannot write or save another preference");

        var itemSetter = featureType.GetMethod("SetItemMenus", Instance);
        check(itemSetter is not null, "item UI and commands share one framework-safe preference setter");
        var (itemFeature, itemConfig, itemFramework) = Fixture(featureType);
        itemFramework.BeforeDispatch = () => check(itemConfig.LocalPreviewItemMenusEnabled,
            "item preference remains unchanged before framework dispatch");
        await Task.Run(() => itemSetter!.Invoke(itemFeature, [false]));
        check(itemFramework.Schedules == 1 && itemFramework.Saves == 1 && !itemConfig.LocalPreviewItemMenusEnabled,
            "item preference is disabled and saved once on the framework thread");
        itemFramework.InsideFramework = true;
        itemSetter!.Invoke(itemFeature, [true]);
        check(itemFramework.Schedules == 1 && itemFramework.Saves == 2 && itemConfig.LocalPreviewItemMenusEnabled,
            "item UI setting applies immediately when already on the framework thread");

        var (queuedCategoryFeature, queuedCategoryConfig, queuedCategoryFramework) = Fixture(featureType);
        queuedCategoryFramework.BeforeDispatch = () => featureType.GetField("disposed", Instance)!.SetValue(queuedCategoryFeature, true);
        await Task.Run(() => featureType.GetMethod("SetCategory", Instance)!.Invoke(queuedCategoryFeature, [Enum.Parse(kindType, "Emote"), false]));
        check(queuedCategoryConfig.LocalPreviewEmoteEnabled && queuedCategoryFramework.Saves == 0,
            "category work queued before unload cannot mutate or save after disposal");
        var (queuedItemsFeature, queuedItemsConfig, queuedItemsFramework) = Fixture(featureType);
        queuedItemsFramework.BeforeDispatch = () => featureType.GetField("disposed", Instance)!.SetValue(queuedItemsFeature, true);
        await Task.Run(() => itemSetter!.Invoke(queuedItemsFeature, [false]));
        check(queuedItemsConfig.LocalPreviewItemMenusEnabled && queuedItemsFramework.Saves == 0,
            "item preference work queued before unload cannot mutate or save after disposal");

        foreach (var name in new[] { "Mount", "Minion", "Accessory", "Emote", "ItemMenus" })
        {
            var (commandFeature, commandConfig, commandFramework) = Fixture(featureType);
            var option = name == "ItemMenus" ? "items" : name.ToLowerInvariant();
            var property = typeof(Configuration).GetProperty("LocalPreview" + name + "Enabled")!;
            await Task.Run(() => featureType.GetMethod("OnCommand")!.Invoke(commandFeature, [$"option {option} off"]));
            check(commandFramework.Schedules == 1 && commandFramework.Saves == 1 && !(bool)property.GetValue(commandConfig)!,
                option + " command changes and saves the intended preference through framework dispatch");
            check(new[] { "Mount", "Minion", "Accessory", "Emote", "ItemMenus" }.Where(other => other != name)
                    .All(other => (bool)typeof(Configuration).GetProperty("LocalPreview" + other + "Enabled")!.GetValue(commandConfig)!),
                option + " command preserves all other preferences");
            commandFramework.InsideFramework = true;
            featureType.GetMethod("OnCommand")!.Invoke(commandFeature, [$"  OpTiOn   {option.ToUpperInvariant()}   On  "]);
            check(commandFramework.Schedules == 1 && commandFramework.Saves == 2 && (bool)property.GetValue(commandConfig)!,
                option + " command accepts normal whitespace and case without re-dispatching the game thread");
        }
        foreach (var arguments in new[] { "option", "option mount", "option mount off on", "option 0 off", "option 99 off", "option all off", "option mount 0", "option mount true" })
        {
            var (commandFeature, commandConfig, commandFramework) = Fixture(featureType);
            await Task.Run(() => featureType.GetMethod("OnCommand")!.Invoke(commandFeature, [arguments]));
            check(commandFramework.Schedules == 0 && commandFramework.Saves == 0 && commandConfig.LocalPreviewMountEnabled &&
                commandConfig.LocalPreviewMinionEnabled && commandConfig.LocalPreviewAccessoryEnabled &&
                commandConfig.LocalPreviewEmoteEnabled && commandConfig.LocalPreviewItemMenusEnabled,
                "invalid settings command cannot mutate preferences: " + arguments);
        }
    }

    private static (object Feature, Configuration Config, SettingsFrameworkProxy Framework) Fixture(Type featureType)
    {
        var config = new Configuration();
        var service = DispatchProxy.Create<IFramework, SettingsFrameworkProxy>();
        var framework = (SettingsFrameworkProxy)(object)service;
        var feature = RuntimeHelpers.GetUninitializedObject(featureType);
        featureType.GetField("Framework", Instance)!.SetValue(feature, service);
        featureType.GetField("Chat", Instance)!.SetValue(feature, DispatchProxy.Create<IChatGui, SettingsChatProxy>());
        featureType.GetField("config", Instance)!.SetValue(feature, config);
        featureType.GetField("saveConfig", Instance)!.SetValue(feature, (Action)(() =>
        {
            if (!framework.InsideFramework) throw new InvalidOperationException("configuration save ran outside the framework thread");
            framework.Saves++;
        }));
        return (feature, config, framework);
    }
}

public class SettingsChatProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        => method!.Name is "Print" or "PrintError" ? null : throw new NotSupportedException(method.Name);
}

public class SettingsFrameworkProxy : DispatchProxy
{
    public bool InsideFramework;
    public int Schedules;
    public int Saves;
    public Action BeforeDispatch = () => { };

    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method!.Name == "get_IsInFrameworkUpdateThread") return InsideFramework;
        if (method.Name != "RunOnFrameworkThread") throw new NotSupportedException(method.Name);
        BeforeDispatch();
        Schedules++;
        InsideFramework = true;
        try
        {
            var value = ((Delegate)arguments![0]!).DynamicInvoke();
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(method.ReturnType.GenericTypeArguments[0])
                .Invoke(null, [value]);
        }
        finally { InsideFramework = false; }
    }
}
