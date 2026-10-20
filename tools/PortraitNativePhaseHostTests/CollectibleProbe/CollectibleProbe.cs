using System.Reflection;

public static class CollectibleProbe
{
    public static Delegate Build(MethodInfo original, MethodInfo direct, bool retire)
    {
        var gatewayType = typeof(CollectibleProbe).Assembly.GetType("FireGaze.PortraitPreview.PortraitNativePhaseHostGateway");
        if (gatewayType is null) throw new InvalidOperationException("Gateway missing in actual collectible plugin fixture.");
        unsafe
        {
            var create = gatewayType.GetMethod("TryCreate", BindingFlags.Public | BindingFlags.Static)!;
            var arguments = create.GetParameters().Length == 5 ? new object?[] { 0, new[] { typeof(void*) }, (nint)0xF110001, null, null }
                : new object?[] { 0, new[] { typeof(void*) }, null, null };
            if (!(bool)create.Invoke(null, arguments)!) throw new InvalidOperationException((string?)arguments[^1]);
            var gateway = arguments[^2]!; var type = gateway.GetType(); var delegateType = (Type)type.GetProperty("DelegateType")!.GetValue(gateway)!;
            type.GetMethod("PublishOriginal")!.Invoke(gateway, [Delegate.CreateDelegate(delegateType, original)]);
            var state = new ObserverState(); Func<int, nint, uint, float, Action<bool>?> observer = state.Begin;
            type.GetMethod("SetObserver")!.Invoke(gateway, [observer]);
            if (retire)
            {
                type.GetMethod("DetachObserver")!.Invoke(gateway, null);
                if (!(bool)type.GetMethod("TryRetireTo")!.Invoke(gateway, [Delegate.CreateDelegate(delegateType, direct)])!)
                    throw new InvalidOperationException("Idle collectible fixture failed to retire.");
                type.GetMethod("ReleaseOwnership")?.Invoke(gateway, null);
            }
            return (Delegate)type.GetProperty("Detour")!.GetValue(gateway)!;
        }
    }

    private sealed class ObserverState
    {
        private int entries;
        public Action<bool> Begin(int phase, nint receiver, uint frame, float delta) { entries++; return _ => entries++; }
    }

    public static unsafe Delegate BuildResource(MethodInfo original, MethodInfo direct, bool retire)
    {
        var gatewayType = typeof(CollectibleProbe).Assembly.GetType("FireGaze.PortraitPreview.PortraitNativePhaseHostGateway")!;
        var create = gatewayType.GetMethod("TryCreateResource", BindingFlags.Public | BindingFlags.Static)!;
        object?[] args = [7, new[] { typeof(FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager*) }, typeof(byte), (nint)0xF110007, null, null];
        if (!(bool)create.Invoke(null, args)!) throw new InvalidOperationException((string?)args[^1]);
        var gateway = args[^2]!; var delegateType = (Type)gatewayType.GetProperty("DelegateType")!.GetValue(gateway)!;
        gatewayType.GetMethod("PublishOriginal")!.Invoke(gateway, [Delegate.CreateDelegate(delegateType, original)]);
        var state = new ResourceObserverState();
        Func<int, nint, Action<bool, byte>?> observer = state.Begin;
        gatewayType.GetMethod("SetResourceObserver")!.Invoke(gateway, [observer]);
        if (retire)
        {
            gatewayType.GetMethod("DetachObserver")!.Invoke(gateway, null);
            if (!(bool)gatewayType.GetMethod("TryRetireTo")!.Invoke(gateway, [Delegate.CreateDelegate(delegateType, direct)])!) throw new InvalidOperationException("Resource fixture did not retire.");
            gatewayType.GetMethod("ReleaseOwnership")!.Invoke(gateway, null);
        }
        return (Delegate)gatewayType.GetProperty("Detour")!.GetValue(gateway)!;
    }

    private sealed class ResourceObserverState
    {
        private int calls;
        public Action<bool, byte> Begin(int phase, nint receiver) { calls++; return (_, _) => calls++; }
    }
}
