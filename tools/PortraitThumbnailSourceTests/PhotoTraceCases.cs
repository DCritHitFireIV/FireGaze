#if HAS_PHOTO_TRACE
using FireGaze.PortraitPreview;

internal static class PhotoTraceCases
{
    public static async Task Run(PortraitIdentity identity, Action<bool, string> check)
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var operation = new PortraitPhotoTrace(lines.Enqueue).Start(PortraitPhotoOperation.HistoryCapture);
        var api = new FakeApi { DelayCopies = true };
        api.BeforeCopy = () => check(lines.Count(x => x.Contains("begin SourceCopy")) == api.Copies + 1
            && lines.Count(x => x.Contains("task-returned SourceCopy")) == api.Copies,
            "source trace precedes the synchronous CopyAsync service prefix");
        var pending = new PortraitThumbnailSource(api).CaptureAsync(identity, default, operation);
        check(!pending.IsCompleted && lines.Any(x => x.Contains("begin SourceCopy"))
            && lines.Any(x => x.Contains("task-returned SourceCopy"))
            && !lines.Any(x => x.Contains("end SourceCopy")),
            "source trace distinguishes a returned pending copy task from its physical completion");
        api.CompleteCopies();
        using var result = await pending;
        check(lines.Any(x => x.Contains("end SourceAcquire")) && lines.Any(x => x.Contains("end SourceCopy"))
            && lines.All(x => !x.Contains("0x") && !x.Contains("identity")),
            "completed production source phases log terminal records without native addresses");
        check(new[] { 0, 1, 2 }.All(layer => lines.Count(x => x.StartsWith("begin SourceCopy") && x.Contains(" layer=" + layer + " ")) == 1
            && lines.Count(x => x.StartsWith("task-returned SourceCopy") && x.Contains(" layer=" + layer + " ")) == 1
            && lines.Count(x => x.StartsWith("end SourceCopy") && x.Contains(" layer=" + layer + " ")) == 1),
            "each normalized layer copy has its own begin, task-returned and terminal phase");

        lines.Clear();
        var failure = new FakeApi { FailCopy = 1 };
        try { await new PortraitThumbnailSource(failure).CaptureAsync(identity, default,
            new PortraitPhotoTrace(lines.Enqueue).Start(PortraitPhotoOperation.HistoryCapture)); }
        catch (InvalidOperationException) { }
        check(lines.Any(x => x.Contains("fail SourceCopy") && x.Contains("InvalidOperationException"))
            && lines.All(x => !x.Contains("copy failure")) && failure.Inputs.All(x => x.Disposes == 1),
            "source failure trace preserves ownership and excludes exception messages");

        var silentFailure = new FakeApi();
        using var silentResult = await new PortraitThumbnailSource(silentFailure).CaptureAsync(identity, default,
            new PortraitPhotoTrace(_ => throw new IOException("logger path must never escape"))
                .Start(PortraitPhotoOperation.HistoryCapture));
        check(silentResult is not null && silentFailure.Copies == 3,
            "source tracing logger failure cannot change native acquisition or copy scheduling");

        lines.Clear();
        operation = new PortraitPhotoTrace(lines.Enqueue).Start(PortraitPhotoOperation.HistoryCapture);
        var calls = 0;
        for (var i = 0; i < 100; i++)
            await operation.CallAsync(PortraitPhotoPhase.SourceCopy, () => Task.FromResult(++calls));
        check(calls == 100 && lines.Count <= PortraitPhotoTrace.MaximumLinesPerOperation
            && lines.Count(x => x.StartsWith("begin ")) == lines.Count(x => x.StartsWith("end "))
            && lines.Count(x => x.StartsWith("begin ")) == lines.Count(x => x.StartsWith("task-returned ")),
            "hard operation budget preserves all admitted async terminal records without throttling the operation");

        lines.Clear();
        operation = new PortraitPhotoTrace(lines.Enqueue).Start(PortraitPhotoOperation.HistoryCapture);
        try { await operation.CallAsync<int>(PortraitPhotoPhase.SourceCopy,
            () => throw new IOException("private exception path must stay private")); }
        catch (IOException) { }
        check(lines.Count == 2 && lines.First().StartsWith("begin SourceCopy")
            && lines.Last().StartsWith("fail SourceCopy") && lines.All(x => !x.Contains("private")),
            "synchronous service failure leaves begin and fail without inventing a returned task");
    }
}
#endif
