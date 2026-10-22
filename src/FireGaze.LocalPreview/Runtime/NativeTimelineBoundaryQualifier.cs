using System.Buffers.Binary;
using Dalamud.Plugin.Services;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using InteropGenerator.Runtime;

namespace FireGaze.LocalPreview.Runtime;

internal sealed record NativeTimelineModule(string Name, nint BaseAddress, int Size, byte[] FileImage);
internal sealed record NativeTimelineMetadata(byte[] SdkImage, Guid LoadedSdkMvid, byte[] DalamudImage, Guid LoadedDalamudMvid,
    nint SdkTimeline, nint SdkActionTimeline, IReadOnlyList<nint> AllSdkAddresses, bool UsesDeferredHookBackend);
internal sealed record NativeTimelineTarget(nint Address, int Rva, byte[] Code);
internal sealed record NativeTimelinePlan(nint ModuleBase, int ModuleSize, NativeTimelineTarget Timeline, NativeTimelineTarget ActionTimeline);
internal interface INativeTimelineProcess
{
    bool TryReadModule(out NativeTimelineModule module);
    bool TryReadMetadata(out NativeTimelineMetadata metadata);
    bool TryReadMemory(nint address, Span<byte> destination);
}

// This inventory follows complete current CN branch graphs, including all three
// chained-unwind spans of the resolved single-timeline function. Unknown SDK
// metadata alone never qualifies an address. Every input and loaded helper span
// must match in both read rounds before a target plan can reach a Hook factory.
internal sealed class NativeTimelineBoundaryQualifier(INativeTimelineProcess process)
{
    internal const string ClientHash = "958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9";
    internal const string SdkHash = "5726E301EE5456D526F524E9F88659F38A8717CB527FE4A86D83D1ADE0AB9798";
    internal const string DalamudHash = "AF15D754340C659F9F57C110C9914D8C68A068F588725CAAE50AC237B07DA1C4";
    private static readonly (int Rva, int Size)[] ReviewedSpans = [
        (0x606AB0, 0x54), (0x8BE410, 0x58), (0x606B10, 0x43A), (0x606FD0, 0xA5),
        (0x608530, 0x46B), (0x6089B0, 0x406), (0x8C0000, 0xDA), (0x6180E8, 0x26),
        (0x836D10, 0x3D), (0x836D80, 0x31), (0x1D9E0E0, 0x18),
    ];

    public bool TryQualify(out NativeTimelinePlan? plan, out string reason)
    {
        plan = null;
        reason = "Native timeline executable, SDK metadata, or loaded code did not match the reviewed contracts.";
        try
        {
            if (nint.Size != 8 || !process.TryReadModule(out var module) || !ValidModule(module)
                || !process.TryReadMetadata(out var metadata)) return false;
            if (!metadata.UsesDeferredHookBackend)
            {
                reason = "Reviewed local disabled SafetyHook factory unavailable: official scope or Boot identity/code did not qualify.";
                return false;
            }
            if (!ValidMetadata(metadata)
                || !Inspect(module, metadata, out var timeline, out var action, out var spans)
                || !ReadSpans(module, spans)) return false;
            if (!process.TryReadModule(out var secondModule) || !ValidModule(secondModule)
                || secondModule.BaseAddress != module.BaseAddress || secondModule.Size != module.Size || secondModule.Name != module.Name
                || !process.TryReadMetadata(out var secondMetadata) || !ValidMetadata(secondMetadata)
                || !SameMetadata(metadata, secondMetadata) || !ReadSpans(secondModule, spans))
            {
                reason = "Native timeline module, metadata, or loaded code changed during qualification.";
                return false;
            }
            plan = new(module.BaseAddress, module.Size, timeline!, action!);
            reason = "Exact current client/SDK/Dalamud identities, wrapper tails, SDK inventory and loaded native paths qualified; game acceptance remains separate.";
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            reason = "Native timeline qualification unavailable: " + error.GetType().Name;
            return false;
        }
    }

    public bool TryConfirmTarget(NativeTimelinePlan plan, NativeTimelineTarget target)
    {
        try
        {
            if ((target != plan.Timeline && target != plan.ActionTimeline) || !process.TryReadModule(out var module)
                || !ValidModule(module) || module.BaseAddress != plan.ModuleBase || module.Size != plan.ModuleSize
                || !process.TryReadMetadata(out var metadata) || !ValidMetadata(metadata)
                || !Inspect(module, metadata, out var timeline, out var action, out var spans)
                || timeline!.Address != plan.Timeline.Address || action!.Address != plan.ActionTimeline.Address
                || !timeline.Code.AsSpan().SequenceEqual(plan.Timeline.Code)
                || !action.Code.AsSpan().SequenceEqual(plan.ActionTimeline.Code)) return false;
            // Both factories are staged while disabled. No peer entry or body
            // is exempt: each pre-factory and final pre-enable check compares
            // the complete reviewed paths before either patch is exposed.
            return target.Address == module.BaseAddress + target.Rva
                && ReadSpans(module, spans);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private static bool ValidModule(NativeTimelineModule module) => module is not null
        && string.Equals(module.Name, "ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase)
        && module.BaseAddress > 0 && module.Size > 0 && module.BaseAddress <= nint.MaxValue - module.Size
        && Hash(module.FileImage) == ClientHash;

    private static bool ValidMetadata(NativeTimelineMetadata metadata)
        => metadata is not null && metadata.UsesDeferredHookBackend
            && Hash(metadata.SdkImage) == SdkHash && Hash(metadata.DalamudImage) == DalamudHash
            && Mvid(metadata.SdkImage) == metadata.LoadedSdkMvid && Mvid(metadata.DalamudImage) == metadata.LoadedDalamudMvid
            && metadata.LoadedSdkMvid != Guid.Empty && metadata.LoadedDalamudMvid != Guid.Empty
            && metadata.AllSdkAddresses.Count == 2574 && metadata.AllSdkAddresses.All(address => address != 0);

    private static bool SameMetadata(NativeTimelineMetadata left, NativeTimelineMetadata right)
        => left.LoadedSdkMvid == right.LoadedSdkMvid && left.LoadedDalamudMvid == right.LoadedDalamudMvid
            && left.SdkTimeline == right.SdkTimeline && left.SdkActionTimeline == right.SdkActionTimeline
            && left.UsesDeferredHookBackend == right.UsesDeferredHookBackend
            && left.AllSdkAddresses.SequenceEqual(right.AllSdkAddresses);

    private bool ReadSpans(NativeTimelineModule module, IReadOnlyList<(int Rva, byte[] Code)> spans)
    {
        foreach (var span in spans)
        {
            var loaded = new byte[span.Code.Length];
            if (!process.TryReadMemory(module.BaseAddress + span.Rva, loaded) || !loaded.AsSpan().SequenceEqual(span.Code)) return false;
        }
        return true;
    }

    private static bool Inspect(NativeTimelineModule module, NativeTimelineMetadata metadata,
        out NativeTimelineTarget? timeline, out NativeTimelineTarget? action,
        out IReadOnlyList<(int Rva, byte[] Code)> spans)
    {
        timeline = action = null;
        spans = [];
        using var pe = new PEReader(new MemoryStream(module.FileImage, false));
        var headers = pe.PEHeaders;
        if (headers.CoffHeader.Machine != Machine.Amd64 || headers.PEHeader is not { Magic: PEMagic.PE32Plus } header
            || header.SizeOfImage != module.Size) return false;
        var timelineRoot = FindSdkFunction(module.FileImage, headers, ActionTimelineSequencer.Addresses.PlayTimeline);
        var actionRoot = FindSdkFunction(module.FileImage, headers, TimelineContainer.Addresses.PlayActionTimeline);
        if (timelineRoot < 0 || actionRoot < 0 || metadata.SdkTimeline != module.BaseAddress + timelineRoot
            || metadata.SdkActionTimeline != module.BaseAddress + actionRoot) return false;
        var timelineRva = Relative(module.FileImage, headers, timelineRoot + 0x4F, 0xE9);
        var actionRva = Relative(module.FileImage, headers, actionRoot + 0x53, 0xE9);
        if (timelineRva != 0x606B10 || actionRva != 0x606FD0
            || !IsFunctionRoot(module.FileImage, headers, timelineRva) || !IsFunctionRoot(module.FileImage, headers, actionRva)
            || metadata.AllSdkAddresses.Contains(module.BaseAddress + timelineRva)
            || metadata.AllSdkAddresses.Contains(module.BaseAddress + actionRva)) return false;
        var copies = new List<(int, byte[])>();
        foreach (var span in ReviewedSpans)
        {
            var offset = FileOffset(headers, span.Rva, span.Size, true);
            if (offset < 0 || (long)offset + span.Size > module.FileImage.Length) return false;
            copies.Add((span.Rva, module.FileImage.AsSpan(offset, span.Size).ToArray()));
        }
        timeline = new(module.BaseAddress + timelineRva, timelineRva, copies.Single(copy => copy.Item1 == timelineRva).Item2);
        action = new(module.BaseAddress + actionRva, actionRva, copies.Single(copy => copy.Item1 == actionRva).Item2);
        spans = copies;
        return true;
    }

    private static int FindSdkFunction(byte[] bytes, PEHeaders headers, Address address)
    {
        if (!address.RelativeFollowOffsets.SequenceEqual(new ushort[] { 1 })) return -1;
        var pattern = address.String.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token == "??" ? (byte?)null : Convert.ToByte(token, 16)).ToArray();
        if (pattern.Length < 5 || pattern[0] != 0xE8) return -1;
        var matches = new List<int>();
        foreach (var section in headers.SectionHeaders.Where(section => (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0))
        {
            var cursor = 0;
            while (cursor <= section.SizeOfRawData - pattern.Length)
            {
                var match = bytes.AsSpan(section.PointerToRawData + cursor, section.SizeOfRawData - cursor).IndexOf((byte)0xE8);
                if (match < 0) break;
                cursor += match;
                if (cursor > section.SizeOfRawData - pattern.Length) break;
                var found = true;
                for (var index = 1; index < pattern.Length; index++)
                    if (pattern[index] is { } expected && bytes[section.PointerToRawData + cursor + index] != expected) { found = false; break; }
                if (found && matches.Count == 1) return -1;
                if (found) matches.Add(section.VirtualAddress + cursor);
                cursor++;
            }
        }
        return matches.Count == 1 ? Relative(bytes, headers, matches[0], 0xE8) : -1;
    }

    private static int Relative(byte[] bytes, PEHeaders headers, int site, byte opcode)
    {
        var offset = FileOffset(headers, site, 5, true);
        if (offset < 0 || bytes[offset] != opcode) return -1;
        var target = (long)site + 5 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 1));
        return target > 0 && target <= int.MaxValue && FileOffset(headers, (int)target, 1, true) >= 0 ? (int)target : -1;
    }

    private static bool IsFunctionRoot(byte[] bytes, PEHeaders headers, int rva)
    {
        var directory = headers.PEHeader!.ExceptionTableDirectory;
        if (directory.Size <= 0 || directory.Size % 12 != 0) return false;
        var start = FileOffset(headers, directory.RelativeVirtualAddress, directory.Size, false);
        if (start < 0) return false;
        var matches = 0;
        for (var offset = start; offset < start + directory.Size; offset += 12)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) == rva) matches++;
        return matches == 1;
    }

    private static int FileOffset(PEHeaders headers, int rva, int length, bool executable)
    {
        foreach (var section in headers.SectionHeaders)
            if ((!executable || (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0)
                && rva >= section.VirtualAddress && (long)rva + length <= (long)section.VirtualAddress + section.SizeOfRawData)
                return checked(section.PointerToRawData + rva - section.VirtualAddress);
        return -1;
    }
    private static string Hash(byte[]? image) => image is null ? "" : Convert.ToHexString(SHA256.HashData(image));
    private static Guid Mvid(byte[] image)
    { using var pe = new PEReader(new MemoryStream(image, false)); var reader = pe.GetMetadataReader(); return reader.GetGuid(reader.GetModuleDefinition().Mvid); }
}

internal sealed class NativeTimelineCurrentProcess(IGameInteropProvider? interop = null) : INativeTimelineProcess
{
    public bool TryReadModule(out NativeTimelineModule module)
    {
        module = null!;
        using var process = Process.GetCurrentProcess();
        var current = process.MainModule;
        if (current is null || !current.ModuleName.Equals("ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase)) return false;
        module = new(current.ModuleName, current.BaseAddress, current.ModuleMemorySize, File.ReadAllBytes(current.FileName));
        return true;
    }
    public bool TryReadMetadata(out NativeTimelineMetadata metadata)
    {
        var sdk = typeof(ActionTimelineSequencer).Assembly;
        var dalamud = typeof(Dalamud.Hooking.Hook<>).Assembly;
        var addresses = sdk.GetTypes().Where(type => type.Name == "Addresses")
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static)).Where(field => field.FieldType == typeof(Address))
            .Select(field => ((Address)field.GetValue(null)!).Value).ToArray();
        metadata = new(File.ReadAllBytes(sdk.Location), sdk.ManifestModule.ModuleVersionId,
            File.ReadAllBytes(dalamud.Location), dalamud.ManifestModule.ModuleVersionId,
            ActionTimelineSequencer.Addresses.PlayTimeline.Value, TimelineContainer.Addresses.PlayActionTimeline.Value, addresses,
            UsesReviewedDisabledBackend(dalamud));
        return true;
    }
    private bool UsesReviewedDisabledBackend(Assembly dalamud)
    {
        if (interop is null
            || !ReviewedSafetyHookSchema.MatchesIdentity(File.ReadAllBytes(dalamud.Location), dalamud.ManifestModule.ModuleVersionId)
            || !ReviewedSafetyHookSchema.TryRead(dalamud, out var schema) || interop.GetType() != schema!.Provider)
            return false;
        var plugin = schema.Plugin.GetValue(interop);
        var ready = plugin is not null
            && ReferenceEquals(schema.PluginAssembly.GetValue(plugin), typeof(ActorPreviewRuntime).Assembly)
            && schema.Tracked.GetValue(interop) is not null;
        if (!ready) return false;
        var boot = ReadReviewedBootEvidence();
        return NativeTimelineBackendContract.Matches(ready, boot.Image, boot.ReadLoaded);
    }
    internal ReviewedBootEvidence ReadReviewedBootEvidence()
    {
        using var current = Process.GetCurrentProcess();
        var boot = current.Modules.Cast<ProcessModule>().SingleOrDefault(module => module.ModuleName.Equals("Dalamud.Boot.dll", StringComparison.OrdinalIgnoreCase));
        if (boot is null || boot.BaseAddress <= 0) throw new InvalidOperationException("Loaded official Boot unavailable.");
        var address = boot.BaseAddress;
        var size = boot.ModuleMemorySize;
        return new(File.ReadAllBytes(boot.FileName), (rva, length) => {
            if (rva < 0 || (long)rva + length > size || address > nint.MaxValue - rva) return null;
            var code = new byte[length];
            return TryReadMemory(address + rva, code) ? code : null;
        });
    }
    public unsafe bool TryReadMemory(nint address, Span<byte> destination)
    {
        if (address <= 0 || destination.IsEmpty || address > nint.MaxValue - destination.Length) return false;
        fixed (byte* output = destination) return ReadProcessMemory(-1, address, output, (nuint)destination.Length, out var read) && read == (nuint)destination.Length;
    }
    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadProcessMemory(nint process, nint address, void* output, nuint length, out nuint read);
}
