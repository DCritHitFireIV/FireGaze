using System.Buffers.Binary;
using System.IO.Compression;
using Dalamud.Interface.Textures;
#if HAS_PNG_ENCODER
using FireGaze.PortraitPreview;
#endif

#if !HAS_PNG_ENCODER
throw new InvalidOperationException("FAIL: portrait PNG encoding must use retained CPU pixels without SDK GPU readback.");
#else
var checks = 0;
void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks++; }
byte[] Decode(byte[] png, int width, int height, bool srgb)
{
    Check(png.AsSpan(0, 8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}), "PNG signature");
    var at = 8;
    using var idat = new MemoryStream();
    var color = false;
    var ended = false;
    while (at < png.Length)
    {
        var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at)); at += 4;
        var type = System.Text.Encoding.ASCII.GetString(png, at, 4);
        var chunk = png.AsSpan(at, length + 4);
        uint crc = uint.MaxValue;
        foreach (var b in chunk)
        {
            crc ^= b;
            for (var i=0;i<8;i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        Check(~crc == BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + length + 4)), "each PNG chunk has valid CRC");
        var body = png.AsSpan(at + 4, length);
        if (type == "IHDR") Check(BinaryPrimitives.ReadInt32BigEndian(body) == width
            && BinaryPrimitives.ReadInt32BigEndian(body[4..]) == height && body[8] == 8 && body[9] == 6, "PNG exact RGBA dimensions");
        if (type == "IDAT") idat.Write(body);
        if (type == "sRGB") color = true;
        if (type == "IEND") ended = true;
        at += length + 8;
    }
    Check(at == png.Length && ended && color == srgb, "PNG terminates exactly and preserves sRGB identity");
    idat.Position = 0;
    using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
    using var raw = new MemoryStream(); zlib.CopyTo(raw);
    return raw.ToArray();
}
foreach (var format in new[]{28,29,87,91})
{
    var bytes = new byte[]{1,2,3,4, 5,6,7,8, 99,99,99,99, 9,10,11,12, 13,14,15,16};
    var png = PortraitPngEncoder.Encode(new RawImageSpecification(2,2,format,12), bytes);
    var actual = Decode(png,2,2,format is 29 or 91);
    var expected = format is 87 or 91
        ? new byte[]{0,3,2,1,4, 7,6,5,8, 0,11,10,9,12, 15,14,13,16}
        : new byte[]{0,1,2,3,4, 5,6,7,8, 0,9,10,11,12, 13,14,15,16};
    Check(actual.SequenceEqual(expected), "PNG row packing, channel order and alpha preserve exact pixels");
}
foreach(var spec in new[]{new RawImageSpecification(2,2,28,7), new RawImageSpecification(2,2,10,8), new RawImageSpecification(16384,16384,28,65536)})
{
    var refused = false; try { PortraitPngEncoder.Encode(spec, new byte[16]); } catch (ArgumentException) { refused = true; }
    Check(refused, "invalid pixels/format/oversized PNG denied before allocation");
}
var shortRefused = false;
try { PortraitPngEncoder.Encode(new RawImageSpecification(2,2,28,12), new byte[19]); } catch (ArgumentException) { shortRefused=true; }
Check(shortRefused,"truncated final row rejected");
using(var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel(); var denied=false;
    try { PortraitPngEncoder.Encode(new RawImageSpecification(1,1,28,4), new byte[4], cancelled.Token); } catch(OperationCanceledException) {denied=true;}
    Check(denied,"cancelled encoding cannot produce an image");
}
Console.WriteLine($"PASS: {checks} portrait CPU PNG checks");
await ProviderCases.Run();
await RuntimeCases.Run();
#endif
