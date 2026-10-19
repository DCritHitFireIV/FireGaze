using System.Reflection;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FireGaze.PortraitPreview;

internal static class PhotoDrawCompositionTests
{
    internal static async Task<int> RunAsync(string root, PortraitHistoryStore history,
        PortraitHistoryKey key, PortraitHistoryRecord record, PortraitEditorSnapshot snapshot, PortraitIdentity identity)
    {
        var provider = DispatchProxy.Create<ITextureProvider, SafePhotoProvider>();
        var readback = DispatchProxy.Create<ITextureReadbackProvider, SafePhotoReadback>();
        var gpu = (SafePhotoProvider)(object)provider;
        var source = new PortraitThumbnailSource();
        using var photos = new PortraitHistoryPhotos(Path.Combine(root, "safe-composition.bin"), provider, readback, source, history.Bytes);
        photos.SetRecords(history.GetRecords(key), history.Bytes);
        photos.Poll("safe-composition", identity, record.ID, snapshot, true);
        await Task.Delay(250);
        photos.Poll("safe-composition", identity, record.ID, snapshot, true);
        if (source.Calls != 1) throw new InvalidOperationException("A settled photo ticket must begin a genuine service capture.");
        var captured = new FakeTexture();
        source.Complete(new(captured));
        photos.Poll("safe-composition", identity, record.ID, snapshot, true);
        photos.Draw();
        for (var attempt = 0; attempt < 100 && Count(photos, "saved") == 0; attempt++)
        {
            photos.Poll("safe-composition", identity, record.ID, snapshot, true);
            await Task.Delay(10);
        }
        if (Count(photos, "saved") != 1 || gpu.DrawListCalls != 0 || gpu.Uploads != 1)
            throw new InvalidOperationException("History photos must save via safe pixel composition without invoking the offscreen draw-list renderer.");
        if (captured.Disposes != 1 || gpu.Output.Disposes != 1)
            throw new InvalidOperationException("Safe photo composition must release input and encoded image exactly once.");
        return 3;
    }

    private static long Count(PortraitHistoryPhotos photos, string name)
        => Convert.ToInt64(photos.Diagnostics().GetType().GetProperty(name)!.GetValue(photos.Diagnostics()));
}

public class SafePhotoProvider : DispatchProxy
{
    public int DrawListCalls, Uploads;
    public readonly SharingTexture Output = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(ITextureProvider.CreateDrawListTexture))
        { DrawListCalls++; throw new InvalidOperationException("The unsafe native renderer must not be used."); }
        if (method?.Name == nameof(ITextureProvider.CreateFromRawAsync))
        { Uploads++; return Task.FromResult<IDalamudTextureWrap>(Output); }
        throw new InvalidOperationException("Unexpected physical operation: " + method?.Name);
    }
}

public class SafePhotoReadback : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == nameof(ITextureReadbackProvider.GetRawImageAsync))
        {
            var pixels = new byte[160 * 256 * 4];
            for (var at = 3; at < pixels.Length; at += 4) pixels[at] = 255;
            return Task.FromResult((new RawImageSpecification(160, 256, 28, 160 * 4), pixels));
        }
        if (method?.Name == nameof(ITextureReadbackProvider.SaveToStreamAsync))
        {
            ((Stream)args![2]!).Write(new EncodingReadbackProxy().Png);
            return Task.CompletedTask;
        }
        throw new InvalidOperationException("Unexpected readback operation: " + method?.Name);
    }
}
