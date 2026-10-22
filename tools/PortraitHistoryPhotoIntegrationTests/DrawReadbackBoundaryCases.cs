using System.Reflection;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal static class DrawReadbackBoundaryCases
{
    internal static async Task<int> RunAsync(string root, PortraitHistoryStore history,
        PortraitHistoryKey key, PortraitHistoryRecord record, PortraitEditorSnapshot snapshot,
        PortraitIdentity identity)
    {
        var source = new PortraitThumbnailSource();
        var textures = DispatchProxy.Create<ITextureProvider, TracePhotoProvider>();
        var readback = DispatchProxy.Create<ITextureReadbackProvider, BlockingReadbackProxy>();
        var blocking = (BlockingReadbackProxy)(object)readback;
        using var photos = new PortraitHistoryPhotos(Path.Combine(root, "draw-boundary.bin"),
            textures, readback, source, history.Bytes);
        photos.SetRecords(history.GetRecords(key), history.Bytes);
        photos.Poll("draw-boundary-owner", identity, record.ID, snapshot, true);
        await Task.Delay(250);
        photos.Poll("draw-boundary-owner", identity, record.ID, snapshot, true);
        source.Complete(new(new FakeTexture()));
        photos.Poll("draw-boundary-owner", identity, record.ID, snapshot, true);

        var draw = Task.Run(() => photos.Draw());
        if (!blocking.Entered.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("readback synchronous-prefix fixture was not entered");

        var completedWithoutRelease = draw.Wait(TimeSpan.FromMilliseconds(250));
        blocking.Release.Set();
        await draw;
        if (!completedWithoutRelease)
            throw new InvalidOperationException(
                "Framework Draw entered the official readback synchronous prefix before returning");

        Console.WriteLine("PASS Framework Draw returns while history readback synchronous prefix remains blocked");
        return 1;
    }
}

public class BlockingReadbackProxy : DispatchProxy
{
    public readonly ManualResetEventSlim Entered = new(false);
    public readonly ManualResetEventSlim Release = new(false);

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(ITextureReadbackProvider.GetRawImageAsync))
        {
            Entered.Set();
            Release.Wait();
            var bytes = new byte[160 * 256 * 4];
            for (var index = 3; index < bytes.Length; index += 4) bytes[index] = 255;
            return Task.FromResult((new RawImageSpecification(160, 256, 28, 160 * 4), bytes));
        }
        if (method?.Name == nameof(ITextureReadbackProvider.SaveToStreamAsync))
        {
            ((Stream)args![2]!).Write([137, 80, 78, 71]);
            return Task.CompletedTask;
        }
        throw new InvalidOperationException("Unexpected readback call in Draw boundary fixture: " + method?.Name);
    }
}
