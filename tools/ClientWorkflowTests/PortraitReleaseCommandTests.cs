using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FireGaze;

internal static class PortraitReleaseCommandTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run(Action<bool, string> check)
    {
        var assembly = typeof(Configuration).Assembly;
        var featureType = assembly.GetType("FireGaze.PortraitPreview.PortraitPreviewFeature")!;
        var pluginType = assembly.GetType("FireGaze.Plugin")!;
        var command = featureType.GetMethod("OnCommand")!;
        var frameworkProperty = pluginType.GetProperty("Framework")!;
        var chatProperty = pluginType.GetProperty("Chat")!;
        var oldFramework = frameworkProperty.GetValue(null);
        var oldChat = chatProperty.GetValue(null);
        var framework = DispatchProxy.Create<IFramework, PortraitReleaseFrameworkProxy>();
        var host = (PortraitReleaseFrameworkProxy)(object)framework;
        var chat = DispatchProxy.Create<IChatGui, PortraitReleaseChatProxy>();
        var chatHost = (PortraitReleaseChatProxy)(object)chat;
        var results = new List<(bool Passed, string Name)>();

        object Feature(bool isDev)
        {
            var feature = RuntimeHelpers.GetUninitializedObject(featureType);
            var plugin = DispatchProxy.Create<IDalamudPluginInterface, PortraitReleasePluginProxy>();
            ((PortraitReleasePluginProxy)(object)plugin).IsDev = isDev;
            featureType.GetField("pluginInterface", Instance)!.SetValue(feature, plugin);
            return feature;
        }

        try
        {
            frameworkProperty.SetValue(null, framework);
            chatProperty.SetValue(null, chat);
            foreach (var name in new[] { "native-joint", "native-owned", "native-census" })
            foreach (var action in new[] { "start", "stop", "status", "unexpected" })
            {
                host.Pending.Clear(); chatHost.Messages.Clear();
                command.Invoke(Feature(false), [$"{name} {action}"]);
                results.Add((host.Pending.Count == 0 && chatHost.Messages.Count == 0,
                    $"release {name} {action} is silent and queues no native-facing diagnostic work"));
            }
            foreach (var text in new[] { "native-scene start 0", "light-fit window" })
            {
                host.Pending.Clear(); chatHost.Messages.Clear();
                command.Invoke(Feature(false), [text]);
                results.Add((host.Pending.Count == 0 && chatHost.Messages.Count == 0,
                    $"release {text} remains unavailable before any framework work"));
            }
            foreach (var name in new[] { "native-joint", "native-owned", "native-census" })
            {
                host.Pending.Clear(); chatHost.Messages.Clear();
                command.Invoke(Feature(true), [$"{name} start"]);
                // Hold all delegates: this verifies the real command dispatch without
                // creating hooks, reading game memory, or running a diagnostic.
                results.Add((host.Pending.Count == 1 && chatHost.Messages.Count == 0,
                    $"development {name} start still reaches its deferred framework entry"));
            }
            host.Pending.Clear(); chatHost.Messages.Clear();
            command.Invoke(Feature(false), ["not-a-command"]);
            results.Add((chatHost.Messages.Count == 1 && chatHost.Messages[0].Contains("open|close|status")
                && !chatHost.Messages[0].Contains("light-space") && !chatHost.Messages[0].Contains("light-fit"),
                "release portrait usage advertises available preview commands without deferred tools"));
        }
        finally
        {
            // Delegates deliberately never execute, even on a red test run.
            host.Pending.Clear();
            frameworkProperty.SetValue(null, oldFramework);
            chatProperty.SetValue(null, oldChat);
        }
        check(results.All(result => result.Passed), "portrait release command boundary: "
            + string.Join("; ", results.Where(result => !result.Passed).Select(result => result.Name)));
        foreach (var result in results) check(result.Passed, result.Name);
    }
}

public class PortraitReleasePluginProxy : DispatchProxy
{
    public bool IsDev;
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        => method!.Name == "get_IsDev" ? IsDev : throw new NotSupportedException(method.Name);
}

public class PortraitReleaseFrameworkProxy : DispatchProxy
{
    public List<Delegate> Pending { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method!.Name == "get_IsInFrameworkUpdateThread") return false;
        if (method.Name != "RunOnFrameworkThread") throw new NotSupportedException(method.Name);
        Pending.Add((Delegate)arguments![0]!);
        if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
        var valueType = method.ReturnType.GenericTypeArguments[0];
        return typeof(Task).GetMethod(nameof(Task.FromResult))!
            .MakeGenericMethod(valueType)
            .Invoke(null, [valueType.IsValueType ? Activator.CreateInstance(valueType) : null]);
    }
}

public class PortraitReleaseChatProxy : DispatchProxy
{
    public List<string> Messages { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method!.Name is not ("Print" or "PrintError")) throw new NotSupportedException(method.Name);
        Messages.Add(arguments![0]?.ToString() ?? "");
        return null;
    }
}
