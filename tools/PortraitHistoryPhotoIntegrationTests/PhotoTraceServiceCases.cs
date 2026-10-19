using System.Collections.Concurrent;
using System.Reflection;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal static class PhotoTraceServiceCases
{
    internal static async Task<int> RunAsync(string root, PortraitHistoryStore history,
        PortraitHistoryKey key, PortraitHistoryRecord record, PortraitEditorSnapshot snapshot, PortraitIdentity identity)
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++; Console.WriteLine("PASS " + name);
        }
        var lines = new ConcurrentQueue<string>();
        var provider = DispatchProxy.Create<ITextureProvider, TracePhotoProvider>();
        var gpu = (TracePhotoProvider)(object)provider;
        var readback = DispatchProxy.Create<ITextureReadbackProvider, TracePhotoReadback>();
        var probe = (TracePhotoReadback)(object)readback;
        probe.BeforeCall = phase => Check(lines.Any(x => x.Contains("begin " + phase))
            && !lines.Any(x => x.Contains("task-returned " + phase)),
            "trace is present before the synchronous official " + phase + " prefix");
        var source = new PortraitThumbnailSource();
        using var photos = new PortraitHistoryPhotos(Path.Combine(root, "traced-pipeline.bin"), provider, readback,
            source, history.Bytes, lines.Enqueue);
        photos.SetRecords(history.GetRecords(key), history.Bytes);
        photos.Poll("traced", identity, record.ID, snapshot, true);
        await Task.Delay(250);
        photos.Poll("traced", identity, record.ID, snapshot, true);
        source.Complete(new(new FakeTexture()));
        photos.Poll("traced", identity, record.ID, snapshot, true);
        photos.Draw();
        Check(lines.Any(x => x.Contains("task-returned LayerReadback"))
            && !lines.Any(x => x.Contains("end LayerReadback")),
            "real Draw publishes the pending first raw readback boundary without waiting for it");
        probe.Raw.SetResult(probe.Image());
        await Wait(() => lines.Any(x => x.Contains("task-returned VisibleReadback")));
        Check(lines.Any(x => x.Contains("task-returned VisibleReadback"))
            && !lines.Any(x => x.Contains("end VisibleReadback")),
            "real photo Encode distinguishes pending visibility readback from composition");
        probe.Visible.SetResult(probe.Image());
        await Wait(() => lines.Any(x => x.Contains("task-returned PngEncode")));
        Check(lines.Any(x => x.Contains("task-returned PngEncode"))
            && !lines.Any(x => x.Contains("end PngEncode")),
            "real PNG encoder task remains observable while physical encoding is pending");
        probe.Stream!.Write(new EncodingReadbackProxy().Png);
        probe.Png.SetResult();
        for (var i = 0; i < 3000 && Count(photos, "saved") == 0; i++)
        {
            photos.Poll("traced", identity, record.ID, snapshot, true);
            await Task.Delay(1);
        }
        Check(Count(photos, "saved") == 1 && lines.Any(x => x.Contains("end PngEncode"))
            && lines.Any(x => x.Contains("end CacheWrite")), "same operation reaches durable cache completion");
        var ids = lines.Select(x => x.Split("op=")[1].Split(' ')[0]).Distinct().ToArray();
        Check(ids.Length == 1 && lines.Count <= PortraitPhotoTrace.MaximumLinesPerOperation
            && lines.All(x => !x.Contains("traced-pipeline") && !x.Contains("owner")),
            "one bounded operation id correlates the pipeline without paths or player data");
        gpu.BeforeImage = () => Check(lines.Any(x => x.Contains("begin LoadedImageUpload"))
            && !lines.Any(x => x.Contains("task-returned LoadedImageUpload")),
            "cached image upload trace precedes its synchronous official service prefix");
        Check(photos.Get(record.ID) is null, "loading a saved image remains demand driven and nonblocking");
        await Wait(() => lines.Any(x => x.Contains("task-returned LoadedImageUpload")));
        Check(lines.Any(x => x.Contains("end LoadedImageRead"))
            && !lines.Any(x => x.Contains("end LoadedImageUpload")),
            "cached-image read and pending decoded texture upload have separate phases");
        gpu.Image.SetResult(new SharingTexture());
        await Wait(() => Count(photos, "loaded") == 1);
        Check(lines.Any(x => x.Contains("end LoadedImageUpload")) && photos.Get(record.ID) is not null,
            "completed cached-image upload logs its terminal phase before becoming available");
        return checks;
    }

    private static long Count(PortraitHistoryPhotos photos, string name)
        => Convert.ToInt64(photos.Diagnostics().GetType().GetProperty(name)!.GetValue(photos.Diagnostics()));
    private static async Task Wait(Func<bool> ready)
    {
        for (var i = 0; i < 3000 && !ready(); i++) await Task.Delay(1);
        if (!ready()) throw new InvalidOperationException("Trace boundary did not settle.");
    }
}

public class TracePhotoProvider : DispatchProxy
{
    public readonly TaskCompletionSource<IDalamudTextureWrap> Image = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action? BeforeImage;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(ITextureProvider.CreateFromRawAsync))
            return Task.FromResult<IDalamudTextureWrap>(new SharingTexture());
        if (method?.Name == nameof(ITextureProvider.CreateFromImageAsync))
        {
            BeforeImage?.Invoke();
            return Image.Task;
        }
        throw new InvalidOperationException("Unexpected traced image provider operation.");
    }
}

public class TracePhotoReadback : DispatchProxy
{
    public readonly TaskCompletionSource<(RawImageSpecification, byte[])> Raw = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource<(RawImageSpecification, byte[])> Visible = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Png = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action<string>? BeforeCall;
    public int RawCalls;
    public Stream? Stream;
    public (RawImageSpecification, byte[]) Image()
    {
        var pixels = new byte[160 * 256 * 4];
        for (var at = 3; at < pixels.Length; at += 4) pixels[at] = 255;
        return (new(160, 256, 28, 160 * 4), pixels);
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(ITextureReadbackProvider.GetRawImageAsync))
        {
            RawCalls++;
            BeforeCall?.Invoke(RawCalls == 1 ? "LayerReadback" : "VisibleReadback");
            return RawCalls == 1 ? Raw.Task : Visible.Task;
        }
        if (method?.Name == nameof(ITextureReadbackProvider.SaveToStreamAsync))
        {
            BeforeCall?.Invoke("PngEncode");
            Stream = (Stream)args![2]!;
            return Png.Task;
        }
        throw new InvalidOperationException("Unexpected photo trace boundary.");
    }
}
