using System.Buffers.Binary;

namespace FireGaze.PortraitPreview;

internal static class PortraitThumbnailImage
{
    /// <summary>Rejects a not-yet-rendered transparent surface without treating opaque black as empty.</summary>
    public static bool HasVisiblePixels(ReadOnlySpan<byte> bytes, int width, int height, int pitch, int dxgiFormat)
    {
        // RGBA8 and BGRA8, both linear and sRGB, put alpha in the fourth byte.
        if (width != 160 || height != 256 || dxgiFormat is not (28 or 29 or 87 or 91)
            || pitch < width * 4 || (long)pitch * height > bytes.Length) return false;
        for (var row = 0; row < height; row++)
        {
            var scanline = bytes.Slice(row * pitch, width * 4);
            for (var alpha = 3; alpha < scanline.Length; alpha += 4)
                if (scanline[alpha] != 0) return true;
        }
        return false;
    }

    // Inspect dimensions before handing an encoded cache entry to an image/GPU decoder.
    public static bool IsSafeSize(ReadOnlySpan<byte> bytes) => bytes.Length is >= 33 and <= 256 * 1024
        && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
        && BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) == 13
        && bytes.Slice(12, 4).SequenceEqual("IHDR"u8)
        && BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]) == 160
        && BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]) == 256
        && bytes[24] == 8 && bytes[25] is 0 or 2 or 3 or 4 or 6;
}
