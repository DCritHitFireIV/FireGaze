using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FireGaze.LocalPreview.Core;
using FireGaze.LocalPreview.Runtime;

namespace FireGaze.LocalPreview.Compatibility;

/// <summary>
/// Audits the unmodified HMS binary and observes its public zone event and lifecycle state.
/// The preview clone leaves the original local actor authoritative for HMS capture and toggles.
/// Polling is a next-frame fallback: it cannot identify a same-ID replay or bracket an input.
/// Native leaf and draw lifecycle protection belongs to ActorPreviewRuntime.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class HmsOriginalAdapter : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly ActorPreviewRuntime runtime;
    private readonly IPluginLog log;
    private readonly Func<HmsOriginalBodyState?> readOriginalBody;
    private Assembly? assembly;
    private object? plugin;
    private object? zoneLoad;
    private object? relay;
    private object? visibility;
    private EventInfo? zoneEvent;
    private Action? zoneHandler;
    private HmsLifecycleState? lifecycle;
    private HmsOriginalBodyState? originalBody;
    private WeakReference<Assembly>? rejected;
    private string? fault;
    private bool disposed;

    public HmsOriginalAdapter(IDalamudPluginInterface pluginInterface, ICommandManager commands,
        ActorPreviewRuntime runtime, IPluginLog log, Func<HmsOriginalBodyState?>? readOriginalBody = null)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.runtime = runtime;
        this.log = log;
        this.readOriginalBody = readOriginalBody ?? HmsOriginalBodyState.Read;
    }

    public HmsCoexistenceDecision Refresh()
    {
        if (disposed) return new(false, "HMS 兼容监测器已卸载。");
        try
        {
            var loaded = pluginInterface.InstalledPlugins.Where(p => p.IsLoaded &&
                (p.InternalName is "HMSync" or "HMSyncDev" || p.Name.Contains("HMapSync", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (loaded.Length == 0) { Detach(); return new(true, ""); }
            if (loaded.Length != 1) { Detach(); return new(false, "检测到多个 HMS 同时加载，请仅加载一个后再试。"); }
            var instance = FindInstance(loaded[0]);
            if (instance == null && commands.Commands.TryGetValue("/hms", out var command))
                instance = command.Handler.Target?.GetType().FullName == "HMSync.HMSyncPlugin" ? command.Handler.Target : null;
            if (instance == null || instance.GetType().FullName != "HMSync.HMSyncPlugin")
            { Detach(); return new(false, "HMS 正在加载或其实例暂不可读取，请稍后再试。"); }
            var candidate = instance.GetType().Assembly;
            if (rejected != null && rejected.TryGetTarget(out var refused) && ReferenceEquals(refused, candidate))
                return new(false, fault ?? "该 HMS 版本尚未通过本地试用兼容验证。");
            if (!ReferenceEquals(candidate, assembly) || !ReferenceEquals(instance, plugin))
            {
                Detach();
                try { Install(candidate, instance, loaded[0]); rejected = null; }
                catch { rejected = new(candidate); throw; }
            }
            return Poll();
        }
        catch (Exception exception)
        {
            Fail(exception);
            return new(false, fault!);
        }
    }

    private void Install(Assembly target, object instance, object exposed)
    {
        // Stream-loaded plugins have an empty Assembly.Location. Trust only the exact
        // exposed LocalPlugin instance and its own on-disk file, after IsLoaded was checked.
        var local = FindLocalPlugin(exposed);
        if (local == null || !ReferenceEquals(ReadField(local, "instance"), instance)
            || !ReferenceEquals(instance.GetType().Assembly, target))
            throw new InvalidOperationException("HMS exposed plugin instance differs from the loaded candidate");
        var file = local.GetType().GetProperty("DllFile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(local) as FileInfo
            ?? throw new InvalidOperationException("HMS exposed plugin DLL file is unavailable");
        var bytes = File.ReadAllBytes(file.FullName);
        var profile = HmsOriginalFingerprint.Identify(bytes) ?? throw new InvalidOperationException("Unsupported HMS binary fingerprint");
        if (!ReferenceEquals(ReadField(local, "instance"), instance) || target.ManifestModule.ModuleVersionId != profile.Mvid
            || target.GetName().Version != profile.Version)
            throw new InvalidOperationException("Loaded HMS identity differs from the audited file");

        var root = target.GetType("HMSync.HMSyncPlugin", true)!;
        object Field(string name, string typeName)
        {
            var field = root.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.FieldType.FullName != typeName || field.GetValue(instance) is not { } value)
                throw new InvalidOperationException("HMS field changed: " + name);
            return value;
        }
        var zone = Field("zoneLoad", "HMSync.Services.ZoneLoadService");
        var ownRelay = Field("relay", "HMSync.Sync.RelaySyncService");
        var ownVisibility = Field("actorVisibility", "HMSync.Services.ActorVisibilityService");
        if (ownVisibility.GetType().GetField("active", BindingFlags.NonPublic | BindingFlags.Instance)?.FieldType != typeof(bool))
            throw new InvalidOperationException("HMS visibility state changed");
        var signal = zone.GetType().GetEvent("ZoneWillChange", BindingFlags.Public | BindingFlags.Instance);
        if (signal?.EventHandlerType != typeof(Action) || signal.AddMethod == null || signal.RemoveMethod == null)
            throw new InvalidOperationException("HMS public ZoneWillChange event changed");

        // These pure getters and event accessors are the only HMS code invoked here.
        // Check their live IL against the fingerprinted file; no HMS method is rewritten.
        foreach (var property in new[] { "IsTransitioning", "HomeRestoreArmed", "IsZoneLoaded", "CurrentLoadedZone" })
            VerifyGetter(bytes, zone, property, property == "CurrentLoadedZone" ? typeof(uint) : typeof(bool));
        VerifyGetter(bytes, ownRelay, "IsSessionActive", typeof(bool));
        HmsOriginalFingerprint.VerifyLoadedMethod(bytes, signal.AddMethod);
        HmsOriginalFingerprint.VerifyLoadedMethod(bytes, signal.RemoveMethod);
        var firstLifecycle = ReadLifecycle(zone, ownRelay);
        var firstBody = readOriginalBody();
        Action handler = () => OnZoneWillChange(zone);
        runtime.BeforeHmsLifecycle();
        assembly = target;
        plugin = instance;
        zoneLoad = zone;
        relay = ownRelay;
        visibility = ownVisibility;
        zoneEvent = signal;
        zoneHandler = handler;
        lifecycle = firstLifecycle;
        originalBody = firstBody;
        fault = null;
        try { signal.AddEventHandler(zone, handler); }
        catch { Detach(); throw; }
        log.Information("[LocalPreview] Original HMS observer attached: {Profile}, {Mvid}", profile.Name, profile.Mvid);
    }

    private HmsCoexistenceDecision Poll()
    {
        if (fault != null) return new(false, fault);
        if (zoneLoad == null || relay == null) return new(false, "HMS 兼容监测尚未就绪。");
        var nextLifecycle = ReadLifecycle(zoneLoad, relay);
        var nextBody = readOriginalBody();
        var visibilityActive = visibility != null && ReadField(visibility, "active") is true;
        runtime.SetHmsVisibilityProtection(visibilityActive && !nextLifecycle.Transitioning && !nextLifecycle.Restoring);
        // This catches persistent state changes after HMS operations. Synchronous native
        // dispatch and same-ID replay are deliberately handled by the native runtime.
        if (runtime.HasActivePreview && (lifecycle != nextLifecycle || originalBody != nextBody))
            runtime.BeforeHmsLifecycle();
        lifecycle = nextLifecycle;
        originalBody = nextBody;
        if (nextLifecycle.Transitioning || nextLifecycle.Restoring)
        {
            if (runtime.HasActivePreview) runtime.BeforeHmsLifecycle();
            return new(false, "HMS 正在切换或恢复场景，请稍后再试。");
        }
        return new(true, "");
    }

    private void OnZoneWillChange(object owner)
    {
        if (disposed || !ReferenceEquals(zoneLoad, owner)) return;
        try { runtime.SetHmsVisibilityProtection(false); runtime.BeforeHmsLifecycle(); originalBody = null; }
        catch (Exception exception) { Fail(exception); }
    }

    private static void VerifyGetter(byte[] bytes, object owner, string name, Type expected)
    {
        var property = owner.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        if (property?.PropertyType != expected || property.GetMethod == null)
            throw new InvalidOperationException("HMS lifecycle property changed: " + name);
        HmsOriginalFingerprint.VerifyLoadedMethod(bytes, property.GetMethod);
    }

    private static HmsLifecycleState ReadLifecycle(object zone, object ownRelay)
        => new(Read<bool>(ownRelay, "IsSessionActive"), Read<bool>(zone, "IsZoneLoaded"),
            Read<uint>(zone, "CurrentLoadedZone"), Read<bool>(zone, "IsTransitioning"), Read<bool>(zone, "HomeRestoreArmed"));

    private static T Read<T>(object owner, string name)
        => owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(owner) is T value
            ? value : throw new InvalidOperationException("HMS lifecycle value unavailable: " + name);

    private void Fail(Exception exception)
    {
        fault = "HMS 兼容监测已停止：" + exception.GetBaseException().Message;
        runtime.SetHmsVisibilityProtection(false);
        try { log.Error(exception, "[LocalPreview] Original HMS observer failed"); } catch { }
        try { runtime.BeforeHmsLifecycle(); } catch { }
    }

    private void Detach()
    {
        runtime.SetHmsVisibilityProtection(false);
        if (zoneEvent != null && zoneLoad != null && zoneHandler != null)
        {
            try { zoneEvent.RemoveEventHandler(zoneLoad, zoneHandler); }
            catch (Exception exception) { try { log.Error(exception, "[LocalPreview] HMS event detach failed"); } catch { } }
        }
        if (assembly != null) { try { runtime.BeforeHmsLifecycle(); } catch { } }
        zoneEvent = null;
        zoneHandler = null;
        zoneLoad = null;
        relay = null;
        visibility = null;
        plugin = null;
        assembly = null;
        lifecycle = null;
        originalBody = null;
        fault = null;
    }

    private static object? FindInstance(object exposed)
        => FindLocalPlugin(exposed) is { } local ? ReadField(local, "instance") : null;
    private static object? FindLocalPlugin(object exposed) => ReadField(exposed, "plugin") ?? ReadField(exposed, "<plugin>P");
    private static object? ReadField(object value, string name)
    {
        for (var type = value.GetType(); type != null; type = type.BaseType)
            if (type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly) is { } field)
                return field.GetValue(value);
        return null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Detach();
        rejected = null;
    }

    private readonly record struct HmsLifecycleState(bool Session, bool Loaded, uint Zone, bool Transitioning, bool Restoring);
}
