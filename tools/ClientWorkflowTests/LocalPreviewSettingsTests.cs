using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Plugin.Services;
using Dalamud.Plugin;
using FireGaze;

internal static class LocalPreviewSettingsTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static async Task Run(Action<bool, string> check)
    {
        var featureType = typeof(Configuration).Assembly.GetType("FireGaze.LocalPreview.LocalPreviewFeature")!;
        var kindType = typeof(Configuration).Assembly.GetType("FireGaze.LocalPreview.Interaction.PreviewKind")!;
        await VerifyDisposeDispatch(featureType, check);
        await VerifyUiDispatch(featureType, kindType, check);
        await VerifyEnableConfirmation(featureType, check);
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
            check(commandFramework.Saves == 0 && commandConfig.LocalPreviewMountEnabled &&
                commandConfig.LocalPreviewMinionEnabled && commandConfig.LocalPreviewAccessoryEnabled &&
                commandConfig.LocalPreviewEmoteEnabled && commandConfig.LocalPreviewItemMenusEnabled,
                "invalid settings command cannot mutate preferences: " + arguments);
        }
    }

    private static async Task VerifyEnableConfirmation(Type featureType, Action<bool, string> check)
    {
        var request = featureType.GetMethod("RequestEnabled", Instance)!;
        var complete = featureType.GetMethod("CompleteEnableConfirmation", Instance)!;
        var (feature, config, framework) = Fixture(featureType);
        config.LocalPreviewEnabled = false;
        featureType.GetField("enabled", Instance)!.SetValue(feature, false);
        request.Invoke(feature, [true]);
        check(!config.LocalPreviewEnabled && framework.Saves == 0 && framework.Schedules == 0,
            "opening the real enable confirmation does not enable or save preview");
        complete.Invoke(feature, [false]);
        check(!config.LocalPreviewEnabled && framework.Saves == 0,
            "canceling the real enable confirmation keeps preview disabled");
        framework.HoldDispatch = true;
        request.Invoke(feature, [true]);
        await Task.Run(() => complete.Invoke(feature, [true]));
        check(!config.LocalPreviewEnabled && framework.Saves == 0 && framework.Schedules == 1,
            "confirming from render queues configuration and runtime activation on framework");
        framework.Flush();
        check(config.LocalPreviewEnabled && (bool)featureType.GetField("enabled", Instance)!.GetValue(feature)!
                && framework.Saves == 1 && !config.LocalPreviewChatLinksEnabled,
            "framework confirmation enables all preview menus while chat linking remains opted out");
        featureType.GetMethod("SetEnabled", Instance)!.Invoke(feature, [false]);
        framework.Flush();
        request.Invoke(feature, [true]);
        check(!config.LocalPreviewEnabled && (bool)featureType.GetField("enableConfirmationVisible", Instance)!.GetValue(feature)!,
            "every new off-to-on request requires confirmation again");
        complete.Invoke(feature, [false]);
        check(!config.LocalPreviewEnabled && framework.Saves == 2,
            "closing a later confirmation cannot reuse earlier consent to enable preview");

        var (released, releaseConfig, releaseFramework) = Fixture(featureType);
        ((SettingsPluginProxy)(object)featureType.GetField("pluginInterface", Instance)!.GetValue(released)!).IsDev = false;
        releaseConfig.LocalPreviewEnabled = false;
        featureType.GetField("enabled", Instance)!.SetValue(released, false);
        await Task.Run(() => featureType.GetMethod("OnCommand")!.Invoke(released, ["on"]));
        check(!releaseConfig.LocalPreviewEnabled && releaseFramework.Saves == 0 && releaseFramework.ChatCalls == 1
                && (bool)featureType.GetField("enableConfirmationVisible", Instance)!.GetValue(released)!,
            "release on command opens the same confirmation instead of bypassing consent");
        await Task.Run(() => featureType.GetMethod("OnCommand")!.Invoke(released, ["option mount off"]));
        check(releaseConfig.LocalPreviewMountEnabled && releaseFramework.Saves == 0,
            "release commands cannot expose hidden individual category preferences");
    }

    private static async Task VerifyDisposeDispatch(Type featureType, Action<bool, string> check)
    {
        var dispose = featureType.GetMethod("Dispose")!;
        var (feature, _, framework) = Fixture(featureType);
        var state = ObserveCleanup(featureType, feature, framework);
        framework.HoldDispatch = true;
        Exception? failure = null;
        var caller = Task.Run(() =>
        {
            try { dispose.Invoke(feature, null); }
            catch (Exception ex) { failure = ex; }
        });
        await Task.WhenAny(framework.DispatchObserved.Task, caller, Task.Delay(1500));
        bool waited = !caller.IsCompleted && framework.Schedules == 1;
        bool untouched = state.Removes == 0 && state.Completions == 0;
        framework.Flush();
        await caller;
        check(waited && untouched && failure is null,
            "worker disposal waits for framework cleanup and performs no native-facing cleanup before dispatch");
        check(state.Removes == 1 && state.Completions == 1 && state.InsideFramework && state.ClosedBeforeCleanup,
            "feature disposal closes input and render state before one complete framework cleanup");
        framework.HoldDispatch = false;
        await Task.Run(() => dispose.Invoke(feature, null));
        check(state.Removes == 1 && state.Completions == 1,
            "repeated worker disposal cannot repeat feature component cleanup");

        var (inline, _, inlineFramework) = Fixture(featureType);
        var inlineState = ObserveCleanup(featureType, inline, inlineFramework);
        inlineFramework.InsideFramework = true;
        dispose.Invoke(inline, null);
        dispose.Invoke(inline, null);
        check(inlineFramework.Schedules == 0 && inlineState.Removes == 1 && inlineState.Completions == 1
                && inlineState.InsideFramework && inlineState.ClosedBeforeCleanup,
            "framework disposal completes inline without scheduling or repeating cleanup");
    }

    private static CleanupState ObserveCleanup(Type featureType, object feature,
        SettingsFrameworkProxy framework)
    {
        var state = new CleanupState();
        var effectsType = featureType.GetField("uiActiveEffects", Instance)!.FieldType.GetElementType()!;
        featureType.GetField("uiActiveEffects", Instance)!.SetValue(feature, Array.CreateInstance(effectsType, 1));
        framework.OnRemoveUpdate = () =>
        {
            state.Removes++;
            state.InsideFramework &= framework.InsideFramework;
            state.ClosedBeforeCleanup &= (bool)featureType.GetField("disposed", Instance)!.GetValue(feature)!
                && ((Array)featureType.GetField("uiActiveEffects", Instance)!.GetValue(feature)!).Length == 0;
        };
        var log = DispatchProxy.Create<IPluginLog, SettingsLogProxy>();
        ((SettingsLogProxy)(object)log).OnLog = method =>
        {
            state.InsideFramework &= framework.InsideFramework;
            if (method == "Information") state.Completions++;
        };
        featureType.GetField("Log", Instance)!.SetValue(feature, log);
        return state;
    }

    private sealed class CleanupState
    {
        public int Removes;
        public int Completions;
        public bool InsideFramework = true;
        public bool ClosedBeforeCleanup = true;
    }

    private static async Task VerifyUiDispatch(Type featureType, Type kindType, Action<bool, string> check)
    {
        var slotType = featureType.Assembly.GetType("FireGaze.LocalPreview.Runtime.PreviewSlot")!;
        // A direct setter, a synchronous framework wait, or a missing unload guard
        // must each fail this real feature invocation with a deferred dispatcher.
        foreach (var (method, arguments) in new (string, object?[])[]
        {
            ("SetEnabled", [false]), ("Cancel", []), ("CancelSlot", [Enum.Parse(slotType, "Mount")]),
            ("SetCategory", [Enum.Parse(kindType, "Mount"), false]), ("SetItemMenus", [false]),
            ("SetChatLinks", [true]),
            ("Request", [Enum.Parse(kindType, "Mount"), (uint)1]),
        })
        {
            var (feature, config, framework) = Fixture(featureType);
            framework.HoldDispatch = true;
            var mutation = featureType.GetMethod(method, Instance)!;
            Exception? failure = null;
            var caller = Task.Run(() =>
            {
                try { mutation.Invoke(feature, arguments); }
                catch (Exception ex) { failure = ex; }
            });
            var returned = await Task.WhenAny(caller, Task.Delay(1500)) == caller;
            var untouched = config.LocalPreviewEnabled && config.LocalPreviewMountEnabled &&
                config.LocalPreviewItemMenusEnabled && framework.Saves == 0 && framework.ChatCalls == 0;
            if (!returned) framework.Flush(); // Release a broken blocking implementation before failing.
            await caller;
            check(returned && failure is null && framework.Schedules == 1 && untouched,
                method + " returns to the render caller without waiting or mutating native-facing state");
            framework.Flush();
            check(method switch
            {
                "SetEnabled" => !config.LocalPreviewEnabled && framework.Saves == 1,
                "SetCategory" => !config.LocalPreviewMountEnabled && framework.Saves == 1,
                "SetItemMenus" => !config.LocalPreviewItemMenusEnabled && framework.Saves == 1,
                "SetChatLinks" => config.LocalPreviewChatLinksEnabled && framework.Saves == 1,
                "Request" => framework.ChatCalls == 1,
                "Cancel" => (string?)featureType.GetField("lastMessage", Instance)!.GetValue(feature) == "全部试用已结束",
                _ => framework.ChatCalls == 0 && framework.Saves == 0,
            }, method + " executes its state change after framework dispatch");

            var (disposedFeature, disposedConfig, disposedFramework) = Fixture(featureType);
            disposedFramework.HoldDispatch = true;
            mutation.Invoke(disposedFeature, arguments);
            featureType.GetField("disposed", Instance)!.SetValue(disposedFeature, true);
            disposedFramework.Flush();
            check(disposedConfig.LocalPreviewEnabled && disposedConfig.LocalPreviewMountEnabled &&
                disposedConfig.LocalPreviewItemMenusEnabled && !disposedConfig.LocalPreviewChatLinksEnabled && disposedFramework.Saves == 0 && disposedFramework.ChatCalls == 0 &&
                featureType.GetField("lastMessage", Instance)!.GetValue(disposedFeature) is null,
                method + " queued before unload cannot mutate or call client services after disposal");
        }

        var (snapshotFeature, _, snapshotFramework) = Fixture(featureType);
        snapshotFramework.InsideFramework = true;
        var runtimeType = featureType.Assembly.GetType("FireGaze.LocalPreview.Runtime.ActorPreviewRuntime")!;
        var effectType = featureType.Assembly.GetType("FireGaze.LocalPreview.Runtime.PreviewEffect")!;
        var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
        var effects = (System.Collections.IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(slotType, effectType))!;
        var slot = Enum.Parse(slotType, "Mount");
        effects.Add(slot, Activator.CreateInstance(effectType, [slot, (ushort)223, (byte)0])!);
        runtimeType.GetField("effects", Instance)!.SetValue(runtime, effects);
        featureType.GetField("runtime", Instance)!.SetValue(snapshotFeature, runtime);
        featureType.GetMethod("PublishActiveEffects", Instance)!.Invoke(snapshotFeature, null);
        var snapshot = (Array)featureType.GetMethod("ReadActiveEffects", Instance)!.Invoke(snapshotFeature, null)!;
        effects.Clear();
        check(snapshot.Length == 1, "render effect snapshot survives later framework dictionary changes");
        featureType.GetMethod("PublishActiveEffects", Instance)!.Invoke(snapshotFeature, null);
        check(((Array)featureType.GetMethod("ReadActiveEffects", Instance)!.Invoke(snapshotFeature, null)!).Length == 0 && snapshot.Length == 1,
            "publishing a new effect snapshot never mutates a snapshot already held by the render caller");

        var (closedFeature, _, closedFramework) = Fixture(featureType);
        featureType.GetField("runtime", Instance)!.SetValue(closedFeature, runtime);
        closedFramework.HoldDispatch = true;
        featureType.GetMethod("Request", Instance)!.Invoke(closedFeature, [Enum.Parse(kindType, "Mount"), (uint)223]);
        featureType.GetField("enabled", Instance)!.SetValue(closedFeature, false);
        closedFramework.Flush();
        check(closedFramework.ChatCalls == 1 && effects.Count == 0,
            "a request queued while enabled cannot start a preview after the feature is closed");
    }

    private static (object Feature, Configuration Config, SettingsFrameworkProxy Framework) Fixture(Type featureType)
    {
        var config = new Configuration { LocalPreviewEnabled = true, LocalPreviewSettingsRevision = 1 };
        var service = DispatchProxy.Create<IFramework, SettingsFrameworkProxy>();
        var framework = (SettingsFrameworkProxy)(object)service;
        var feature = RuntimeHelpers.GetUninitializedObject(featureType);
        featureType.GetField("Framework", Instance)!.SetValue(feature, service);
        var plugin = DispatchProxy.Create<IDalamudPluginInterface, SettingsPluginProxy>();
        featureType.GetField("pluginInterface", Instance)!.SetValue(feature, plugin);
        var chat = DispatchProxy.Create<IChatGui, SettingsChatProxy>();
        ((SettingsChatProxy)(object)chat).OnPrint = () =>
        {
            if (!framework.InsideFramework) throw new InvalidOperationException("client chat ran outside the framework thread");
            framework.ChatCalls++;
        };
        featureType.GetField("Chat", Instance)!.SetValue(feature, chat);
        featureType.GetField("config", Instance)!.SetValue(feature, config);
        featureType.GetField("enabled", Instance)!.SetValue(feature, true);
        featureType.GetField("saveConfig", Instance)!.SetValue(feature, (Action)(() =>
        {
            if (!framework.InsideFramework) throw new InvalidOperationException("configuration save ran outside the framework thread");
            framework.Saves++;
        }));
        return (feature, config, framework);
    }
}

public class SettingsPluginProxy : DispatchProxy
{
    public bool IsDev = true;
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        => method!.Name == "get_IsDev" ? IsDev : throw new NotSupportedException(method.Name);
}

public class SettingsChatProxy : DispatchProxy
{
    public Action OnPrint = () => { };
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method!.Name is not ("Print" or "PrintError")) throw new NotSupportedException(method.Name);
        OnPrint();
        return null;
    }
}

public class SettingsLogProxy : DispatchProxy
{
    public Action<string> OnLog = _ => { };
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        OnLog(method!.Name);
        if (method.ReturnType == typeof(void)) return null;
        return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}

public class SettingsFrameworkProxy : DispatchProxy
{
    public bool InsideFramework;
    public int Schedules;
    public int Saves;
    public int ChatCalls;
    public bool HoldDispatch;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> pending = new();
    public Action BeforeDispatch = () => { };
    public Action OnRemoveUpdate = () => { };
    public readonly TaskCompletionSource<bool> DispatchObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Flush()
    {
        while (pending.TryDequeue(out var action)) action();
    }

    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method!.Name == "get_IsInFrameworkUpdateThread") return InsideFramework;
        if (method.Name == "remove_Update") { OnRemoveUpdate(); return null; }
        if (method.Name != "RunOnFrameworkThread") throw new NotSupportedException(method.Name);
        BeforeDispatch();
        Schedules++;
        if (HoldDispatch)
        {
            var resultType = method.ReturnType.GenericTypeArguments[0];
            var completion = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(resultType))!;
            pending.Enqueue(() =>
            {
                InsideFramework = true;
                try
                {
                    var value = ((Delegate)arguments![0]!).DynamicInvoke();
                    completion.GetType().GetMethod("SetResult")!.Invoke(completion, [value]);
                }
                catch (Exception ex) { completion.GetType().GetMethod("SetException", [typeof(Exception)])!.Invoke(completion, [ex]); }
                finally { InsideFramework = false; }
            });
            DispatchObserved.TrySetResult(true);
            return completion.GetType().GetProperty("Task")!.GetValue(completion);
        }
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
