using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

// Historical input is parsed as data only. Never load it or substitute the
// current SDK assembly: these suites separately assert current-build refusal.
internal sealed record FrozenSdkFixture(string GameExecutable, byte[] Image, Guid MVID,
    byte[] CurrentImage, Guid CurrentMVID, bool CurrentUnsupported)
{
    private const string AuditedSHA256 = "5A7A8EA71E84D7410DBAD8A5391D4759C2A38B1A55F4F3BB745B22B556A6F778";

    public static FrozenSdkFixture Load(string[] arguments, Assembly currentSDK)
    {
        if (arguments.Length is not (2 or 4) || arguments[0] != "--game-exe"
            || arguments.Length == 4 && arguments[2] != "--frozen-sdk")
            throw new ArgumentException("--game-exe <current client> [--frozen-sdk <audited historical SDK>] required; disk data only.");
        var current = File.ReadAllBytes(currentSDK.Location);
        if (ReadMVID(current) != currentSDK.ManifestModule.ModuleVersionId)
            throw new InvalidOperationException("The current test SDK disk image and loaded MVID differ.");
        var frozen = arguments.Length == 4 ? File.ReadAllBytes(arguments[3]) : current;
        if (Hash(frozen) != AuditedSHA256)
            throw new InvalidOperationException("Historical positive cases require the exact audited SDK fixture; pass --frozen-sdk. Production admission pins must remain unchanged.");
        var currentHash = Hash(current);
        Console.WriteLine($"Historical SDK fixture SHA256={AuditedSHA256}; current SDK SHA256={currentHash}. Historical cases do not qualify the current SDK or a live game.");
        return new(arguments[1], frozen, ReadMVID(frozen), current, currentSDK.ManifestModule.ModuleVersionId,
            currentHash != AuditedSHA256);
    }

    private static string Hash(byte[] image) => Convert.ToHexString(SHA256.HashData(image));
    private static Guid ReadMVID(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image, false));
        var metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }
}
