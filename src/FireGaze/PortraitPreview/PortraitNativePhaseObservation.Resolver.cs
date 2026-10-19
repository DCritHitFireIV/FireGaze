using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitNativePhaseProcessModule(string Name, nint BaseAddress, int ImageSize, byte[] FileImage);
internal sealed record PortraitNativePhaseSDKImage(byte[] FileImage, Guid LoadedMVID);
internal sealed record PortraitNativePhaseResolvedPlan(nint ModuleBase, int ModuleSize, string OnDiskEXESHA256,
    string OnDiskSDKSHA256, Guid SDKMVID, IReadOnlyList<PortraitNativePhaseBoundTarget> Targets);

/// <summary>OS/module boundaries only. Never resolves managers, characters, scenes, or textures.</summary>
internal interface IPortraitNativePhaseProcess
{
    bool TryReadModule(out PortraitNativePhaseProcessModule module);
    bool TryReadSDK(out PortraitNativePhaseSDKImage image);
    nint SDKManagerRender { get; }
    nint SDKSourceRender { get; }
    bool IsAddressReserved(nint address);
    bool TryReadMemory(nint address, Span<byte> destination);
}

/// <summary>
/// Resolves actual audited entries before a diagnostic host can create any hooks. A resolved plan
/// is inert metadata, never permission to install a hook, allocate a character or run a scene
/// method. Installation must atomically reserve its targets and revalidate their live entries.
/// </summary>
internal sealed class PortraitNativePhaseObservationResolver(IPortraitNativePhaseProcess process)
{
    public bool TryResolve(out PortraitNativePhaseResolvedPlan? plan, out string reason)
    {
        plan = null;
        reason = "Native observation process, image, or runtime entry validation failed.";
        try
        {
            if (nint.Size != 8 || !process.TryReadModule(out var module) || !ValidModule(module)
                || !HashMatches(module.FileImage, PortraitNativePhaseObservationBinding.EXE)
                || !process.TryReadSDK(out var sdk) || !ValidSDK(sdk)
                || !TryInspectExecutableImage(module.FileImage, module.ImageSize, out var diskEntries, out reason)) return false;
            reason = "Runtime native entry, module identity, SDK resolution, or address reservation changed during qualification.";
            if (!ReadEntries(module, diskEntries, out var first)) return false;
            if (!process.TryReadModule(out var after) || after.Name != module.Name || after.BaseAddress != module.BaseAddress
                || after.ImageSize != module.ImageSize || !HashMatches(after.FileImage, PortraitNativePhaseObservationBinding.EXE)
                || !process.TryReadSDK(out var sdkAfter) || sdkAfter.LoadedMVID != sdk.LoadedMVID || !ValidSDK(sdkAfter)
                || !ReadEntries(after, diskEntries, out var second)) return false;
            if (!first.Select(StableEntry).SequenceEqual(second.Select(StableEntry))
                || !PortraitNativePhaseObservationBinding.TryBind(module.BaseAddress, PortraitNativePhaseObservationBinding.EXE,
                    PortraitNativePhaseObservationBinding.SDK, second, out var targets, out reason)) return false;
            // A reservation can appear inside the final read. This last sweep rejects
            // that observed drift; it cannot replace the installer's atomic lease.
            if (targets.Any(target => process.IsAddressReserved(target.Address)))
            {
                reason = "A native entry was reserved before the read-only plan was published.";
                return false;
            }
            plan = new(module.BaseAddress, module.ImageSize, PortraitNativePhaseObservationBinding.EXE,
                PortraitNativePhaseObservationBinding.SDK, sdk.LoadedMVID, targets);
            reason = "The current module's on-disk EXE, loaded SDK MVID and four runtime entry prefixes match; no installation lease or scene admission.";
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = "Native observation qualification unavailable: " + ex.GetType().Name;
            return false;
        }
    }

    internal static bool TryResolveCurrentProcess(Func<nint, bool> isAddressReserved,
        out PortraitNativePhaseResolvedPlan? plan, out string reason)
    {
        ArgumentNullException.ThrowIfNull(isAddressReserved);
        return new PortraitNativePhaseObservationResolver(new PortraitNativePhaseCurrentProcess(isAddressReserved)).TryResolve(out plan, out reason);
    }

    private static bool ValidModule(PortraitNativePhaseProcessModule module) => module is not null
        && string.Equals(module.Name, "ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase)
        && module.BaseAddress > 0 && module.ImageSize > 0 && module.BaseAddress <= nint.MaxValue - module.ImageSize;

    private static bool ValidSDK(PortraitNativePhaseSDKImage sdk)
    {
        if (sdk is null || sdk.LoadedMVID == Guid.Empty || !HashMatches(sdk.FileImage, PortraitNativePhaseObservationBinding.SDK)) return false;
        using var pe = new PEReader(new MemoryStream(sdk.FileImage, false));
        if (!pe.HasMetadata) return false;
        var metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid) == sdk.LoadedMVID;
    }

    private bool ReadEntries(PortraitNativePhaseProcessModule module, IReadOnlyList<PortraitNativePhaseEntryEvidence> diskEntries,
        out PortraitNativePhaseEntryEvidence[] entries)
    {
        entries = new PortraitNativePhaseEntryEvidence[diskEntries.Count];
        for (var index = 0; index < diskEntries.Count; index++)
        {
            var disk = diskEntries[index];
            var address = module.BaseAddress + (nint)disk.FunctionRootRVA;
            if (process.IsAddressReserved(address)) return false;
            var prefix = new byte[disk.Prefix.Length];
            if (!process.TryReadMemory(address, prefix) || !prefix.AsSpan().SequenceEqual(disk.Prefix)) return false;
            var sdkAddress = disk.Phase switch {
                PortraitNativeObservedPhase.ManagerRender => process.SDKManagerRender,
                PortraitNativeObservedPhase.SourceCharaViewRender => process.SDKSourceRender,
                _ => 0,
            };
            var usesSDK = disk.Phase is PortraitNativeObservedPhase.ManagerRender or PortraitNativeObservedPhase.SourceCharaViewRender;
            if (usesSDK && sdkAddress != address) return false;
            entries[index] = disk with { Address = address, Prefix = prefix, SDKResolvedAddress = sdkAddress };
        }
        return true;
    }

    private static (PortraitNativeObservedPhase, nint, uint, int, Type, nint, string) StableEntry(PortraitNativePhaseEntryEvidence entry)
        => (entry.Phase, entry.Address, entry.FunctionRootRVA, entry.PrefixMatches, entry.DelegateType,
            entry.SDKResolvedAddress, Convert.ToHexString(entry.Prefix));

    private static bool HashMatches(byte[]? image, string expected) => image is not null
        && Convert.ToHexString(SHA256.HashData(image)) == expected;

    internal static bool TryInspectExecutableImage(byte[] image, int moduleSize,
        out IReadOnlyList<PortraitNativePhaseEntryEvidence> entries, out string reason)
    {
        entries = [];
        reason = "Executable PE entry or unwind contract does not match the audited native functions.";
        try
        {
            using var pe = new PEReader(new MemoryStream(image, false));
            var headers = pe.PEHeaders;
            if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader is not { Magic: PEMagic.PE32Plus } header
                || header.SizeOfImage != moduleSize || moduleSize <= 0) return false;
            var directory = header.ExceptionTableDirectory;
            if (directory.Size <= 0 || directory.Size % 12 != 0 || directory.Size > image.Length) return false;
            var tableOffset = FileOffset(headers, directory.RelativeVirtualAddress, directory.Size);
            if (tableOffset < 0) return false;
            var result = new List<PortraitNativePhaseEntryEvidence>();
            foreach (var contract in PortraitNativePhaseObservationBinding.Inventory)
            {
                var prefix = Convert.FromHexString(contract.Prefix);
                if (contract.RVA > int.MaxValue || contract.RVA + prefix.Length > moduleSize) return false;
                var rva = (int)contract.RVA;
                if (!headers.SectionHeaders.Any(section => (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0
                    && rva >= section.VirtualAddress && (long)rva + prefix.Length <= (long)section.VirtualAddress + section.SizeOfRawData)) return false;
                var offset = FileOffset(headers, rva, prefix.Length);
                if (offset < 0 || !image.AsSpan(offset, prefix.Length).SequenceEqual(prefix)) return false;
                var remaining = image.AsSpan(); var count = 0;
                while (remaining.IndexOf(prefix) is var match && match >= 0)
                {
                    if (++count > 1) return false;
                    remaining = remaining[(match + 1)..];
                }
                if (count != 1 || !TryFunctionRoot(image, headers, tableOffset, directory.Size, contract.RVA, out var root)
                    || root != contract.RVA) return false;
                result.Add(new(contract.Phase, 0, root, count, prefix, contract.Delegate, 0));
            }
            entries = result.AsReadOnly();
            reason = "Audited PE executable sections, unique prefixes and unwind roots verified.";
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = "Executable inspection unavailable: " + ex.GetType().Name;
            return false;
        }
    }

    private static int FileOffset(PEHeaders headers, int rva, int length)
    {
        if (rva < 0 || length < 0) return -1;
        foreach (var section in headers.SectionHeaders)
        {
            if (rva < section.VirtualAddress || (long)rva + length > (long)section.VirtualAddress + section.SizeOfRawData) continue;
            var offset = (long)section.PointerToRawData + rva - section.VirtualAddress;
            return offset is >= 0 and <= int.MaxValue ? (int)offset : -1;
        }
        return -1;
    }

    private static bool TryFunctionRoot(byte[] image, PEHeaders headers, int tableOffset, int tableSize, uint rva, out uint root)
    {
        root = 0;
        uint begin = 0, end = 0, unwind = 0;
        var found = false;
        for (var offset = tableOffset; offset < (long)tableOffset + tableSize; offset += 12)
        {
            var row = image.AsSpan(offset, 12);
            var first = BinaryPrimitives.ReadUInt32LittleEndian(row);
            var last = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            if (rva < first || rva >= last) continue;
            if (found) return false;
            begin = first; end = last; unwind = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]); found = true;
        }
        if (!found) return false;
        var seen = new HashSet<(uint, uint, uint)>();
        for (var depth = 0; depth < 32; depth++)
        {
            if (begin >= end || unwind > int.MaxValue || !seen.Add((begin, end, unwind))) return false;
            var offset = FileOffset(headers, (int)unwind, 4);
            if (offset < 0) return false;
            var data = image.AsSpan(offset, 4);
            if ((data[0] & 7) is not (1 or 2)) return false;
            var flags = data[0] >> 3;
            if ((flags & 4) == 0) { root = begin; return true; }
            if ((flags & 3) != 0) return false;
            var chainRVA = (long)unwind + 4 + ((data[2] + 1) & ~1) * 2;
            if (chainRVA > int.MaxValue) return false;
            var chainOffset = FileOffset(headers, (int)chainRVA, 12);
            if (chainOffset < 0) return false;
            var row = image.AsSpan(chainOffset, 12);
            begin = BinaryPrimitives.ReadUInt32LittleEndian(row);
            end = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            unwind = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
        }
        return false;
    }
}
