using System.Reflection;
using System.Runtime.CompilerServices;

internal static class ExceptionalFeatureRetentionCases
{
    internal static int Run(Assembly assembly)
    {
        var retained = Begin(assembly);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        if (retained.Drain.IsCompleted || !retained.Owner.IsAlive)
            throw new InvalidOperationException("an exceptional pre-seal Feature drain lost its owner while physical release remains unknown");
        GC.KeepAlive(retained.Drain);
        Console.WriteLine("PASS actual Feature exceptional drain alone retains the unsealed Feature owner");
        return 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task Drain, WeakReference Owner) Begin(Assembly assembly)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = assembly.GetType("FireGaze.PortraitPreview.PortraitPreviewFeature", true)!;
        // Native close was interrupted before its seal. Exercise the real public drain
        // method without constructing native services or registering any game callbacks.
        var feature = RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("incompletePhysicalClose", fields)!.SetValue(feature,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var method = type.GetMethod("DrainPhysicalAsync")!;
        var drain = (Task)method.Invoke(feature, null)!;
        if (!ReferenceEquals(drain, method.Invoke(feature, null)))
            throw new InvalidOperationException("exceptional physical drain is not stable across repeated host reads");
        return (drain, new WeakReference(feature));
    }
}
