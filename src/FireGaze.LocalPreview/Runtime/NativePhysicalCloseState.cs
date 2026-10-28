using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Dalamud.Hooking;

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
    private static readonly ConditionalWeakTable<IDisposable, Attempt> Attempts = new();
    private sealed class Attempt { public bool Started, Complete; public Exception? Failure; }

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

    internal static bool TryDispose(IDisposable hook, object owner, out Exception? failure)
    {
        var attempt = Attempts.GetOrCreateValue(hook);
        lock (attempt)
        {
            if (attempt.Started)
            {
                failure = attempt.Failure ?? (attempt.Complete ? null : new InvalidOperationException("Native Hook teardown is already in progress."));
                return attempt.Complete;
            }
            // Latch before entering the backend, including reentrant Dispose calls.
            attempt.Started = true;
        }
        try
        {
            hook.Dispose();
            lock (attempt) { attempt.Complete = true; failure = null; }
            return true;
        }
        catch (Exception error)
        {
            lock (attempt) attempt.Failure = error;
            Retain(hook, owner);
            failure = error;
            return false;
        }
    }
}

// Every independent cleanup still runs. An uncertain close is terminal in this
// process and keeps the callback owner alive; later Dispose never retries it.
internal sealed class NativePhysicalCloseState(IDisposable owner)
{
    internal bool Complete { get; private set; }
    internal string? Failure { get; private set; }
    internal void Finish() => Complete = Failure is null;
    internal void Fail(string operation, Exception? error = null)
    {
        Complete = false;
        Failure ??= operation + (error is null ? "" : ": " + error.Message);
        NativeHookFailureRoots.Retain(owner, owner);
    }
    internal void Try(Action cleanup, string operation)
    {
        try { cleanup(); } catch (Exception error) { Fail(operation, error); }
    }
    internal bool CloseHook(IDisposable hook)
    {
        if (NativeHookFailureRoots.TryDispose(hook, owner, out var error)) return true;
        Fail("Native Hook physical close is incomplete", error);
        return false;
    }
    internal void CloseHook<T>(ref Hook<T>? hook) where T : Delegate
    {
        if (hook is not null && CloseHook(hook)) hook = null;
    }
}
