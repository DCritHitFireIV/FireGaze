using System.Runtime.InteropServices;

namespace FireGaze.LocalPreview.Runtime;

// A Dalamud backend removes its official tracked root before native teardown.
// If teardown throws, neither IsDisposed nor a weak plugin-service inventory
// proves that its callback stopped. A normal GCHandle roots both the Hook and
// its callback owner across collectible plugin-context unload. Deliberately do
// not retry an uncertain native teardown or release these roots in this process.
internal static class NativeHookFailureRoots
{
    private static readonly object Gate = new();
    private static readonly Dictionary<IDisposable, GCHandle> Roots = new(ReferenceEqualityComparer.Instance);

    internal static bool Contains(IDisposable hook)
    {
        lock (Gate) return Roots.ContainsKey(hook);
    }

    internal static void Retain(IDisposable hook, object owner)
    {
        lock (Gate)
        {
            if (!Roots.ContainsKey(hook)) Roots.Add(hook, GCHandle.Alloc(new object[] { hook, owner }, GCHandleType.Normal));
        }
    }
}

public sealed unsafe partial class ActorPreviewRuntime
{
    private bool TryDisposeNativeHook(IDisposable hook)
    {
        if (NativeHookFailureRoots.Contains(hook)) return false;
        try { hook.Dispose(); return true; }
        catch (Exception error)
        {
            NativeHookFailureRoots.Retain(hook, this);
            try { log.Warning(error, "[LocalPreview] Native Hook teardown failed; callback and Original owner retained until process exit without retry."); } catch { }
            return false;
        }
    }
}
