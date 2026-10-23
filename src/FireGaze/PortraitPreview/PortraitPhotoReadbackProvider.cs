using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Narrow photo-only adapter. Every raw read, including PNG output, uses the same bounded
/// nonblocking acquisition. Unsupported transformations never fall back to SDK readback.
/// </summary>
internal sealed class PortraitPhotoReadbackProvider(
    Func<IDalamudTextureWrap, CancellationToken, Task<(RawImageSpecification Specification, byte[] RawData)>> acquire)
    : ITextureReadbackProvider
{
    private static readonly Guid Png = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");

    // The callback is invoked before returning, so it must synchronously retain an independent
    // input reference. An async prefix here would allow the caller to release the source too soon.
    public Task<(RawImageSpecification Specification, byte[] RawData)> GetRawImageAsync(
        IDalamudTextureWrap wrap, TextureModificationArgs args = default, bool leaveWrapOpen = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wrap);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (args.MakeOpaque || args.DxgiFormat != 0 || args.NewWidth != 0 || args.NewHeight != 0
                || args.Uv0 != Vector2.Zero || args.Uv1 != Vector2.Zero && args.Uv1 != Vector2.One)
                throw new NotSupportedException("Portrait pixel acquisition only accepts the complete prepared texture.");
            var pending = acquire(wrap, cancellationToken)
                ?? throw new InvalidOperationException("Pixel acquisition returned no task.");
            return CompleteRead(pending, wrap, leaveWrapOpen, cancellationToken);
        }
        catch (Exception error)
        {
            if (!leaveWrapOpen) wrap.Dispose();
            return Task.FromException<(RawImageSpecification, byte[])>(error);
        }
    }

    private static async Task<(RawImageSpecification, byte[])> CompleteRead(
        Task<(RawImageSpecification, byte[])> pending, IDalamudTextureWrap wrap,
        bool leaveWrapOpen, CancellationToken token)
    {
        try
        {
            var result = await pending.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally { if (!leaveWrapOpen) wrap.Dispose(); }
    }

    public Task SaveToStreamAsync(IDalamudTextureWrap wrap, Guid containerGUID, Stream stream,
        IReadOnlyDictionary<string, object>? props = null, bool leaveWrapOpen = false,
        bool leaveStreamOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wrap);
        ArgumentNullException.ThrowIfNull(stream);
        Task<(RawImageSpecification Specification, byte[] RawData)> pending;
        try
        {
            if (containerGUID != Png || props is { Count: > 0 })
                throw new NotSupportedException("Portrait photos use the CPU PNG encoder without codec options.");
            // CompleteEncoding owns the original wrapper; acquisition owns its independent lease.
            pending = GetRawImageAsync(wrap, leaveWrapOpen: true, cancellationToken: cancellationToken);
        }
        catch (Exception error) { pending = Task.FromException<(RawImageSpecification, byte[])>(error); }
        return CompleteEncoding(pending, wrap, stream, leaveWrapOpen, leaveStreamOpen, cancellationToken);
    }

    private static async Task CompleteEncoding(Task<(RawImageSpecification Specification, byte[] RawData)> pending,
        IDalamudTextureWrap wrap, Stream stream, bool leaveWrapOpen, bool leaveStreamOpen, CancellationToken token)
    {
        try
        {
            var raw = await pending.ConfigureAwait(false);
            var png = await Task.Run(() => PortraitPngEncoder.Encode(raw.Specification, raw.RawData, token),
                token).ConfigureAwait(false);
            await stream.WriteAsync(png, token).ConfigureAwait(false);
        }
        finally
        {
            try { if (!leaveStreamOpen) await stream.DisposeAsync().ConfigureAwait(false); }
            finally { if (!leaveWrapOpen) wrap.Dispose(); }
        }
    }

    public Task<(int MipLevels, (RawImageSpecification Specification, byte[] RawData)[] Images)> GetAllRawImagesAsync(
        IDalamudTextureWrap wrap, bool leaveWrapOpen = false, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Portrait photos do not acquire arrays or mip chains.");
    public IEnumerable<IBitmapCodecInfo> GetSupportedImageEncoderInfos() => [];
    public Task SaveToFileAsync(IDalamudTextureWrap wrap, Guid containerGUID, string path,
        IReadOnlyDictionary<string, object>? props = null, bool leaveWrapOpen = false,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Portrait persistence owns its atomic file transaction.");
    public Task CopyToClipboardAsync(IDalamudTextureWrap wrap, string? preferredFileNameWithoutExtension = null,
        bool leaveWrapOpen = false, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Portrait photos do not write to the clipboard.");
}
