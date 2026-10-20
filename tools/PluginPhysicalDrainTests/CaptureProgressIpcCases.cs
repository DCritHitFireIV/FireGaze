using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal static class CaptureProgressIpcCases
{
    internal static async Task<int> RunAsync(Assembly assembly)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        var featureType = assembly.GetType("FireGaze.PortraitPreview.PortraitPreviewFeature", true)!;
        var photoType = assembly.GetType("FireGaze.PortraitPreview.PortraitHistoryPhotos", true)!;
        var initialize = featureType.GetMethod("InitializeCaptureProgress", instance)
            ?? throw new InvalidOperationException("actual Feature has no independent managed capture-progress IPC");
        var operationType = assembly.GetType("FireGaze.PortraitPreview.PortraitPhotoTrace+Operation", true)!;
        var photoSync = new object();
        var photo = RuntimeHelpers.GetUninitializedObject(photoType);
        photoType.GetField("sync", instance)!.SetValue(photo, photoSync);
        photoType.GetField("photoOperation", instance)!.SetValue(photo,
            operationType.GetField("Silent", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        var feature = RuntimeHelpers.GetUninitializedObject(featureType);
        featureType.GetField("photos", instance)!.SetValue(feature, photo);
        var gateField = featureType.GetField("captureProgress", instance)!;
        var ipc = DispatchProxy.Create(gateField.FieldType, typeof(ProgressGateProxy));
        var gate = (ProgressGateProxy)ipc;
        var pluginField = featureType.GetField("pluginInterface", instance)!;
        var plugin = DispatchProxy.Create(pluginField.FieldType, typeof(ProgressPluginProxy));
        ((ProgressPluginProxy)plugin).Gate = ipc;
        pluginField.SetValue(feature, plugin);
        initialize.Invoke(feature, null);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var locked = Task.Run(() => { lock (photoSync) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); } });
        try
        {
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("photo-lock fixture did not enter");
            var json = await Task.Run(() => gate.Callback!()).WaitAsync(TimeSpan.FromSeconds(1));
            using var value = JsonDocument.Parse(json);
            if (value.RootElement.GetProperty("version").GetString() != assembly.GetName().Version!.ToString()
                || value.RootElement.GetProperty("mvid").GetGuid() != assembly.ManifestModule.ModuleVersionId
                || value.RootElement.GetProperty("stages").GetArrayLength() != 0)
                throw new InvalidOperationException("actual managed IPC did not return its own assembly identity and detached stages");
        }
        finally { release.Set(); await locked; }
        Console.WriteLine("PASS actual registered capture-progress callback works off Framework with native fields absent and photo lock held");
        try { featureType.GetMethod("Dispose")!.Invoke(feature, null); }
        catch (TargetInvocationException) { /* Native host deliberately absent: cleanup must still detach the managed callback. */ }
        if (gate.Callback is not null || gate.Unregisters != 1)
            throw new InvalidOperationException("native close failure skipped capture-progress IPC detachment");
        Console.WriteLine("PASS actual Feature detaches managed capture-progress callback even when native close throws");

        var failedFeature = RuntimeHelpers.GetUninitializedObject(featureType);
        var failedIpc = DispatchProxy.Create(gateField.FieldType, typeof(ProgressGateProxy));
        var failedGate = (ProgressGateProxy)failedIpc;
        failedGate.ThrowAfterRegistration = true;
        ((ProgressPluginProxy)plugin).Gate = failedIpc;
        pluginField.SetValue(failedFeature, plugin);
        try { initialize.Invoke(failedFeature, null); throw new InvalidOperationException("failed registration was not surfaced"); }
        catch (TargetInvocationException error) when (error.InnerException is IOException) { }
        if (failedGate.Callback is not null || failedGate.Unregisters != 1)
            throw new InvalidOperationException("failed partial IPC registration left a callback to an unconstructed Feature");
        Console.WriteLine("PASS failed partial capture-progress registration removes its callback");
        return 3;
    }
}

public class ProgressPluginProxy : DispatchProxy
{
    public object? Gate;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "GetIpcProvider" && args is ["FireGaze.PortraitPreview.CaptureProgress"]) return Gate;
        throw new InvalidOperationException("Unexpected native/plugin access from managed capture progress: " + method?.Name);
    }
}

public class ProgressGateProxy : DispatchProxy
{
    public Func<string>? Callback;
    public int Unregisters;
    public bool ThrowAfterRegistration;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "RegisterFunc")
        {
            Callback = (Func<string>)args![0]!;
            if (ThrowAfterRegistration) throw new IOException("partial registration");
            return null;
        }
        if (method?.Name == "UnregisterFunc") { Callback = null; Unregisters++; return null; }
        throw new InvalidOperationException("Unexpected progress IPC operation: " + method?.Name);
    }
}
