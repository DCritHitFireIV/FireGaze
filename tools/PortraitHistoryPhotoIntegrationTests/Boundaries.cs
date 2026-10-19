using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

public class NoGpuProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => throw new InvalidOperationException("This non-GPU integration test must not call " + targetMethod?.Name);
}

namespace FireGaze.PortraitPreview
{
    // The actual service, gate, persistence, texture cache and history store are linked unchanged.
    // This source boundary controls only Present-dependent copies; it never creates native pointers.
    internal sealed class PortraitThumbnailSource
    {
        public int Calls;
        public PortraitIdentity RequestedIdentity;
        public CancellationToken RequestedCancellation;
        private TaskCompletionSource<PortraitThumbnailLayers?> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PortraitThumbnailLayers?> CaptureAsync(PortraitIdentity identity, CancellationToken token,
            PortraitPhotoTrace.Operation? operation = null)
        {
            Calls++; RequestedIdentity = identity; RequestedCancellation = token; return pending.Task;
        }
        public void Complete(PortraitThumbnailLayers layers) => pending.SetResult(layers);
    }

    internal sealed class PortraitThumbnailLayers(FakeTexture character) : IDisposable
    {
        public IDalamudTextureWrap Character => character;
        public IDalamudTextureWrap? Frame => null;
        public IDalamudTextureWrap? Accent => null;
        public System.Numerics.Vector2 FrameUV0 => System.Numerics.Vector2.Zero;
        public System.Numerics.Vector2 FrameUV1 => System.Numerics.Vector2.One;
        public System.Numerics.Vector2 AccentUV0 => System.Numerics.Vector2.Zero;
        public System.Numerics.Vector2 AccentUV1 => System.Numerics.Vector2.One;
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) character.Dispose(); }
    }

    internal sealed class FakeTexture : IDalamudTextureWrap
    {
        private int disposes;
        public int Disposes => Volatile.Read(ref disposes);
        public ImTextureID Handle => new((nint)123);
        public int Width => 160;
        public int Height => 256;
        public IDalamudTextureWrap CreateWrapSharingLowLevelResource() => new FakeTexture();
        public void Dispose() => Interlocked.Increment(ref disposes);
    }
}
