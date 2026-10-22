using System.Reflection;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal static class PhotoProgressReadCases
{
    internal static async Task<int> RunAsync(string root, ITextureProvider textures, ITextureReadbackProvider readback)
    {
        var photos = new PortraitHistoryPhotos(Path.Combine(root, "progress-read.bin"), textures, readback,
            new PortraitThumbnailSource(), 0);
        using var loggerEntered = new ManualResetEventSlim();
        using var releaseLogger = new ManualResetEventSlim();
        using var syncEntered = new ManualResetEventSlim();
        using var releaseSync = new ManualResetEventSlim();
        var operation = new PortraitPhotoTrace(_ => {
            loggerEntered.Set();
            if (!releaseLogger.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("logger fixture was not released");
        }).Start(PortraitPhotoOperation.HistoryCapture);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(PortraitHistoryPhotos).GetField("photoOperation", fields)!.SetValue(photos, operation);
        var sync = typeof(PortraitHistoryPhotos).GetField("sync", fields)!.GetValue(photos)!;
        var blockedLog = Task.Run(() => operation.Call(PortraitPhotoPhase.LayerReadback, () => 7, 0));
        var blockedService = Task.Run(() => { lock (sync) {
            syncEntered.Set();
            if (!releaseSync.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("service fixture was not released");
        }});
        try
        {
            if (!loggerEntered.Wait(TimeSpan.FromSeconds(5)) || !syncEntered.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("progress fixture did not reach the two independent blocked prefixes");
            var method = typeof(PortraitHistoryPhotos).GetMethod("ReadCaptureProgress");
            var read = Task.Run(() => method?.Invoke(photos, null) as PortraitPhotoStageSnapshot[] ?? []);
            var result = await read.WaitAsync(TimeSpan.FromSeconds(1));
            if (result.Length != 1 || result[0].State != "logging-begin" || result[0].Phase != "LayerReadback")
                throw new InvalidOperationException("capture progress is unavailable while service and logger prefixes are blocked");
            if (result[0].OperationID <= 0 || result[0].Layer != 0)
                throw new InvalidOperationException("capture progress lost its exact operation/layer identity");
            result[0] = result[0] with { State = "caller-edited" };
            var independent = (PortraitPhotoStageSnapshot[])method!.Invoke(photos, null)!;
            if (independent[0].State != "logging-begin")
                throw new InvalidOperationException("a diagnostic caller can mutate the service's retained stage state");
            Console.WriteLine("PASS managed capture progress remains readable while both logger and photo-service prefixes are blocked");
            Console.WriteLine("PASS capture progress returns detached operation/layer values independent of caller mutation");
            return 2;
        }
        finally
        {
            releaseLogger.Set(); releaseSync.Set();
            await Task.WhenAll(blockedLog, blockedService).WaitAsync(TimeSpan.FromSeconds(5));
            await photos.BeginDispose().WaitAsync(TimeSpan.FromSeconds(5));
            await photos.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
