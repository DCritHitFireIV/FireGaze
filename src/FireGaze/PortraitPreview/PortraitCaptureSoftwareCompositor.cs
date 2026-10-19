using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Normalizes sources through public readback, composes their pixels on the CPU, then uploads RGBA
/// UNORM pixels. No draw list, ImGui context, native editor pointer, or private renderer API is used.
/// </summary>
internal sealed class PortraitCaptureSoftwareCompositor(ITextureProvider textures, ITextureReadbackProvider readback)
{
    private const int MaximumInputPixels = 16_777_216;
    private const int MaximumDimension = 16_384;
    private const int MaximumInputBytes = MaximumInputPixels * 4;
    private const int MaximumLayers = 3; // Portrait, frame and accent.
    private static readonly float[] SrgbToLinear = BuildSrgbTable();

    // Intentionally not async: acquire every independent resource reference before returning to a
    // caller which is allowed to dispose all input wrappers immediately, even while readback waits.
    public Task<IDalamudTextureWrap> ComposeAsync(int width, int height,
        IReadOnlyList<PortraitCaptureLayer> layers, CancellationToken token = default,
        PortraitPhotoTrace.Operation? operation = null)
    {
        operation ??= PortraitPhotoTrace.Operation.Silent;
        ArgumentNullException.ThrowIfNull(layers);
        token.ThrowIfCancellationRequested();
        if (!ValidDimensions(width, height, PortraitCaptureGeometry.MaximumPixels))
            throw new ArgumentOutOfRangeException(nameof(width), "Composition exceeds the bounded output size.");
        if (layers.Count > MaximumLayers)
            throw new ArgumentException("Composition exceeds the portrait layer budget.", nameof(layers));

        var owned = new List<OwnedLayer>(layers.Count);
        try
        {
            foreach (var layer in layers)
            {
                ArgumentNullException.ThrowIfNull(layer);
                ArgumentNullException.ThrowIfNull(layer.Texture);
                var sourceWidth = layer.Texture.Width;
                var sourceHeight = layer.Texture.Height;
                var placement = layer.Placement ?? new PortraitCaptureRect(0, 0, 1, 1);
                var uv0 = layer.UV0 ?? Vector2.Zero;
                var uv1 = layer.UV1 ?? Vector2.One;
                if (!ValidDimensions(sourceWidth, sourceHeight, MaximumInputPixels)
                    || !placement.IsValid || !Finite(uv0) || !Finite(uv1))
                    throw new ArgumentException("Invalid source size or layer mapping.", nameof(layers));
                token.ThrowIfCancellationRequested();
                var retained = operation.Call(PortraitPhotoPhase.RetainLayers,
                    layer.Texture.CreateWrapSharingLowLevelResource, owned.Count);
                if (ReferenceEquals(retained, layer.Texture))
                    throw new InvalidOperationException("A shared wrapper must have independent disposal ownership.");
                owned.Add(new(retained, sourceWidth, sourceHeight, placement, uv0, uv1, layer.Tint));
            }
            // Public readback can perform synchronous staging/Present submission before returning
            // its Task. Retain every input on the caller, then keep that synchronous prefix out of
            // Framework/Draw and any caller lock. Do not pass token to Task.Run: after ownership
            // transfers, even an already-cancelled operation must execute its release path.
            return Task.Run(() => ComposeOwnedAsync(width, height, owned, token, operation));
        }
        catch
        {
            Release(owned);
            throw;
        }
    }

    private async Task<IDalamudTextureWrap> ComposeOwnedAsync(int width, int height,
        IReadOnlyList<OwnedLayer> layers, CancellationToken token, PortraitPhotoTrace.Operation operation)
    {
        IDalamudTextureWrap? output = null;
        Exception? failure = null;
        try
        {
            // Only the bounded output has a decoded float canvas. A large source remains bytes and
            // is sampled directly; finish one layer before starting the next public GPU readback.
            var canvas = new float[checked(width * height * 4)];
            var index = 0;
            foreach (var layer in layers)
            {
                token.ThrowIfCancellationRequested();
                var raw = await operation.CallAsync(PortraitPhotoPhase.LayerReadback, () => readback.GetRawImageAsync(layer.Texture,
                    // Official format normalization handles compressed game UI textures. Keep
                    // full source UV/dimensions: placement, cropping and filtering happen below.
                    new TextureModificationArgs { DxgiFormat = 28, Uv0 = Vector2.Zero, Uv1 = Vector2.One },
                    leaveWrapOpen: true, cancellationToken: token), index).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                ValidateRaw(raw.Specification, raw.RawData, layer);
                await operation.CallAsync(PortraitPhotoPhase.CpuComposite, () => Task.Run(() => CompositeLayer(canvas, width, height, layer,
                    raw.Specification, raw.RawData, token), token), index).ConfigureAwait(false);
                index++;
            }
            var rgba = await operation.CallAsync(PortraitPhotoPhase.CpuEncode,
                () => Task.Run(() => Encode(canvas, token), token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            output = await operation.CallAsync(PortraitPhotoPhase.Upload,
                () => textures.CreateFromRawAsync(new RawImageSpecification(width, height, 28, width * 4),
                    rgba, debugName: "FireGaze portrait CPU composition", cancellationToken: token)).ConfigureAwait(false);
            // A provider may complete successfully after ignoring cancellation. Do not publish that
            // resource, and do not abandon its physical task (or release inputs) before completion.
            token.ThrowIfCancellationRequested();
            if (output is null || output.Width != width || output.Height != height)
                throw new InvalidOperationException("Portrait upload returned unexpected output dimensions.");
        }
        catch (Exception error)
        {
            failure = error;
        }

        // A cleanup failure must not replace the original operation/cancellation exception, and
        // cannot hide an uploaded resource which the caller will never receive to dispose.
        var releaseFailure = Release(layers);
        failure ??= releaseFailure;
        if (failure is not null)
        {
            try { output?.Dispose(); }
            catch { /* Preserve the first failure after attempting every owned cleanup. */ }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return output!;
    }

    private static bool ValidDimensions(int width, int height, int maximumPixels)
        => width is > 0 and <= MaximumDimension && height is > 0 and <= MaximumDimension
            && (long)width * height <= maximumPixels;
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static void ValidateRaw(RawImageSpecification spec, byte[] bytes, OwnedLayer layer)
    {
        // Public readback permits padded rows and a shorter final row without unused padding.
        var required = (long)(spec.Height - 1) * spec.Pitch + (long)spec.Width * 4;
        if (bytes is null || !ValidDimensions(spec.Width, spec.Height, MaximumInputPixels)
            || spec.Width != layer.Width || spec.Height != layer.Height
            || spec.DxgiFormat is not (28 or 29 or 87 or 91) || spec.Pitch < (long)spec.Width * 4
            || required > bytes.Length || required > MaximumInputBytes || bytes.Length > MaximumInputBytes)
            throw new InvalidOperationException("Unsupported or over-budget portrait readback pixels.");
    }

    private static void CompositeLayer(float[] canvas, int width, int height, OwnedLayer layer,
        RawImageSpecification spec, byte[] bytes, CancellationToken token)
    {
        var left = (double)layer.Placement.X * width;
        var top = (double)layer.Placement.Y * height;
        var layerWidth = (double)layer.Placement.Width * width;
        var layerHeight = (double)layer.Placement.Height * height;
        // Clip covered pixel centers, but compute UVs from the original, unclipped rectangle.
        var x0 = PixelBound(left, width);
        var y0 = PixelBound(top, height);
        var x1 = PixelBound(left + layerWidth, width);
        var y1 = PixelBound(top + layerHeight, height);
        var tint = layer.Tint;
        var tintR = (tint & 255) / 255f;
        var tintG = ((tint >> 8) & 255) / 255f;
        var tintB = ((tint >> 16) & 255) / 255f;
        var tintA = (tint >> 24) / 255f;
        for (var y = y0; y < y1; y++)
        {
            token.ThrowIfCancellationRequested();
            var v = layer.UV0.Y + (layer.UV1.Y - (double)layer.UV0.Y) * ((y + .5 - top) / layerHeight);
            for (var x = x0; x < x1; x++)
            {
                var u = layer.UV0.X + (layer.UV1.X - (double)layer.UV0.X) * ((x + .5 - left) / layerWidth);
                var sample = Sample(spec, bytes, u, v);
                var alpha = sample.W * tintA;
                var remaining = 1 - alpha;
                var at = (y * width + x) * 4;
                canvas[at] = sample.X * tintR * tintA + canvas[at] * remaining;
                canvas[at + 1] = sample.Y * tintG * tintA + canvas[at + 1] * remaining;
                canvas[at + 2] = sample.Z * tintB * tintA + canvas[at + 2] * remaining;
                canvas[at + 3] = alpha + canvas[at + 3] * remaining;
            }
        }
    }

    private static int PixelBound(double edge, int extent)
        => (int)Math.Clamp(Math.Ceiling(edge - .5), 0, extent);

    private static Vector4 Sample(RawImageSpecification spec, byte[] bytes, double u, double v)
    {
        // GPU-style normalized texel centers, with clamp-to-edge sampling. Decode sRGB before
        // interpolating, and interpolate premultiplied RGB so transparent edges cannot add halos.
        var x = Math.Clamp(u * spec.Width - .5, 0, spec.Width - 1);
        var y = Math.Clamp(v * spec.Height - .5, 0, spec.Height - 1);
        var ix = (int)x;
        var iy = (int)y;
        var nx = Math.Min(ix + 1, spec.Width - 1);
        var ny = Math.Min(iy + 1, spec.Height - 1);
        var tx = (float)(x - ix);
        var ty = (float)(y - iy);
        return Vector4.Lerp(Vector4.Lerp(Pixel(spec, bytes, ix, iy), Pixel(spec, bytes, nx, iy), tx),
            Vector4.Lerp(Pixel(spec, bytes, ix, ny), Pixel(spec, bytes, nx, ny), tx), ty);
    }

    private static Vector4 Pixel(RawImageSpecification spec, byte[] bytes, int x, int y)
    {
        var at = y * spec.Pitch + x * 4;
        var bgra = spec.DxgiFormat is 87 or 91;
        var srgb = spec.DxgiFormat is 29 or 91;
        var r = bytes[at + (bgra ? 2 : 0)];
        var g = bytes[at + 1];
        var b = bytes[at + (bgra ? 0 : 2)];
        var alpha = bytes[at + 3] / 255f;
        return new((srgb ? SrgbToLinear[r] : r / 255f) * alpha,
            (srgb ? SrgbToLinear[g] : g / 255f) * alpha,
            (srgb ? SrgbToLinear[b] : b / 255f) * alpha, alpha);
    }

    private static byte[] Encode(float[] canvas, CancellationToken token)
    {
        var rgba = new byte[canvas.Length];
        for (var at = 0; at < canvas.Length; at += 4)
        {
            if ((at & 4095) == 0) token.ThrowIfCancellationRequested();
            var alpha = canvas[at + 3];
            if (alpha <= 0) continue;
            rgba[at] = Byte(canvas[at] / alpha);
            rgba[at + 1] = Byte(canvas[at + 1] / alpha);
            rgba[at + 2] = Byte(canvas[at + 2] / alpha);
            rgba[at + 3] = Byte(alpha);
        }
        return rgba;
    }

    private static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
    private static float[] BuildSrgbTable()
    {
        var result = new float[256];
        for (var i = 0; i < result.Length; i++)
        {
            var value = i / 255f;
            result[i] = value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);
        }
        return result;
    }

    private static Exception? Release(IReadOnlyList<OwnedLayer> layers)
    {
        // Do not leave later wrappers unreleased if an individual wrapper's disposal fails.
        Exception? failure = null;
        foreach (var layer in layers)
        {
            try { layer.Texture.Dispose(); }
            catch (Exception error) { failure ??= error; }
        }
        return failure;
    }

    private sealed record OwnedLayer(IDalamudTextureWrap Texture, int Width, int Height,
        PortraitCaptureRect Placement, Vector2 UV0, Vector2 UV1, uint Tint);
}
