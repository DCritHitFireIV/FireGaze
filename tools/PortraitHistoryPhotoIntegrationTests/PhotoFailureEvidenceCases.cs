using System.Reflection;
using System.Runtime.CompilerServices;
using FireGaze.PortraitPreview;

internal static class PhotoFailureEvidenceCases
{
    internal static int Run()
    {
        // Losing this evidence on the next automatic retry hid the failing SDK
        // boundary in the .59 incident. Exercise the real trace across operations.
        var trace = new PortraitPhotoTrace();
        var failed = trace.Start(PortraitPhotoOperation.HistoryCapture);
        try { failed.Call<int>(PortraitPhotoPhase.LayerReadback, ThrowFromReadbackBoundary, 1); }
        catch (OverflowException) { }
        var original = failed.Snapshot.Single();
        var next = trace.Start(PortraitPhotoOperation.HistoryCapture);
        if (next.Call(PortraitPhotoPhase.SourceAcquire, () => 7) != 7)
            throw new InvalidOperationException("evidence collection changed the operation result");

        var property = typeof(PortraitPhotoTrace).GetProperty("LastFailure");
        var retained = property?.GetValue(trace) as PortraitPhotoStageSnapshot;
        if (retained is null || retained.OperationID != original.OperationID
            || retained.Phase != "LayerReadback" || retained.Layer != 1
            || retained.ErrorType != nameof(OverflowException))
            throw new InvalidOperationException("a successful retry must preserve the preceding failed readback evidence");
        Console.WriteLine("PASS later capture progress cannot overwrite the last failed operation/layer evidence");

        var frames = typeof(PortraitPhotoStageSnapshot).GetProperty("ErrorFrames")?.GetValue(retained) as string;
        if (frames is null || !frames.Contains("PhotoFailureEvidenceCases.ThrowFromReadbackBoundary")
            || frames.Contains("private-player") || frames.Contains("APIKEY") || frames.Contains(".cs:")
            || frames.Length > 2048)
            throw new InvalidOperationException("failure evidence must identify bounded method frames without paths or exception messages");
        Console.WriteLine("PASS failed boundary retains bounded method provenance without exception messages or source paths");

        var poisoned = retained with { State = "caller-edited" };
        if (ReferenceEquals(poisoned, property!.GetValue(trace))
            || ((PortraitPhotoStageSnapshot)property.GetValue(trace)!).State == "caller-edited")
            throw new InvalidOperationException("diagnostic caller changed retained failure evidence");
        Console.WriteLine("PASS retained failure evidence is detached from diagnostic caller edits");
        return 3;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ThrowFromReadbackBoundary()
        => throw new OverflowException(@"C:\private-player\APIKEY-secret");
}
