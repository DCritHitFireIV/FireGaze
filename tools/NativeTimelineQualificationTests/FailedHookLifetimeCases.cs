using System.Runtime.CompilerServices;
using FireGaze.LocalPreview.Runtime;

internal static unsafe partial class Program
{
    private static IEnumerable<(string Name, System.Action Run)> FailedHookLifetimeCases()
    {
        yield return ("successful native hook teardown does not retain the old runtime", () => {
            var weak = ReleaseRuntime(false, false);
            Collect();
            Check(!weak.Owner.IsAlive && !weak.Hook.IsAlive, "the fixture must not accidentally root a successfully disposed owner or hook");
        });
        foreach (var constructionFailed in new[] { false, true })
        {
            var constructor = constructionFailed;
            yield return ($"{(constructor ? "constructor rollback" : "runtime unload")} failed native teardown keeps Hook and owner rooted after collection", () => {
                var weak = ReleaseRuntime(true, constructor);
                Collect();
                Check(weak.Hook.IsAlive, "a backend that failed disposal may still have a native callback; keep its Hook rooted");
                Check(weak.Owner.IsAlive, "the Original holder and callback owner must survive even when the plugin releases runtime");
                var hook = weak.Hook.Target!;
                var attempts = HookBackendDouble.Get(hook).Disposals;
                var owner = (ActorPreviewRuntime)weak.Owner.Target!;
                owner.Dispose();
                Check(HookBackendDouble.Get(hook).Disposals == attempts, "do not blindly retry an uncertain failed backend teardown");
            });
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Owner, WeakReference Hook) ReleaseRuntime(bool failDispose, bool constructionFailed)
    {
        var interop = new TimelineHookInstallationTests.RecordingInterop { FailAllDispose = failDispose, ThrowAtRecord = constructionFailed ? 2 : 0 };
        var runtime = TimelineHookInstallationTests.Create(interop);
        var first = interop.Hooks[0];
        runtime.Dispose();
        return (new WeakReference(runtime), new WeakReference(first));
    }
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}
