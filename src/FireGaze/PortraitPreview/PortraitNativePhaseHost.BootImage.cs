using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitNativeBootModule(string Name, nint BaseAddress, int ImageSize, byte[] FileImage);
internal sealed record PortraitNativeBootIdentity(nint BaseAddress, int ImageSize, string DiskSHA256, string LoadedCodeSHA256);
internal sealed record PortraitNativeBootReadRange(int RVA, byte[] Bytes, bool Executable);
internal sealed record PortraitNativeBootImagePlan(int Size, IReadOnlyList<PortraitNativeBootReadRange> Ranges,
    IReadOnlyList<(string Name, uint RVA)> Exports);

internal interface IPortraitNativeBootProcess
{
    bool TryReadModule(out PortraitNativeBootModule module);
    bool TryReadMemory(nint address, Span<byte> destination);
    nint GetExport(nint module, string name);
}

/// <summary>
/// Read-only binding of the audited disk image to the already-loaded Boot code
/// and the standard Windows loader's six SafetyHook exports. No module loads,
/// native calls, hooks, or game object access. Mutable data/import routing and
/// arbitrary CLR DllImportResolver replacement are outside this qualification.
/// The audited text has no relocations; unexpected executable relocations reject
/// rather than masking any code bytes or accepting a different backend build.
/// </summary>
internal sealed class PortraitNativeBootImageQualifier(IPortraitNativeBootProcess process)
{
    internal const string ExpectedSHA256 = "862736DA018084B4A35EB149878D827D3AC651AE029915970365B686699B9A37";
    private static readonly (string Name, uint RVA)[] ExportContracts = [
        ("BootSafetyHookInlineCreate", 0x73690), ("BootSafetyHookInlineDestroy", 0x73800),
        ("BootSafetyHookInlineDisable", 0x73860), ("BootSafetyHookInlineEnable", 0x73930),
        ("BootSafetyHookInlineGetTrampoline", 0x73A00), ("BootSafetyHookInlineIsEnabled", 0x73A10),
    ];

    public bool TryQualify(out PortraitNativeBootIdentity? identity, out string reason)
    {
        identity = null;
        reason = "The already-loaded Boot module, executable image, or SafetyHook exports are unqualified.";
        try
        {
            if (nint.Size != 8 || !process.TryReadModule(out var first) || !Valid(first)
                || !TryInspectImage(first.FileImage, out var plan, out reason)) return false;
            reason = "The loaded Boot code, headers, exception metadata, or actual export addresses differ from the audited image.";
            if (plan!.Size != first.ImageSize || !ReadAndCompare(first, plan, out var codeHash)
                || !process.TryReadModule(out var second) || !Valid(second)
                || second.Name != first.Name || second.BaseAddress != first.BaseAddress || second.ImageSize != first.ImageSize
                || !ReadAndCompare(second, plan, out var secondHash) || codeHash != secondHash) return false;
            identity = new(first.BaseAddress, first.ImageSize, ExpectedSHA256, codeHash);
            reason = "Already-loaded Boot executable bytes and six standard-loader SafetyHook exports match the audited image; no native entry invoked.";
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { reason = "Loaded Boot qualification unavailable: " + error.GetType().Name; return false; }
    }

    public static bool TryQualifyCurrentProcess(out PortraitNativeBootIdentity? identity, out string reason)
        => new PortraitNativeBootImageQualifier(new PortraitNativeBootCurrentProcess()).TryQualify(out identity, out reason);

    private static bool Valid(PortraitNativeBootModule module) => module is not null
        && string.Equals(module.Name, "Dalamud.Boot.dll", StringComparison.OrdinalIgnoreCase)
        && module.BaseAddress > 0 && module.ImageSize is > 0 and <= 16 * 1024 * 1024
        && module.BaseAddress <= nint.MaxValue - module.ImageSize && module.FileImage is not null
        && Convert.ToHexString(SHA256.HashData(module.FileImage)) == ExpectedSHA256;

    private bool ReadAndCompare(PortraitNativeBootModule module, PortraitNativeBootImagePlan plan, out string codeHash)
    {
        codeHash = string.Empty;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var range in plan.Ranges)
        {
            var observed = new byte[range.Bytes.Length];
            // Bounded, failing OS copies; never dereference a possibly stale module.
            for (var offset = 0; offset < observed.Length; offset += 64 * 1024)
            {
                var chunk = observed.AsSpan(offset, Math.Min(64 * 1024, observed.Length - offset));
                if (!process.TryReadMemory(module.BaseAddress + range.RVA + offset, chunk)) return false;
            }
            if (!observed.AsSpan().SequenceEqual(range.Bytes)) return false;
            if (range.Executable) hash.AppendData(observed);
        }
        foreach (var export in plan.Exports)
            if (process.GetExport(module.BaseAddress, export.Name) != module.BaseAddress + (nint)export.RVA) return false;
        codeHash = Convert.ToHexString(hash.GetHashAndReset()); return true;
    }

    // Parser is separately testable against malformed relocation/export inputs.
    // Production always checks the complete immutable disk SHA before entering it.
    internal static bool TryInspectImage(byte[] image, out PortraitNativeBootImagePlan? plan, out string reason)
    {
        plan = null; reason = "Boot PE executable/relocation/export structure does not match its audited contract.";
        try
        {
            using var pe = new PEReader(new MemoryStream(image, false)); var headers = pe.PEHeaders;
            if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader is not { Magic: PEMagic.PE32Plus } header
                || header.SizeOfImage is <= 0 or > 16 * 1024 * 1024 || header.SizeOfHeaders <= 0 || header.SizeOfHeaders > image.Length) return false;
            // Windows' image mapper rewrites this preferred-base field for ASLR,
            // including SEC_IMAGE_NO_EXECUTE views not at that preferred address.
            // Base identity comes from OS module lookup and actual export pointers.
            // No executable bytes are excluded from comparison.
            var imageBaseOffset = headers.PEHeaderStartOffset + 24;
            if (imageBaseOffset < 0 || imageBaseOffset + 8 > header.SizeOfHeaders) return false;
            var ranges = new List<PortraitNativeBootReadRange> {
                new(0, image.AsSpan(0, imageBaseOffset).ToArray(), false),
                new(imageBaseOffset + 8, image.AsSpan(imageBaseOffset + 8, header.SizeOfHeaders - imageBaseOffset - 8).ToArray(), false),
            };
            foreach (var section in headers.SectionHeaders.Where(section => (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0))
            {
                var length = Math.Max(section.VirtualSize, section.SizeOfRawData);
                if (length <= 0 || section.VirtualAddress < header.SizeOfHeaders
                    || (long)section.VirtualAddress + length > header.SizeOfImage || section.SizeOfRawData < 0
                    || section.PointerToRawData < 0 || (long)section.PointerToRawData + section.SizeOfRawData > image.Length
                    || (section.SectionCharacteristics & SectionCharacteristics.MemWrite) != 0) return false;
                var expected = new byte[length]; image.AsSpan(section.PointerToRawData, section.SizeOfRawData).CopyTo(expected);
                ranges.Add(new(section.VirtualAddress, expected, true));
            }
            if (!ranges.Any(range => range.Executable) || ranges.Where(range => range.Executable).Any(left => ranges.Where(range => range.Executable).Any(right => !ReferenceEquals(left, right)
                && left.RVA < (long)right.RVA + right.Bytes.Length && right.RVA < (long)left.RVA + left.Bytes.Length))) return false;
            var reloc = header.BaseRelocationTableDirectory; var relocOffset = FileOffset(headers, image, reloc.RelativeVirtualAddress, reloc.Size);
            if (reloc.Size <= 0 || relocOffset < 0) return false;
            for (var cursor = 0; cursor < reloc.Size;)
            {
                if (reloc.Size - cursor < 8) return false;
                var block = image.AsSpan(relocOffset + cursor); var page = BinaryPrimitives.ReadUInt32LittleEndian(block);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
                if (size < 8 || (size & 1) != 0 || size > reloc.Size - cursor || page >= header.SizeOfImage || (page & 0xFFF) != 0) return false;
                for (var offset = 8; offset < size; offset += 2)
                {
                    var item = BinaryPrimitives.ReadUInt16LittleEndian(block[offset..]); var kind = item >> 12;
                    if (kind == 0) continue;
                    var rva = (long)page + (item & 0xFFF);
                    if (kind != 10 || rva + 8 > header.SizeOfImage || ranges.Any(range => range.Executable
                        && rva < (long)range.RVA + range.Bytes.Length && rva + 8 > range.RVA)) return false;
                }
                cursor += (int)size;
            }
            var exports = header.ExportTableDirectory; var exportOffset = FileOffset(headers, image, exports.RelativeVirtualAddress, exports.Size);
            if (exports.Size < 40 || exportOffset < 0) return false;
            var table = image.AsSpan(exportOffset, exports.Size);
            var functions = BinaryPrimitives.ReadUInt32LittleEndian(table[20..]); var names = BinaryPrimitives.ReadUInt32LittleEndian(table[24..]);
            if (functions is 0 or > 4096 || names is 0 or > 4096) return false;
            var functionTable = FileOffset(headers, image, BinaryPrimitives.ReadUInt32LittleEndian(table[28..]), functions * 4L);
            var nameTable = FileOffset(headers, image, BinaryPrimitives.ReadUInt32LittleEndian(table[32..]), names * 4L);
            var ordinalTable = FileOffset(headers, image, BinaryPrimitives.ReadUInt32LittleEndian(table[36..]), names * 2L);
            if (functionTable < 0 || nameTable < 0 || ordinalTable < 0) return false;
            var found = new Dictionary<string, uint>(StringComparer.Ordinal);
            for (var index = 0; index < names; index++)
            {
                var nameRVA = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(nameTable + index * 4));
                if (nameRVA < exports.RelativeVirtualAddress || nameRVA >= (long)exports.RelativeVirtualAddress + exports.Size) return false;
                var stringOffset = FileOffset(headers, image, nameRVA, 1); if (stringOffset < 0) return false;
                var maximum = (int)((long)exports.RelativeVirtualAddress + exports.Size - nameRVA);
                var end = image.AsSpan(stringOffset, maximum).IndexOf((byte)0); if (end <= 0 || end > 256) return false;
                var name = Encoding.ASCII.GetString(image, stringOffset, end);
                var ordinal = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(ordinalTable + index * 2)); if (ordinal >= functions) return false;
                var rva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(functionTable + ordinal * 4));
                if (ExportContracts.Any(contract => contract.Name == name)
                    && (!found.TryAdd(name, rva) || rva >= exports.RelativeVirtualAddress && rva < (long)exports.RelativeVirtualAddress + exports.Size)) return false;
            }
            if (ExportContracts.Any(contract => !found.TryGetValue(contract.Name, out var rva) || rva != contract.RVA
                || !ranges.Any(range => range.Executable && rva >= range.RVA && rva < (long)range.RVA + range.Bytes.Length))) return false;
            ranges.Add(new(exports.RelativeVirtualAddress, table.ToArray(), false));
            var exceptions = header.ExceptionTableDirectory; var exceptionOffset = FileOffset(headers, image, exceptions.RelativeVirtualAddress, exceptions.Size);
            if (exceptions.Size <= 0 || exceptions.Size % 12 != 0 || exceptionOffset < 0) return false;
            ranges.Add(new(exceptions.RelativeVirtualAddress, image.AsSpan(exceptionOffset, exceptions.Size).ToArray(), false));
            plan = new(header.SizeOfImage, ranges.AsReadOnly(), Array.AsReadOnly(ExportContracts));
            reason = "Audited executable sections contain no code relocations; six direct SafetyHook exports and exception table are well formed."; return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { reason = "Boot PE inspection unavailable: " + error.GetType().Name; return false; }
    }

    private static int FileOffset(PEHeaders headers, byte[] image, long rva, long size)
    {
        if (rva < 0 || size < 0 || rva + size > int.MaxValue) return -1;
        foreach (var section in headers.SectionHeaders)
        {
            if (rva < section.VirtualAddress || rva + size > (long)section.VirtualAddress + section.SizeOfRawData) continue;
            var offset = section.PointerToRawData + rva - section.VirtualAddress;
            return offset >= 0 && offset + size <= image.Length ? (int)offset : -1;
        }
        return -1;
    }
}

/// <summary>Standard loader lookup only; never loads Boot or invokes its exports.</summary>
internal sealed unsafe class PortraitNativeBootCurrentProcess : IPortraitNativeBootProcess
{
    public bool TryReadModule(out PortraitNativeBootModule module)
    {
        module = null!; var handle = GetModuleHandleW("Dalamud.Boot.dll"); if (handle == 0) return false;
        using var process = Process.GetCurrentProcess();
        var matches = process.Modules.Cast<ProcessModule>().Where(candidate => candidate.ModuleName.Equals("Dalamud.Boot.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].BaseAddress != handle) return false;
        var boot = matches[0]; module = new(boot.ModuleName, handle, boot.ModuleMemorySize, File.ReadAllBytes(boot.FileName)); return true;
    }
    public nint GetExport(nint module, string name) => GetModuleHandleW("Dalamud.Boot.dll") == module ? GetProcAddress(module, name) : 0;
    public bool TryReadMemory(nint address, Span<byte> destination)
    {
        if (address <= 0 || destination.IsEmpty || destination.Length > 64 * 1024 || address > nint.MaxValue - destination.Length) return false;
        fixed (byte* output = destination) return ReadProcessMemory(-1, address, output, (nuint)destination.Length, out var read) && read == (nuint)destination.Length;
    }
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern nint GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string name);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern nint GetProcAddress(nint module, [MarshalAs(UnmanagedType.LPStr)] string name);
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadProcessMemory(nint process, nint address, void* output, nuint size, out nuint read);
}
