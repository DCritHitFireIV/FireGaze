#if HAS_PHOTO_TRACE
using System.Collections.Concurrent;
using FireGaze.PortraitPreview;

internal static class PhotoTraceCases
{
    public static async Task Run(Action<bool, string> check)
    {
        var lines = new ConcurrentQueue<string>();
        var operation = new PortraitPhotoTrace(lines.Enqueue).Start(PortraitPhotoOperation.HistoryCapture);
        var gpu = new FakeGpu { PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously),
            PendingUpload = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var source = new Texture(1, 1, [1, 2, 3, 255]);
        var pending = gpu.Compositor.ComposeAsync(1, 1, [new(source)], default, operation);
        check(!pending.IsCompleted && lines.Any(x => x.Contains("begin LayerReadback"))
            && lines.Any(x => x.Contains("task-returned LayerReadback") && x.Contains("status=Waiting"))
            && !lines.Any(x => x.Contains("end LayerReadback")),
            "production compositor logs raw API begin and returned task before physical readback completes");
        gpu.PendingRead.SetResult((source.Specification, source.Bytes));
        for (var i = 0; i < 3000 && !lines.Any(x => x.Contains("task-returned Upload")); i++) await Task.Delay(1);
        check(!pending.IsCompleted && lines.Any(x => x.Contains("end LayerReadback"))
            && lines.Any(x => x.Contains("end CpuComposite")) && lines.Any(x => x.Contains("end CpuEncode"))
            && lines.Any(x => x.Contains("task-returned Upload")) && !lines.Any(x => x.Contains("end Upload")),
            "production upload remains distinguishable from completed CPU composition while pending");
        gpu.PendingUpload.SetResult(new Texture(1, 1, [1, 2, 3, 255]));
        using var output = await pending;
        check(lines.Any(x => x.Contains("end Upload")) && source.Shares.Single().Disposes == 1,
            "physical upload completion has a terminal trace and preserves production resource release");

        lines.Clear();
        gpu = new FakeGpu { ReadFailure = new IOException("private texture payload path") };
        try { await gpu.Compositor.ComposeAsync(1, 1, [new(new Texture(1, 1, [1, 2, 3, 255]))], default,
            new PortraitPhotoTrace(lines.Enqueue).Start(PortraitPhotoOperation.HistoryCapture)); }
        catch (IOException) { }
        check(lines.Any(x => x.Contains("fail LayerReadback") && x.Contains("IOException"))
            && lines.All(x => !x.Contains("private texture")),
            "production readback failure logs its type without image payload or exception message");
    }
}
#endif
