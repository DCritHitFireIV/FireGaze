using System.Reflection;
using Dalamud.Plugin;

// Only the game runtime boundary is replaced. The observer and fingerprint verifier are real.
namespace FireGaze.LocalPreview.Runtime
{
    public sealed class ActorPreviewRuntime
    {
        public bool HasActivePreview { get; set; }
        public int Cleanups;
        public bool VisibilityProtection;
        public void SetHmsVisibilityProtection(bool enabled) => VisibilityProtection = enabled;
        public void BeforeHmsLifecycle() { Cleanups++; HasActivePreview = false; }
    }
}

public class PluginListService : DispatchProxy
{
    public IExposedPlugin[] Plugins = [];
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? arguments)
        => targetMethod?.Name == "get_InstalledPlugins" ? Plugins : null;
    public static (IDalamudPluginInterface Service, PluginListService Host) CreateHost()
    {
        var service = Create<IDalamudPluginInterface, PluginListService>();
        return (service, (PluginListService)(object)service);
    }
}

public class ExposedService : DispatchProxy
{
    public object? plugin;
    public bool Loaded = true;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? arguments) => targetMethod?.Name switch
    {
        "get_IsLoaded" => Loaded,
        "get_InternalName" => "HMSync",
        "get_Name" => "HMapSync (HMS) Dev",
        _ => targetMethod?.ReturnType.IsValueType == true && targetMethod.ReturnType != typeof(void)
            ? Activator.CreateInstance(targetMethod.ReturnType) : null,
    };
}

public class NullService : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? arguments)
        => targetMethod?.ReturnType.IsValueType == true && targetMethod.ReturnType != typeof(void)
            ? Activator.CreateInstance(targetMethod.ReturnType) : null;
    public static T New<T>() where T : class => Create<T, NullService>();
}
