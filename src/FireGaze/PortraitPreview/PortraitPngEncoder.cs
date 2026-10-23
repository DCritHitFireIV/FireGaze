using System.Buffers.Binary;
using System.IO.Compression;
using Dalamud.Interface.Textures;

namespace FireGaze.PortraitPreview;

/// <summary>PNG encoding of detached CPU pixels; this class never enters a GPU or SDK readback.</summary>
internal static class PortraitPngEncoder
{
    public static byte[] Encode(RawImageSpecification spec, byte[] bytes, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        token.ThrowIfCancellationRequested();
        var rowBytes = (long)spec.Width * 4;
        var needed = (long)(spec.Height - 1) * spec.Pitch + rowBytes;
        if (spec.Width is <= 0 or > 16384 || spec.Height is <= 0 or > 16384
            || (long)spec.Width * spec.Height > 16_777_216 || spec.Pitch < rowBytes
            || spec.DxgiFormat is not (28 or 29 or 87 or 91) || needed > bytes.Length || needed > 67_108_864)
            throw new ArgumentException("Invalid or over-budget portrait PNG pixels.", nameof(spec));

        using var output = new MemoryStream();
        output.Write(new byte[] { 137,80,78,71,13,10,26,10 });
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, spec.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], spec.Height);
        header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR"u8, header);
        if (spec.DxgiFormat is 29 or 91) Chunk(output, "sRGB"u8, new byte[] { 0 });
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[checked((int)rowBytes + 1)];
            for (var y=0; y<spec.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                bytes.AsSpan(checked(y * spec.Pitch), (int)rowBytes).CopyTo(row.AsSpan(1));
                if (spec.DxgiFormat is 87 or 91)
                    for (var x=1; x<row.Length; x+=4) (row[x], row[x+2]) = (row[x+2], row[x]);
                zlib.Write(row);
            }
        }
        token.ThrowIfCancellationRequested();
        Chunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        Chunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word); output.Write(type); output.Write(data);
        var crc = Crc(Crc(uint.MaxValue, type), data);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        output.Write(word);
    }
    private static uint Crc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit=0; bit<8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc;
    }
}
