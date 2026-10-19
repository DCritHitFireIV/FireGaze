using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.PortraitPreview;

internal static class CapturePreparationTests
{
    // A regression here would publish native layers before safe asynchronous flattening, or
    // release a source while readback still owns it. Only the physical GPU boundary is replaced.
    public static async Task<int> Run()
    {
        var source = new Source();
        var textures = new Textures();
        using var api = new PortraitLiveCaptureApi(source, textures);
        var pending = api.CaptureAsync(source.Sample);
        if (textures.Prepares != 1 || pending.IsCompleted)
            throw new InvalidOperationException("Capture must await safe asynchronous composition before publishing any layers.");
        if (source.Overlay.Disposes != 0 || textures.Copy.Disposes != 0)
            throw new InvalidOperationException("Composition inputs must remain alive while physical composition is pending.");
        var flattened = new CountingCaptureTexture(2, 1);
        textures.Prepared.SetResult(flattened);
        using (var result = await pending)
        {
            if (result is not { RequiresComposition: false, Layers.Count: 1 } || result.Layers[0].Texture != flattened)
                throw new InvalidOperationException("The public capture must contain one already flattened independent image.");
            if (source.Overlay.Disposes != 1 || textures.Copy.Disposes != 1 || flattened.Disposes != 0)
                throw new InvalidOperationException("After composition only the published image remains owned.");
            using var drawn = api.Compose(result);
            if (flattened.Shares.Count != 1)
                throw new InvalidOperationException("Draw must only lease an already prepared image, without invoking a renderer.");
        }
        if (flattened.Disposes != 1) throw new InvalidOperationException("The prepared image must be disposed once.");
        return 6;
    }

    private sealed class Source : IPortraitLiveCaptureSource
    {
        public readonly CountingCaptureTexture Character = new(2, 1), Overlay = new(2, 1);
        public readonly PortraitCaptureSample Sample = new(new(1, 2), new(3, 4, 5, 6, 7), "safe",
            PortraitImageScope.Portrait, new(7, Vector2.Zero, new(1920, 1080)));
        public bool IsFrameworkThread => true;
        public bool TrySample(PortraitImageScope scope, PortraitCaptureViewport viewport, out PortraitCaptureSample? sample)
        { sample = Sample; return true; }
        public bool TryAcquirePortrait(PortraitCaptureSample sample, out PortraitLiveCaptureInputs? inputs)
        { inputs = new(new(Character, new() { NewWidth = 2, NewHeight = 1 }), [new(Overlay)], new(2, 1)); return true; }
        public void Dispose() { }
    }

    private sealed class Textures : IPortraitCaptureTextures
    {
        public int Prepares;
        public readonly CountingCaptureTexture Copy = new(2, 1);
        public readonly TaskCompletionSource<IDalamudTextureWrap> Prepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IDalamudTextureWrap> CopyAsync(IDalamudTextureWrap source, TextureModificationArgs args) => Task.FromResult<IDalamudTextureWrap>(Copy);
        public Task<IDalamudTextureWrap> CaptureViewportAsync(ImGuiViewportTextureArgs args) => throw new InvalidOperationException();
        public IDalamudTextureWrap Compose(PortraitCaptureLayers layers) => throw new InvalidOperationException("Unsafe Draw composition was invoked.");
        public Task<IDalamudTextureWrap> ComposeAsync(PortraitCaptureLayers layers) { Prepares++; return Prepared.Task; }
    }
}
