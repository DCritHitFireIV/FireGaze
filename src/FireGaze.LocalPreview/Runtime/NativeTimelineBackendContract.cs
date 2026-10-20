using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace FireGaze.LocalPreview.Runtime;

// The current Reloaded constructor briefly activates before returning its
// Original holder. Only the reviewed SafetyHook StartDisabled implementation
// may enter the staged factory path. This does not change any global backend.
internal static class NativeTimelineBackendContract
{
    internal const string BootHash = "862736DA018084B4A35EB149878D827D3AC651AE029915970365B686699B9A37";
    private static readonly (int Rva, int Size)[] CreationSpans = [
        (0x73690, 0x162), // Export passes StartDisabled=1 in R9D.
        (0x1F920, 0x78), // Forwards flags into the underlying create function.
        (0x1F770, 0x1B0), // Flag bit 1 bypasses native Enable.
    ];

    internal static bool Matches(bool safetyHookSelected, byte[] image, Func<int, int, byte[]?> readLoaded)
    {
        if (!safetyHookSelected || Convert.ToHexString(SHA256.HashData(image)) != BootHash) return false;
        using var pe = new PEReader(new MemoryStream(image, false));
        if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64) return false;
        foreach (var (rva, size) in CreationSpans)
        {
            var section = pe.PEHeaders.SectionHeaders.SingleOrDefault(section =>
                (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0 && rva >= section.VirtualAddress
                && (long)rva + size <= (long)section.VirtualAddress + section.SizeOfRawData);
            if (section.SizeOfRawData == 0) return false;
            var offset = checked(section.PointerToRawData + rva - section.VirtualAddress);
            var loaded = readLoaded(rva, size);
            if (loaded is null || loaded.Length != size || !loaded.AsSpan().SequenceEqual(image.AsSpan(offset, size))) return false;
        }
        return true;
    }
}
