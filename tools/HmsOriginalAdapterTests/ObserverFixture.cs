using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Compatibility;
using FireGaze.LocalPreview.Core;
using FireGaze.LocalPreview.Runtime;

internal sealed class ObserverFixture : IDisposable
{
    private readonly AssemblyLoadContext context = new("HMS observer test " + Guid.NewGuid(), true);
    public readonly ActorPreviewRuntime Runtime = new();
    public readonly HmsOriginalAdapter Adapter;
    public readonly PluginListService Host;
    public readonly IExposedPlugin Exposed;
    public readonly ExposedService Exposure;
    public readonly Assembly Target;
    public readonly object Instance;
    public readonly object Zone;
    public readonly object Relay;
    public readonly object Visibility;
    public HmsOriginalBodyState? Body = new(0x1000, 1, 1, 0, 0, 255, 0, 0, 0, 0, 0, 0, 0, 0);

    public ObserverFixture(string binary, bool stream = false, string? suppliedFile = null)
    {
        context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
        if (stream) { using var input = File.OpenRead(binary); Target = context.LoadFromStream(input); }
        else Target = context.LoadFromAssemblyPath(binary);
        var type = Target.GetType("HMSync.HMSyncPlugin", true)!;
        Instance = RuntimeHelpers.GetUninitializedObject(type);
        Zone = InitializeField("zoneLoad");
        Relay = InitializeField("relay");
        Visibility = InitializeField("actorVisibility");

        var exposedType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.ExposedPlugin", true)!;
        var localType = exposedType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(field => field.FieldType.FullName == "Dalamud.Plugin.Internal.Types.LocalPlugin").FieldType;
        var local = RuntimeHelpers.GetUninitializedObject(localType);
        localType.GetField("instance", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(local, Instance);
        localType.GetField("<DllFile>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(local, new FileInfo(suppliedFile ?? binary));
        Exposed = DispatchProxy.Create<IExposedPlugin, ExposedService>();
        Exposure = (ExposedService)(object)Exposed;
        Exposure.plugin = local;
        var services = PluginListService.CreateHost();
        Host = services.Host;
        Host.Plugins = [Exposed];
        Adapter = new HmsOriginalAdapter(services.Service, NullService.New<ICommandManager>(), Runtime,
            NullService.New<IPluginLog>(), () => Body);

        object InitializeField(string name)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
            var value = RuntimeHelpers.GetUninitializedObject(field.FieldType);
            field.SetValue(Instance, value);
            return value;
        }
    }

    public HmsCoexistenceDecision Refresh() => Adapter.Refresh();
    public int ZoneHandlers => (Zone.GetType().GetField("ZoneWillChange", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(Zone) as Action)?.GetInvocationList().Length ?? 0;
    public void SignalZone() => (Zone.GetType().GetField("ZoneWillChange", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(Zone) as Action)?.Invoke();
    public void SetZone(string name, object value)
    {
        var fieldName = name == "HomeRestoreArmed" ? "homeRestoreArmed" : "<" + name + ">k__BackingField";
        Zone.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Zone, value);
    }
    public void Session(bool active) => Relay.GetType().GetProperty("SoloMode")!.SetValue(Relay, active);
    public void Visible(bool active) => Visibility.GetType().GetField("active", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Visibility, active);
    public void Dispose() { Adapter.Dispose(); context.Unload(); }
}
