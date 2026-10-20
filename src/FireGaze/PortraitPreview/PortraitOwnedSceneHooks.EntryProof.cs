using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitOwnedSceneProofIdentity(nint ModuleBase, int ModuleSize, string Fingerprint);
internal readonly record struct PortraitOwnedSceneCreatedHookState(nint Handle, nint Target, nint Destination,
    nint Trampoline, nint OriginalBegin, nint OriginalEnd, nint OriginalCapacity, nuint TrampolineSize,
    int Kind, bool Enabled, bool Disposed, nint OriginalDelegatePointer, nint DetourDelegatePointer);
internal interface IPortraitOwnedSceneProofMemory
{
    bool TryQualify(out PortraitOwnedSceneProofIdentity identity);
    bool PreserveIdentity(PortraitOwnedSceneProofIdentity identity);
    bool TryRead(nint address, int size, out byte[] bytes);
    bool IsExecutable(nint address, int size);
    bool TryDescribeOfficialHook(object hook, Delegate stableDetour, out PortraitOwnedSceneCreatedHookState state);
}
internal sealed record PortraitOwnedSceneProofSpan(uint RVA, string Hex);
internal sealed record PortraitOwnedSceneProofFixup(int Offset, int InstructionEnd, uint TargetRVA);
internal sealed record PortraitOwnedSceneProofCandidate(bool Allowed, string Hex, IReadOnlyList<PortraitOwnedSceneProofFixup> Relocations);
internal sealed record PortraitOwnedSceneProofEntry(PortraitOwnedSceneNativeKind Kind, uint RVA, bool IsUnwindRoot,
    IReadOnlyList<PortraitOwnedSceneProofSpan> Body, IReadOnlyList<PortraitOwnedSceneProofSpan> Metadata,
    IReadOnlyList<PortraitOwnedSceneProofSpan> Padding, PortraitOwnedSceneProofCandidate Near, PortraitOwnedSceneProofCandidate Far);
internal sealed record PortraitOwnedSceneProofVTable(uint RVA, IReadOnlyList<uint> Targets);

/// <summary>
/// Pinned disk-derived expectations. Construction is managed-only. Opt-in qualification
/// checks every canonical byte (including inline data), raw runtime/unwind metadata,
/// independent leaf padding and relocated vtable pointers. It grants no scene admission.
/// </summary>
internal sealed partial class PortraitOwnedSceneCurrentTransportProof : IPortraitOwnedSceneTransportProof, IPortraitOwnedSceneCreatedHookProof
{
    private readonly IPortraitOwnedSceneProofMemory memory;
    private PortraitOwnedSceneProofIdentity identity;
    private bool qualified;
    internal PortraitOwnedSceneCurrentTransportProof(IPortraitOwnedSceneProofMemory? memory = null)
        => this.memory = memory ?? new CurrentProcessMemory();

    public bool TryValidate(IReadOnlyList<PortraitOwnedSceneNativeTarget> targets, out string reason)
    {
        qualified = false;
        reason = "Owned EXE/SDK identity, exact 39-entry inventory, or complete disk-derived byte evidence is unavailable.";
        try
        {
            if (!memory.TryQualify(out var current) || current.ModuleBase <= 0 || current.ModuleSize != FrozenImageSize
                || targets.Count != Manifest.Count || !targets.SequenceEqual(Manifest.Select(entry => new PortraitOwnedSceneNativeTarget(entry.Kind, current.ModuleBase+(nint)entry.RVA)))) return false;
            identity = current;
            if (!VerifyTables() || !Manifest.All(VerifyManifestEntry) || !memory.PreserveIdentity(identity)) return false;
            qualified = true; reason = "Owned 39-entry disk roots/leaves, complete spans and current SDK identity verified."; return true;
        }
        catch (Exception) { return false; }
    }

    public bool VerifyEntry(PortraitOwnedSceneNativeTarget target)
    {
        try
        {
            if (!qualified || !memory.PreserveIdentity(identity)) return false;
            var entry = Find(target);
            return entry is not null && VerifyManifestEntry(entry) && VerifyTables() && memory.PreserveIdentity(identity);
        }
        catch (Exception) { return false; }
    }

    // Called only after official Create returned disabled and before publishing Original,
    // and repeated immediately before Enable. The returned handle is never owned/freed here.
    public bool VerifyCreatedHook(PortraitOwnedSceneNativeTarget target, IPortraitNativePhaseHostHook hook,
        Delegate stableDetour, out string reason)
    {
        reason = "The returned official SafetyHook original span or complete trampoline differs from the pinned contract.";
        try
        {
            if (!VerifyEntry(target) || Find(target) is not { } entry || hook.Address != target.Address
                || hook.BackendName != "SafetyHook" || hook.IsDisposed || hook.IsEnabled
                || hook is not IPortraitNativeHookInspection inspection) return false;
            var inspections = 0;
            var accepted = inspection.TryInspect(actual => ++inspections == 1
                && memory.TryDescribeOfficialHook(actual, stableDetour, out var state) && VerifyCreatedState(target, entry, state));
            return accepted && inspections == 1 && VerifyEntry(target);
        }
        catch (Exception) { return false; }
    }

    private bool VerifyCreatedState(PortraitOwnedSceneNativeTarget target, PortraitOwnedSceneProofEntry entry, PortraitOwnedSceneCreatedHookState state)
    {
        if (state.Handle <= 0 || state.Enabled || state.Disposed || state.Target != target.Address
            || state.Destination <= 0 || state.Destination != state.DetourDelegatePointer
            || state.Trampoline <= 0 || state.Trampoline != state.OriginalDelegatePointer
            || state.Kind is not (1 or 2)) return false;
        var candidate = state.Kind == 1 ? entry.Near : entry.Far;
        if (!candidate.Allowed) return false;
        var original = Convert.FromHexString(candidate.Hex);
        if (original.Length < (state.Kind == 1 ? 5 : 14) || original.Length > 28
            || state.OriginalBegin <= 0 || state.OriginalEnd-state.OriginalBegin != original.Length
            || state.OriginalEnd < state.OriginalBegin || state.OriginalCapacity < state.OriginalBegin
            || state.OriginalCapacity < state.OriginalEnd || state.OriginalCapacity-state.OriginalBegin > 1024
            || !ReadEquals(state.OriginalBegin, original)) return false;
        var tail = state.Kind == 1 ? 19 : 14;
        if (state.TrampolineSize != (nuint)(original.Length+tail)
            || !memory.IsExecutable(state.Trampoline, original.Length+tail)) return false;
        var expected = new byte[original.Length+tail]; original.CopyTo(expected, 0);
        foreach (var relocation in candidate.Relocations)
        {
            if (state.Kind != 1 || relocation.Offset < 0 || relocation.Offset+4 > original.Length) return false;
            var displacement = checked((long)identity.ModuleBase+relocation.TargetRVA-(long)state.Trampoline-relocation.InstructionEnd);
            if (displacement is < int.MinValue or > int.MaxValue) return false;
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(relocation.Offset), (int)displacement);
        }
        var at = original.Length;
        if (state.Kind == 1)
        {
            var displacement = checked((long)target.Address-(long)state.Trampoline-5);
            if (displacement is < int.MinValue or > int.MaxValue) return false;
            expected[at] = 0xE9;
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(at+1), (int)displacement);
            expected[at+5] = 0xFF; expected[at+6] = 0x25;
            BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(at+11), state.Destination);
        }
        else
        {
            expected[at] = 0xFF; expected[at+1] = 0x25;
            BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(at+6), checked((long)target.Address+original.Length));
        }
        return ReadEquals(state.Trampoline, expected);
    }

    private PortraitOwnedSceneProofEntry? Find(PortraitOwnedSceneNativeTarget target)
        => Manifest.FirstOrDefault(entry => entry.Kind == target.Kind && identity.ModuleBase+(nint)entry.RVA == target.Address);
    private bool VerifyManifestEntry(PortraitOwnedSceneProofEntry entry)
        => entry.Body.All(span => VerifySpan(span, true)) && entry.Padding.All(span => VerifySpan(span, true))
            && entry.Metadata.All(span => VerifySpan(span, false));
    private bool VerifySpan(PortraitOwnedSceneProofSpan span, bool executable)
    {
        var bytes = Convert.FromHexString(span.Hex);
        if (span.RVA >= identity.ModuleSize || bytes.Length == 0 || bytes.Length > identity.ModuleSize-span.RVA) return false;
        var address = identity.ModuleBase+(nint)span.RVA;
        return (!executable || memory.IsExecutable(address, bytes.Length)) && ReadEquals(address, bytes);
    }
    private bool VerifyTables()
    {
        foreach (var table in VTables)
        {
            var expected = new byte[table.Targets.Count*8];
            for (var index = 0; index < table.Targets.Count; index++)
                BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(index*8), identity.ModuleBase+(nint)table.Targets[index]);
            if (!ReadEquals(identity.ModuleBase+(nint)table.RVA, expected)) return false;
        }
        return true;
    }
    private bool ReadEquals(nint address, byte[] expected)
        => memory.TryRead(address, expected.Length, out var current) && current.Length == expected.Length && current.AsSpan().SequenceEqual(expected);

    private sealed unsafe class CurrentProcessMemory : IPortraitOwnedSceneProofMemory
    {
        private const string SDKMVID = "c72f17a8-6ed1-4a30-a097-74137dc08f9e";
        public bool TryQualify(out PortraitOwnedSceneProofIdentity identity)
        {
            identity = default;
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return false;
            using var process = Process.GetCurrentProcess();
            var module = process.MainModule; var sdk = typeof(CharaView).Assembly;
            var fingerprint = module is null ? null : Fingerprint(module.FileName, sdk);
            if (module is null || module.ModuleMemorySize != FrozenImageSize
                || !string.Equals(Path.GetFileName(module.FileName), "ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase)
                || !HashMatches(module.FileName, FrozenEXESHA256) || !HashMatches(sdk.Location, FrozenSDKSHA256)
                || sdk.IsCollectible || sdk.ManifestModule.ModuleVersionId != Guid.Parse(SDKMVID)
                || !DiskMVIDMatches(sdk) || !SDKCallingConventionMatches() || !PortraitOwnedSceneHooks.NativeAbiMatchesSDK(out _)
                || !ResolvedSymbolsMatch(module.BaseAddress) || Fingerprint(module.FileName, sdk) != fingerprint) return false;
            identity = new(module.BaseAddress, module.ModuleMemorySize, fingerprint!);
            return true;
        }
        public bool PreserveIdentity(PortraitOwnedSceneProofIdentity identity)
        {
            using var process = Process.GetCurrentProcess();
            var module = process.MainModule; var sdk = typeof(CharaView).Assembly;
            return module is not null && module.BaseAddress == identity.ModuleBase && module.ModuleMemorySize == identity.ModuleSize
                && sdk.ManifestModule.ModuleVersionId == Guid.Parse(SDKMVID) && ResolvedSymbolsMatch(module.BaseAddress)
                && Fingerprint(module.FileName, sdk) == identity.Fingerprint;
        }
        private static string Fingerprint(string exe, Assembly sdk)
        {
            var e = new FileInfo(exe); var s = new FileInfo(sdk.Location);
            return exe+"|"+e.Length+"|"+e.LastWriteTimeUtc.Ticks+"|"+sdk.Location+"|"+s.Length+"|"+s.LastWriteTimeUtc.Ticks+"|"+sdk.ManifestModule.ModuleVersionId;
        }
        private static bool ResolvedSymbolsMatch(nint moduleBase)
            => CharaView.Addresses.Render.Value == moduleBase+0x9CD4D0 && CharaViewPortrait.Addresses.Setup.Value == moduleBase+0x9D3960
                && ClientObjectManager.Addresses.CreateBattleCharacter.Value == moduleBase+0x1756410
                && ClientObjectManager.Addresses.DeleteObjectByIndex.Value == moduleBase+0x1756840;
        private static bool SDKCallingConventionMatches()
        {
            // The pinned SDK emits unmanaged default-convention virtual pointers
            // and generated delegates without a convention override. On this
            // explicitly AMD64 Windows contract that means the Microsoft x64 ABI.
            foreach (var name in new[] { "Dtor", "Initialize", "Release", "Update" })
            {
                var field = typeof(CharaView.CharaViewVirtualTable).GetField(name);
                if (field is null || !field.FieldType.IsUnmanagedFunctionPointer || field.FieldType.GetFunctionPointerCallingConventions().Length != 0) return false;
            }
            return new[] { typeof(CharaView.Delegates.Dtor), typeof(CharaView.Delegates.Initialize), typeof(CharaView.Delegates.Release),
                typeof(CharaView.Delegates.Update), typeof(CharaView.Delegates.Render), typeof(CharaViewPortrait.Delegates.Setup),
                typeof(ClientObjectManager.Delegates.CreateBattleCharacter), typeof(ClientObjectManager.Delegates.DeleteObjectByIndex) }
                .All(type => type.GetCustomAttribute<UnmanagedFunctionPointerAttribute>() is null);
        }
        private static bool HashMatches(string path, string expected)
        { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)) == expected; }
        private static bool DiskMVIDMatches(Assembly assembly)
        {
            using var input = File.OpenRead(assembly.Location); using var pe = new PEReader(input);
            if (!pe.HasMetadata) return false;
            var reader = pe.GetMetadataReader(); return reader.GetGuid(reader.GetModuleDefinition().Mvid) == assembly.ManifestModule.ModuleVersionId;
        }
        public bool TryRead(nint address, int size, out byte[] bytes)
        {
            bytes = [];
            if (address <= 0 || size is <= 0 or > 65536) return false;
            var result = new byte[size];
            fixed (byte* destination = result)
                if (!ReadProcessMemory((nint)(-1), address, destination, (nuint)size, out var copied) || copied != (nuint)size) return false;
            bytes = result; return true;
        }
        public bool IsExecutable(nint address, int size)
        {
            if (address <= 0 || size <= 0) return false;
            var end = checked((nuint)address+(nuint)size); var cursor = (nuint)address;
            while (cursor < end)
            {
                if (VirtualQuery((nint)cursor, out var region, (nuint)sizeof(MemoryRegion)) != (nuint)sizeof(MemoryRegion)
                    || region.State != 0x1000 || (region.Protect & (0x100u|0x01u)) != 0 || (region.Protect & 0xF0u) == 0) return false;
                var next = checked((nuint)region.BaseAddress+region.RegionSize);
                if (next <= cursor) return false; cursor = next;
            }
            return true;
        }
        public bool TryDescribeOfficialHook(object hook, Delegate stableDetour, out PortraitOwnedSceneCreatedHookState state)
        {
            state = default;
            var type = hook.GetType(); var assembly = typeof(Hook<>).Assembly;
            if (!type.IsConstructedGenericType || type.GetGenericTypeDefinition().FullName != "Dalamud.Hooking.Internal.SafetyHookHook`1"
                || type.Assembly != assembly || type.GenericTypeArguments[0] != stableDetour.GetType()
                || assembly.IsCollectible || !DiskMVIDMatches(assembly)
                || !HashMatches(assembly.Location, "CE0C3C8004CAC1E9AA96D0DC749463F5F1A32E9B49988419BF320545BF942C6A")
                || !PortraitNativeBootImageQualifier.TryQualifyCurrentProcess(out _, out _)) return false;
            var flags = BindingFlags.Instance|BindingFlags.NonPublic;
            if (type.GetField("detour", flags)?.GetValue(hook) is not Delegate detour || !ReferenceEquals(detour, stableDetour)
                || type.GetField("handle", flags)?.GetValue(hook) is not nint handle || handle <= 0
                || type.GetProperty("IsDisposed")?.GetValue(hook) is not false
                || type.GetProperty("Original")?.GetValue(hook) is not Delegate original
                || !TryRead(handle, 0xA8, out var bytes)) return false;
            nint Pointer(int offset) => (nint)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));
            state = new(handle, Pointer(0), Pointer(8), Pointer(0x20), Pointer(0x30), Pointer(0x38), Pointer(0x40),
                (nuint)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x48)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0xA4)),
                bytes[0xA0] != 0, false, Marshal.GetFunctionPointerForDelegate(original), Marshal.GetFunctionPointerForDelegate(detour));
            return bytes[0xA0] is 0 or 1 && Pointer(0) == (nint)type.GetProperty("Address")!.GetValue(hook)!;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryRegion { public nint BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionID; public nuint RegionSize; public uint State, Protect, Type; }
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(nint process, nint address, void* destination, nuint size, out nuint copied);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nuint VirtualQuery(nint address, out MemoryRegion region, nuint length);
    }
}

// BEGIN GENERATED OWNED39 DISK PROOF
// Independently reviewed raw EXE fragments, .pdata/unwind, leaf CFG/padding and vtables.
// Review SHA256: 1BF7018FAE3CA7139340CB2B47FAB1AB59C262F2FA8A51A6FD2E36601B12C65A
internal sealed partial class PortraitOwnedSceneCurrentTransportProof
{
    internal const int FrozenImageSize = 0x3811000;
    internal const string FrozenEXESHA256 = "958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9";
    internal const string FrozenSDKSHA256 = "5A7A8EA71E84D7410DBAD8A5391D4759C2A38B1A55F4F3BB745B22B556A6F778";
    internal static IReadOnlyList<PortraitOwnedSceneProofEntry> Manifest { get; } = Array.AsReadOnly(new PortraitOwnedSceneProofEntry[] {
        new(PortraitOwnedSceneNativeKind.Dtor, 0x9d9180, true,
            new PortraitOwnedSceneProofSpan[] { new(0x9D9180, "40534883EC20488D05AB667C01488BD9488901F6C201740ABA18030000E846463C01488BC34883C4205BC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x300F4A0, "80919D00AB919D00CC416802"), new(0x26841CC, "0106020006320230") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "40534883EC20", new PortraitOwnedSceneProofFixup[] {  }), new(false, "40534883EC20488D05AB667C01488BD9", new PortraitOwnedSceneProofFixup[] { new(9, 13, 0x219F838) })),
        new(PortraitOwnedSceneNativeKind.Dtor, 0x9d9230, true,
            new PortraitOwnedSceneProofSpan[] { new(0x9D9230, "40534883EC20488D05FB657C01488BD9488901F6C201740ABA30030000E896453C01488BC34883C4205BC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x300F4B8, "30929D005B929D00D4416802"), new(0x26841D4, "0106020006320230") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "40534883EC20", new PortraitOwnedSceneProofFixup[] {  }), new(false, "40534883EC20488D05FB657C01488BD9", new PortraitOwnedSceneProofFixup[] { new(9, 13, 0x219F838) })),
        new(PortraitOwnedSceneNativeKind.Dtor, 0x9d9130, true,
            new PortraitOwnedSceneProofSpan[] { new(0x9D9130, "48895C2408574883EC20488BF98BDA4881C170030000E8155DC6FF488D05E6667C01488907F6C301740DBA30040000488BCFE881463C01488B5C2430488BC74883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x300F494, "30919D0075919D0080476802"), new(0x2684780, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC20488BF98BDA", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Dtor, 0xd15a10, true,
            new PortraitOwnedSceneProofSpan[] { new(0xD15A10, "48895C2408574883EC208BDA488BF9E88C78CBFFF6C301740DBA28030000488BCFE8B27D0801488B5C2430488BC74883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x3047120, "105AD100445AD10044246D02"), new(0x26D2444, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC208BDA488BF9", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Dtor, 0xd159d0, true,
            new PortraitOwnedSceneProofSpan[] { new(0xD159D0, "48895C2408574883EC208BDA488BF9E8CC78CBFFF6C301740DBA20030000488BCFE8F27D0801488B5C2430488BC74883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x3047114, "D059D100045AD100D8326D02"), new(0x26D32D8, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC208BDA488BF9", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Dtor, 0xd15990, true,
            new PortraitOwnedSceneProofSpan[] { new(0xD15990, "48895C2408574883EC208BDA488BF9E80C79CBFFF6C301740DBA38030000488BCFE8327E0801488B5C2430488BC74883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x3047108, "9059D100C459D100A0336D02"), new(0x26D33A0, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC208BDA488BF9", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Dtor, 0xde4710, true,
            new PortraitOwnedSceneProofSpan[] { new(0xDE4710, "48895C2408574883EC208BDA488BF9E88C8BBEFFF6C301740DBA28030000488BCFE8B290FB00488B5C2430488BC74883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x305513C, "1047DE004447DE006C976E02"), new(0x26E976C, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC208BDA488BF9", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Dtor, 0xfa3530, true,
            new PortraitOwnedSceneProofSpan[] { new(0xFA3530, "48895C2408574883EC208BDA488BF9E86C9DA2FFF6C301740DBA38030000488BCFE892A2DF00488B5C2430488BC74883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x30709E8, "3035FA006435FA0088047102"), new(0x2710488, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC208BDA488BF9", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Dtor, 0xfa3570, true,
            new PortraitOwnedSceneProofSpan[] { new(0xFA3570, "48895C2408574883EC208BDA488BF9E82C9DA2FFF6C301740DBA18030000488BCFE852A2DF00488B5C2430488BC74883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x30709F4, "7035FA00A435FA0048147102"), new(0x2711448, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC208BDA488BF9", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Dtor, 0x10998e0, true,
            new PortraitOwnedSceneProofSpan[] { new(0x10998E0, "48895C2408574883EC208BDA488BF9E8BC3993FFF6C301740DBA30030000488BCFE8E23ED000488B5C2430488BC74883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x307EBB4, "E0980901149909016C627202"), new(0x272626C, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC208BDA488BF9", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Dtor, 0x1099920, true,
            new PortraitOwnedSceneProofSpan[] { new(0x1099920, "48895C2408574883EC208BDA488BF9E87C3993FFF6C301740DBA18030000488BCFE8A23ED000488B5C2430488BC74883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x307EBC0, "2099090154990901F86E7202"), new(0x2726EF8, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC208BDA488BF9", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Dtor, 0x1104600, true,
            new PortraitOwnedSceneProofSpan[] { new(0x1104600, "48895C2408574883EC208BDA488BF9E89C8C8CFFF6C301740DBA28030000488BCFE8C291C900488B5C2430488BC74883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x3085CB8, "00461001344610017C197302"), new(0x273197C, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC208BDA488BF9", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Release, 0x9cd3e0, true,
            new PortraitOwnedSceneProofSpan[] { new(0x9CD3E0, "48895C2408574883EC208B510C33FF488BD985D2782C4533C0488D0D30121202E83B94D80048635310C7430CFFFFFFFF488B056105F3014889BCD0A0000000897B10488B4B184885C9740A488B01FF501848897B18488B4B204885C9741D488B01FF5008488B4B204885C9740E488B01BA01000000FF1048897B2048897B3048897B3848897B40897B084889BBB80200004889BBC00200004889BBC80200004889BBD00200004889BBD80200004889BBE00200004889BBE80200004889BBF00200004889BBF80200004889BB00030000488B5C24304883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x300E4E0, "E0D39C00BBD49C007C2B6802"), new(0x2682B7C, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC208B510C33FF", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Release, 0x9d3b90, true,
            new PortraitOwnedSceneProofSpan[] { new(0x9D3B90, "48895C2408574883EC20488BD94881C170030000E807B8C6FF837B08037C29488B0D5A9DF201E865F46FFF4885C07418488B10488BC8FF92200100008B5310488B4808E848021F008B530C33FF85D2782C4533C0488D0D45AA1102E8502CD80048634B10C7430CFFFFFFFF488B05769DF2014889BCC8A0000000897B10488B4B184885C9740A488B01FF501848897B18488B4B204885C9741D488B01FF5008488B4B204885C9740E488B01BA01000000FF1048897B2048897B3048897B3848897B40897B084889BBB80200004889BBC00200004889BBC80200004889BBD00200004889BBD80200004889BBE00200004889BBE80200004889BBF00200004889BBF80200004889BB00030000488B5C24304883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x300EBB8, "903B9D00A63C9D0068356802"), new(0x2683568, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC20488BD94881C170030000", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Release, 0x10d6c10, false,
            new PortraitOwnedSceneProofSpan[] { new(0x10D6C10, "48C7811803000000000000E9C0678FFFCC") },
            new PortraitOwnedSceneProofSpan[] {  },
            new PortraitOwnedSceneProofSpan[] { new(0x10D6C20, "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC") }, new(true, "48C7811803000000000000", new PortraitOwnedSceneProofFixup[] {  }), new(false, "48C7811803000000000000E9C0678FFF", new PortraitOwnedSceneProofFixup[] { new(12, 16, 0x9CD3E0) })),
        new(PortraitOwnedSceneNativeKind.Initialize, 0x9cd2d0, true,
            new PortraitOwnedSceneProofSpan[] { new(0x9CD2D0, "48895C24084889742410574883EC20488BF244894110418D90F0000000488BD989510C498BF9488D0D33131202E85E98D8004885C074128B530C488D0D1F1312024533C0E82795D8008B530C488D0D0D1312024533C0E8E590D8004533C033D2488BCBE8D80D000048897330488B74243848897B38C7430801000000488B5C24304883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x300E4D4, "D0D29C0057D39C006C2B6802"), new(0x2682B6C, "010F06000F6407000F3406000F320B70") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C24084889742410574883EC20", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Initialize, 0xcabb40, true,
            new PortraitOwnedSceneProofSpan[] { new(0xCABB40, "40534883EC20488BD9E88217D2FFC6831C03000000C78318030000000000004883C4205BC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x3040034, "40BBCA0065BBCA00409A6C02"), new(0x26C9A40, "0106020006320230") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "40534883EC20", new PortraitOwnedSceneProofFixup[] {  }), new(false, "40534883EC20488BD9E88217D2FF", new PortraitOwnedSceneProofFixup[] { new(10, 14, 0x9CD2D0) })),
        new(PortraitOwnedSceneNativeKind.Initialize, 0xcbe5e0, true,
            new PortraitOwnedSceneProofSpan[] { new(0xCBE5E0, "40534883EC20488BD9E8E2ECD0FF33C04889832003000048898328030000488983300300004883C4205BC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x3041300, "E0E5CB000BE6CB007CB66C02"), new(0x26CB67C, "0106020006320230") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "40534883EC20", new PortraitOwnedSceneProofFixup[] {  }), new(false, "40534883EC20488BD9E8E2ECD0FF", new PortraitOwnedSceneProofFixup[] { new(10, 14, 0x9CD2D0) })),
        new(PortraitOwnedSceneNativeKind.Initialize, 0xdbb9c0, true,
            new PortraitOwnedSceneProofSpan[] { new(0xDBB9C0, "4C8BDC55498D6BD84881EC20010000488B0582ECB1014833C4488945D0"), new(0xDBB9DD, "49895BF033DB498973E8488BF14D8963D84C8BE24D896BD0458BE848899920030000C7811C0300000100000066C781180300000000C6811A03000000488D4C24304D8973C84C894C2420E89414C1FF488B4E30E8CB26C1FF4C8BF04885C00F848D0100000FB68818090000884C24300FB68819090000884C24310FB6881A090000884C24320FB6881B090000884C24330FB6881C090000884C24340FB6801D09000088442435410FB6861E09000088442436410FB6861F09000088442437410FB6862009000088442438410FB6862109000088442439410FB686220900008844243A410FB686230900008844243B410FB686240900008844243C410FB686250900008844243D410FB686260900008844243E410FB686270900008844243F410FB6862809000088442440410FB6862909000088442441410FB6862A09000088442442410FB6862B09000088442443410FB6862C09000088442444410FB6862D09000088442445410FB6862E09000088442446410FB6862F09000088442447410FB68630090000"), new(0xDBBB63, "4889BC2408010000488D7C244A88442448410FB68631090000884424494C89BC24E80000000F1F8400000000000FB7D3498D8EF8060000E8117AAFFFFFC3668907488D7F0283FB0272E34180BE18090000084C8BBC24E8000000488BBC24080100007507"), new(0xDBBBC7, "838E1C03000060488D542430488BCEE89517C1FF4C8B4C2420458BC5498BD4488BCEE8E216C1FF488BCEE89A2DC1FF4C8BB424F00000004C8BAC24F80000004C8BA42400010000488BB42410010000488B9C24180100004885C07414"), new(0xDBBC23, "4C8B084533C0488BC8418D500641FF9148010000488B4DD04833CCE89D24FE004881C4200100005DC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x3052D6C, "C0B9DB00DDB9DB0098626E02"), new(0x26E6298, "191D03000F01240004500000E8DFD901"), new(0x3052D78, "DDB9DB0063BBDB00AC626E02"), new(0x26E62AC, "21450A0045E41E0018D41F0011C420000A64220004342300C0B9DB00DDB9DB0098626E02"), new(0x3052D84, "63BBDB00C7BBDB00D0626E02"), new(0x26E62D0, "2125040025F41D0008742100DDB9DB0063BBDB00AC626E02"), new(0x3052D90, "C7BBDB0023BCDB00E8626E02"), new(0x26E62E8, "21000000DDB9DB0063BBDB00AC626E02"), new(0x3052D9C, "23BCDB004CBCDB00F8626E02"), new(0x26E62F8, "21000000C0B9DB00DDB9DB0098626E02") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "4C8BDC55498D6BD8", new PortraitOwnedSceneProofFixup[] {  }), new(true, "4C8BDC55498D6BD84881EC20010000", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Initialize, 0xffa470, true,
            new PortraitOwnedSceneProofSpan[] { new(0xFFA470, "40555357488D6C24B94881ECF0000000488B05D1018E014833C448894537488BDA488BF9E8372E9DFFC6871803000001C6871F03000000488B4B10488B01FF50708B931C010000488BC8E851627FFF48898728030000488BD00FB64837C0E903F6D180E101888F1A0300000FB64837C0E904F6D180E101888F1B030000488D4D970FB64037C0E805240188871C0300000FB64237C0E806240188871D030000E8AC299DFF488B4F30E8E33B9DFF488BC84885C0741280B818090000087509C6871E03000001EB10C6871E030000004885C90F842C0100000FB680180900008845970FB681190900008845980FB6811A0900008845990FB6811B09000088459A0FB6811C09000088459B0FB6811D09000088459C0FB6811E09000088459D0FB6811F09000088459E0FB6812009000088459F0FB681210900008845A00FB681220900008845A10FB681230900008845A20FB681240900008845A30FB681250900008845A40FB681260900008845A50FB681270900008845A60FB681280900008845A70FB681290900008845A80FB6812A0900008845A90FB6812B0900008845AA0FB6812C0900008845AB0FB6812D0900008845AC0FB6812E0900008845AD0FB6812F0900008845AE0FB681300900008845AF0FB681310900008845B00FB6871A03000088451F0FB6871B0300008845200FB6871C0300008845210FB6871D030000884522488D5597488BCFE8F12C9DFFBB380000000F1F40000F1F840000000000488B8728030000488B0C1848C1E920E86CB283FF4883C31C4881FBC00100007CDFC6871903000001488B4D374833CCE81C3ADA004881C4F00000005F5B5DC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x3075B9C, "70A4FF00CFA6FF00B49A7102"), new(0x2719AB4, "191E050010011E000470033002500000E8DFD901") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "40555357488D6C24B9", new PortraitOwnedSceneProofFixup[] {  }), new(true, "40555357488D6C24B94881ECF0000000", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Update, 0x9db610, false,
            new PortraitOwnedSceneProofSpan[] { new(0x9DB610, "C20000CC") },
            new PortraitOwnedSceneProofSpan[] {  },
            new PortraitOwnedSceneProofSpan[] { new(0x9DB613, "CCCCCCCCCCCCCCCCCCCCCCCCCC") }, new(true, "C20000CCCC", new PortraitOwnedSceneProofFixup[] {  }), new(true, "C20000CCCCCCCCCCCCCCCCCCCCCC", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Update, 0x9cfb10, true,
            new PortraitOwnedSceneProofSpan[] { new(0x9CFB10, "48895C2408574883EC2080B91C03000000498BF8488BD9750BE8C201000088831C0300008B931803000081FA000000E0743A488D0DA70EEE01E812CB12004885C074290FB690370900000FB68737090000C0E804C0EA04240180E2013AD0740C488D8FF8060000E87431EEFF488B5C24304883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x300E72C, "10FB9C0087FB9C00F82E6802"), new(0x2682EF8, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC2080B91C03000000", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Update, 0x9d3d30, true,
            new PortraitOwnedSceneProofSpan[] { new(0x9D3D30, "48895C24084889742410574883EC40488BF90FB6896803000085C9746B83E901743F83F9017561488D8F70030000E89DB5C6FF84C07451488B1D621A0C02488D8F70030000E846B8C6FF8B57104C8BC0488BCBE8E86A89FFC6876803000003EB270FB78F66030000E863DFE6FF4885C074168B5004488D8F70030000E80FB2C6FFC6876803000002488BCFE81024000048837F20000F84FE0000008B570C488D0D5BA81102E8862DD800488BD84885C00F84E3000000488B10488BC8FF92C0000000488BF04885C00F84CB000000F68318010000010F85BE0000000F57C04C8D4424300F57C90F29442420488D5424200F294C2430488BCFE8132B0000488D542420488BCEE8E6E9A5FF84C07429488D542430488BCEE8D5E9A5FF84C07418488B034533C0488BCB40B601418D5003FF9048010000EB16488B034533C0488BCB418D5003FF90380100004032F680BF0504000000741A488B034533C0488BCB418D50034084F67424FF9048010000EB224084F6740A488BCFE803090000EB1A488B034533C0488BCB418D5003FF9038010000C687060400000180BF07040000000F84810000008B570C488D0D50A711020FB6B708040000E8742CD800488BD84885C0746333D2488D88300A0000E86EEDEEFF84C0751EC68707040000014088B708040000488B5C2450488B7424584883C4405FC34533C066C787070400000000488D8B801B0000B2104084F67414488B5C2450488B7424584883C4405FE92E28F0FFE8592BF0FF488B5C2450488B7424584883C4405FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x300EBD0, "303D9D00673F9D0080356802"), new(0x2683580, "010F06000F640B000F340A000F720B70") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C24084889742410574883EC40", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Update, 0xc78140, true,
            new PortraitOwnedSceneProofSpan[] { new(0xC78140, "405741564883EC5880B918030000004D8BF0488BF90F8493040000"), new(0xC7815B, "4889B42480000000488B7130C6811803000000E8BDFEFFFF450FB68E7E0D00000FB6971E030000410FB6C9C0E90680E1013ACA743C0FB6CA84C075213887090300007519488BCFE8C966D5FF450FB68E7E0D0000B0010FB68F1E03000041C0E9064180E101443AC97407C687180300000184C00F850B040000488BCFE8945BD5FF84C00F84FB03000080BE64030000000F85EE03000080BE65030000000F85E1030000440FB6871A0300004981C6F806000048895C247033D248896C2478498BCE"), new(0xC7821C, "4C89642450"), new(0xC78221, "4C896C24484C897C2440E830A9C3FF0FB6971B030000498BCEE8D198C3FF0FB6971C030000498BCEE8A2AAC3FF0FB6971D030000498BCEE823ABC3FF80BF200300000041BD01000000458BFD0F85AA000000488B1D16ECE2014885DB0F84A8000000488B03488BCBFF900801000084C00F84940000000FB69358090000450FB6C5488BCFE8665ED5FF4533C033D2498BCEE819B3C3FF488B9350090000498BCEE80AB9C3FF488D8BF8060000E83EB0C3FF0FB6D0498BCEE803B0C3FF488D8BA0010000E8D763C2FF83BE040500000074070FB686FE0400000FB6C833D2E89DA246FF8BC8E866CCBBFF4885C0741C0FB6485AE8A88A46FF448BF8EB0E450FB6C533D2488BCFE8E55DD5FF0FB686DC040000418BED0FB68EC0040000458BE7D3E50FABC50FB686A40400000FABC50FB686880400000FABC50FB6866C0400000FABC50FB686500400000FABC50FB686340400000FABC50FB686180400000FABC50FB686FC0300000FABC50FB686E00300000FABC50FB686C40300000FABC50FB686A80300000FABC50FB6868C0300000FABC50FB686700300000FABC54184ED746E32DB488D86E2040000B90D0000009083780600741B440FB6004584C074120FB650FA84D27415413AD50FB6DB410F44DD4883E81C492BCD79D6EB33418BC833D2E89AA146FF8BC8E863CBBBFF4885C0741D0FB6485AE8A58946FF448BF8413BC5740C84DB740583E5FEEB0383E5FD80BF19030000004C8B6C24480F859A000000"), new(0xC78441, "33DB0F1F4000660F1F8400000000008BD3488D8EF8040000486BC21C4803C8448B410C4585C0746580390D750C33D2498BCEE858B1C3FFEB544183FF017505453BE7741485DB750640F6C502EB0883FA0175054084EA753580790A008B5110740B0FB64105440FB64904EB090FB64103440FB64902C644243001488BCF895424280FB6D388442420E8A260D5FFFFC383FB0E0F8C77FFFFFFEB614183FF01755B448B86040500004585C0744F80BEF90400000D744680BE02050000008B8E0805000074110FB686FD040000440FB68EFC040000EB0F0FB686FB040000440FB68EFA040000C64424300033D2894C2428488BCF88442420E83460D5FF4C8B7C2440488D9E7C0300004C8B642450BE0E000000"), new(0xC78552, "0FB653F480FA0D7510440FB70333D2498BCEE867B0C3FFEB50448B034585C074480FA3D57343807BFE008B4B04740B0FB643F9440FB64BF8EB090FB643F7440FB64BF6C644243001894C2428488BCF88442420E8C65FD5FF440FB643FB488BCF0FB653F4E81562D5FF4883C31C4883EE01758D488B6C2478488B5C2470488BB424800000004883C458415E5FC3"), new(0xC785DF, "488BB42480000000C6871803000001"), new(0xC785EE, "4883C458415E5FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x303CD28, "4081C7005B81C700044B6C02"), new(0x26C4B04, "0108030008A204E002700000"), new(0x303CD34, "5B81C7001C82C700104B6C02"), new(0x26C4B10, "21BE0600BE540F00B7340E00086410004081C7005B81C700044B6C02"), new(0x303CD40, "1C82C7002182C7002C4B6C02"), new(0x26C4B2C, "2105020005C40A005B81C7001C82C700104B6C02"), new(0x303CD4C, "2182C7004184C700404B6C02"), new(0x26C4B40, "210A04000AF4080005D409001C82C7002182C7002C4B6C02"), new(0x303CD58, "4184C7005285C700584B6C02"), new(0x26C4B58, "2100020000F408001C82C7002182C7002C4B6C02"), new(0x303CD64, "5285C700DF85C7006C4B6C02"), new(0x26C4B6C, "210000005B81C7001C82C700104B6C02"), new(0x303CD70, "DF85C700EE85C7007C4B6C02"), new(0x26C4B7C, "21000200006410004081C7005B81C700044B6C02"), new(0x303CD7C, "EE85C700F685C700904B6C02"), new(0x26C4B90, "210000004081C7005B81C700044B6C02") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "405741564883EC58", new PortraitOwnedSceneProofFixup[] {  }), new(true, "405741564883EC5880B91803000000", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Update, 0xcabb70, true,
            new PortraitOwnedSceneProofSpan[] { new(0xCABB70, "F6C2010F84B30400005341564881EC880000004983B80001000000498BD8"), new(0xCABB8E, "4C897C24604C8BF14C8B79304C897C2448753A488B01FF5010498B064C8D0D6F2D00004533C0498BD7498BCEFF5008488D153C25D2FF498BCE4C8B7C24604881C488000000415E5BE98517D2FF"), new(0xCABBDB, "4C896424704C8B25A9B2DF014C896424504D85E40F841B040000498B0424498BCCFF900801000084C00F8406040000498BCEE85E21D2FF84C00F84F6030000498B4F10"), new(0xCABC1E, "4889AC24A00000004889B424800000004C896C2468488B01FF9090000000BA2801000041B802000000488BC8E831A9B1FF85C0488DABF80600000FB68336090000410F94C048896C24402401443AC0740A33D2488BCDE8E76EC0FF498B4F10488B01FF9090000000BA2A01000041B802000000488BC8E8E7A8B1FF85C00FB683370900000F94C224013AD07408488BCDE85D5EC0FF498B4F10488B01FF9090000000BA2901000041B802000000488BC8E8ADA8B1FF85C00FB683370900000F95C2C0E80424013AD07408488BCDE80070C0FF498B4F10488B01FF9090000000BA2B01000041B802000000488BC8E870A8B1FF85C00FB683370900000F95C2C0E80524013AD07408488BCDE85370C0FF0FB705FE0BE401488D0DB5E2E101418BB6180300008B15B800E20166898424A8000000"), new(0xCABD50, "48897C247889B424B8000000E8DF03BCFF4C8BE84885C0747E488B0833D24C8B4128488BC841FFD0488BF84885C07467488B10488BC8FF523085C0745A488B17488BCFFF52308BF0B883DE1B43F7E6C1EA1169CA20A107002BF1488BCF89B424B8000000E8078D53FF448BC085C07427F68424A800000001751DB883DE1B43418BF041F7E0C1EA1169CA20A107002BF189B424B80000004139B618030000740A33D2498BCFE8F62B00000FB6837E0D0000410FB6961C030000C0E80624013AC2740D498BCEE8562AD2FFE9D701000033F64D85ED0F846F0100008BFE0F1F40008D47FBA9F7FFFFFF0F8433010000498B45008BD7498BCDFF5028488BD84885C00F840F010000488B10488BC8FF523085C00F84FE00000083FF0175248BD7488D0D7BE1E101E8869FBCFF4885C00F84EE00000080B897000000030F85E100000033D2488BCBE8A6CBBBFFBA01000000488BCB440FB6F8E895CBBBFF0FB78C24A8000000440FB6E00FA3F98BEE722C4038730E7421488BCBE814C6BBFF4885C07414488BCBE807C6BBFF488BC8E8DF8B53FF8BE8EB0B8B6B3CEB064532E44532FF488B03488BCBFF5030896C2428450FB6CF448BC04488642420400FB6D7498BCEE8BB25D2FF84C07558488B03488BCBFF5030C644243001450FB6CF448BC0896C2428400FB6D74488642420498BCEE82D26D2FF488B03488BCBFF9080000000D0E8400FB6D72401498BCE440FB6C0E86D28D2FFEB0C400FB6D7498BCEE81F24D2FFFFC783FF0E0F8CB4FEFFFF8B8424B8000000488B6C24404C8B7C24484C8B64245041898618030000498B942450090000488BCDE8277CC0FF498D8C24F8060000E85A73C0FF0FB6D0488BCDE81F73C0FF0F1F40006666660F1F8400000000000FB7D6498D8C24F8060000E8D075C0FF440FB7C00FB7D6488BCDE8E175C0FFFFC683FE0272DA488B7C24784C8B6C2468488BB42480000000488BAC24A0000000"), new(0xCAC010, "498BCFE8581D00004C8B6424704C8B7C24604881C488000000415E5B"), new(0xCAC02C, "C3") },
            new PortraitOwnedSceneProofSpan[] { new(0x3040040, "70BBCA008EBBCA00489A6C02"), new(0x26C9A48, "01130400130111000CE00A30"), new(0x304004C, "8EBBCA00DBBBCA00549A6C02"), new(0x26C9A54, "2105020005F40C0070BBCA008EBBCA00489A6C02"), new(0x3040058, "DBBBCA001EBCCA00689A6C02"), new(0x26C9A68, "2105040005C40E0000F40C0070BBCA008EBBCA00489A6C02"), new(0x3040064, "1EBCCA0050BDCA00809A6C02"), new(0x26C9A80, "2115060015D40D001064100008541400DBBBCA001EBCCA00689A6C02"), new(0x3040070, "50BDCA0010C0CA009C9A6C02"), new(0x26C9A9C, "2105020005740F001EBCCA0050BDCA00809A6C02"), new(0x304007C, "10C0CA002CC0CA00B09A6C02"), new(0x26C9AB0, "21000000DBBBCA001EBCCA00689A6C02"), new(0x3040088, "2CC0CA002DC0CA00C09A6C02"), new(0x26C9AC0, "2100000070BBCA008EBBCA00489A6C02") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "F6C2010F84B3040000", new PortraitOwnedSceneProofFixup[] { new(5, 9, 0xCAC02C) }), new(false, "F6C2010F84B30400005341564881EC88000000", new PortraitOwnedSceneProofFixup[] { new(5, 9, 0xCAC02C) })),
        new(PortraitOwnedSceneNativeKind.Update, 0xcbe640, true,
            new PortraitOwnedSceneProofSpan[] { new(0xCBE640, "4C8BDC5541574881EC88000000488B0504C0C1014833C4488944246080B91803000000498BE84C8BF90F841C010000"), new(0xCBE66F, "49895B1049897320498BF049897BE8488D3D1B57E1014D8963E04533E44D8973D8418BDC482BF749BEFFFFFFFFFF000000488B0748894424404833843EC80800004985C67416488D8DF80600004533C94C8D4424408BD3E83542BFFFFFC34883C70883FB0A7CCA488B05B356E1014C8D7424484889442448488DB510070000488B05A356E101498D9F200300004889442450418BFC4C896424584D2BF70F1F40004D8B841EE0FCFFFF4C3903743D4489642438488D8DF806000044886424304533C944886424288BD7C6442420014C8903E83B21BFFF488B0E4885C9740D488B01498B5718FF90F8000000FFC74883C3084883C67083FF037CA74C8B7424704C8B642478488BBC2480000000488BB424B8000000488B9C24A8000000"), new(0xCBE78B, "488B4C24604833CCE848F90D014881C488000000415F5DC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x304130C, "40E6CB006FE6CB0084B66C02"), new(0x26CB684, "191C04000D01110006F00450E8DFD901"), new(0x3041318, "6FE6CB008BE7CB0098B66C02"), new(0x26CB698, "21210A0021E40E001AC40F000F741000086417000434150040E6CB006FE6CB0084B66C02"), new(0x3041324, "8BE7CB00A3E7CB00BCB66C02"), new(0x26CB6BC, "2100000040E6CB006FE6CB0084B66C02") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "4C8BDC554157", new PortraitOwnedSceneProofFixup[] {  }), new(false, "4C8BDC5541574881EC88000000488B0504C0C101", new PortraitOwnedSceneProofFixup[] { new(16, 20, 0x28DA658) })),
        new(PortraitOwnedSceneNativeKind.Update, 0xdbbc60, true,
            new PortraitOwnedSceneProofSpan[] { new(0xDBBC60, "4C894424185541574881EC9800000080B91A030000004D8BF8488BE9744A80B90903000000754180B90A03000000753880B91903000000752F498B00498BC8FF90C000000083B8F802000000751A4533C0C6851903000001498B07498BCF418D5006FF903801000080BD18030000000F845306000080BD09030000000F8546060000488BCDE88620C1FF84C00F8436060000"), new(0xDBBCF2, "48899C24B8000000BA020000004889B424900000004533C04889BC24880000008BBD1C030000C1EF044C89A424800000004080E7014D8DA7300A0000897C2458498BCC4C896C24784C89642468E8EC3AB0FFB90100000084C075168BD14533C0498BCCE8D63AB0FF84C075054532EDEB0341B501488B9D200300004032F644886C24404C897424704885DB0F841D0500000FB6851C03000033FF2401837B0C0188442448740C84C0757439BB64380000756C8B155600D101488D0D3FE2D001E88A03ABFF4885C07455488B0833D24C8B4128488BC841FFD0488BF04885C0743E488B10488BC8FF5230448BF085C0742E8BC8E8279BA7FF4885C00F840C050000488BD0418BCEE8E3C6ACFF85C0750F488B06488BCEFF50308BF885C0750FF683743800000A75068BBB6438000032C0888424C800000085FF741B8BCFE8DD9AA7FF4885C00F84C20400000FB64055888424C800000038851B03000074254584ED0F85A604000041F6877E0D00004074124533C033D2498BCCE80134B0FFE98A040000C6850903000001488D0D6EE1D0018B1578FFD001E8B302ABFF448B851C030000498D8FF806000041D1E833D24180E0014C8BE8E8B46CAFFF8B951C030000498D8FF8060000C1EA0280E201E84C5CAFFF8B951C030000498D8FF8060000C1EA0380E201E8146EAFFF8B951C030000498D8FF8060000C1EA06F6D280E201E88A6EAFFF488B852003000033FF440FB70D2B0AD301897C244C44894C2454448B400C488D0535E44001488944246044894424508B18488D0DE2B24001488B852003000041BC01000000440FB63C99486BCB3C4488BC24B00000004803C1418BCF41D3E44523E1488D90643800004183F8010F856B0100004D85ED0F8498020000498B45008BD1498BCDFF5028488BF04885C00F8480020000488B10488BC8FF5230448BF885C00F84630200004C8B8520030000B8010000008BD08BCBD3E24532F6418590C83B0000740F418590CC3B0000450FB6F6440F45F066897C244432DB488D7C2444660F1F8400000000000FB6D3488BCEE855CAAAFFFEC38807488D7F0180FB0272E8807E0E007421488BCEE8DAC4AAFF4885C07414488BCEE8CDC4AAFF488BC8E8A58A42FF8BD8EB038B5E3C85DB74538BCBE8E398A7FF4885C00F84C80200004585E4751B448B85E40000004C8BC88B95E00000008BCBE89EBDACFF85C0742333FF8BDF32C00F1F40000FB6C84883F9020F83C6020000FEC0885C0C443C0272E9EB0233FF418BCFE88D98A7FF4885C00F8472020000488BD0418BCFE849C4ACFF85C00F85620100004584F60F45DF74068BC78BCFEB0A0FB64424450FB64C24440FB69424B0000000440FB6C9C644243001458BC7895C2428E977010000448B124585D20F85E800000044385424480F851F0100004D85ED0F8416010000498B45008BD1498BCDFF5028488BF04885C00F84FE000000488B10488BC8FF5230448BF085C00F84EA0000006689BC24B000000032DB488DBC24B00000000FB6D3488BCEE803C9AAFFFEC38807488D7F0180FB0272E8807E0E007421488BCEE888C3AAFF4885C07414488BCEE87BC3AAFF488BC8E8538942FF8BD8EB038B5E3C33FF4585E4741F8BDF32C0900FB6C84883F9020F83A6010000FEC0889C0CB00000003C0272E6418BCEE86E97A7FF4885C00F8453010000488BD0418BCEE82AC3ACFF85C07550C644243001458BC6895C2428EB6EF680743800000A75394805983800006689BC24B00000004C8D8424B00000004C2BC2BA0200000090807802007522803800751D0FB648E4EB1B440FB6BC24B0000000410FB6D7488BCDE87A21C1FFEB3F0FB648E641884C00CC48FFC04883EA0175C6C644243001458BC2897C2428440FB68C24B0000000410FB6D70FB68424B1000000488BCD88442420E81923C1FF448B44244C488B44246041FFC04883C004448944244C48894424604183F80C730F448B442450448B4C2454E99EFCFFFF4C8BBC24C00000008B7C24580FB6B424C80000004C8B6424684038B51B030000741B4084FF74164533C033D2498BCCE8B52FB0FF4088B51B030000EB33807C244000C6851803000000C6851A03000001750C400FB6D7488BCDE88B25C1FF410FB6877E0D0000C0E8062401403AC77407C68518030000014C8B7424704C8BA42480000000488BBC2488000000488BB42490000000488B9C24B80000004C8B6C2478"), new(0xDBC328, "4881C498000000415F5DC3"), new(0xDBC333, "E80C1AFE00CC") },
            new PortraitOwnedSceneProofSpan[] { new(0x3052DA8, "60BCDB00F2BCDB0008636E02"), new(0x26E6308, "010F04000F01130008F00650"), new(0x3052DB4, "F2BCDB0028C3DB0014636E02"), new(0x26E6314, "21880C0088E40E0048D40F0031C4100020741100156412000834170060BCDB00F2BCDB0008636E02"), new(0x3052DC0, "28C3DB0033C3DB003C636E02"), new(0x26E633C, "2100000060BCDB00F2BCDB0008636E02"), new(0x3052DCC, "33C3DB0039C3DB004C636E02"), new(0x26E634C, "21000C0000E40E0000D40F0000C4100000741100006412000034170060BCDB00F2BCDB0008636E02") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "4C89442418", new PortraitOwnedSceneProofFixup[] {  }), new(true, "4C894424185541574881EC98000000", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Update, 0xf469c0, true,
            new PortraitOwnedSceneProofSpan[] { new(0xF469C0, "488BC44C8940185641574881EC9800000080B924030000004D8BF8488BF10F846F060000"), new(0xF469E4, "4889581033D2"), new(0xF469EA, "488968E8488978E0498DB8F8060000440FB68128030000C6812403000000488BCF4C8960D84C8970C848897C2460E843C196FF0FB69629030000488BCFE8E4B096FF0FB6962A030000488BCFE8B5C296FF0FB6962B030000488BCFE836C396FF8B9620030000488D0D999F9601E8045CBBFF4533E44889442458BD010000004C8BF089AC24C80000004885C00F849C000000488B10488BC8FF920801000084C00F8488000000410FB69658090000440FB6C5488BCEE86C76A8FF498B9650090000488BCFE81DD196FF498D8EF8060000E851C896FF0FB6D0488BCFE816C896FF418BDC0F1F004533C00FB7D3488BCFE8F2CA96FFFFC383FB0272EB498D8EA0010000E8CF7B95FF8BC833D2E8A6BA19FF8BC8E86FE48EFF4885C074120FB6485AE8B1A219FF8BE8898424C80000004C8B46304C89442450418B40308BD083EA05744183FA01743C83F807742E83C0F783F80276264963406485C00F8864040000413B40600F8D5A040000488BC8498B80A8000000488B14C8EB10498D90D0000000EB07498D90C80000004C8B324C897424684D85F60F8429040000498B06498BCEFF503085C00F8418040000498B06498BCEFF50308BC8E86AED8EFF4885C00F84F80300000FB6889A000000"), new(0xF46BB6, "4C896C2478E810151CFF4439A618030000488D0D2234B8010FB73D5D5DBA01BAF82A00000F44151F52B801448BE8894424446689BC24B0000000E84B5592FF4889442448488BD84885C00F84170300004438A6250300000F8595020000458BFC418BFC0F1F800000000085FF75114183FD01751D413BED0F8522010000EB1283FF01750D4438A62E0300000F840E010000488B038BCF41BE010000008BD741D3E6488BCB450BFE44897C2440FF5028488BD84885C00F84DF000000488B10488BC8FF523085C00F84CE00000033D2488BCBE8B41D92FFBA01000000488BCB440FB6E8E8A31D92FF0FB78C24B00000000FB6E8458BFC4185CE752E4438630E7422488BCBE8221892FF4885C07415488BCBE8151892FF488BC8E8EDDD29FF448BF8EB0C448B7B3CEB064032ED4532ED4438A6260300000FB68E2F030000448BF1410FB6C5440F44F04438A627030000741583BE1C030000027C090FB6AE30030000EB030FB6E9488B03488BCBFF5030C644243001450FB6CE448BC044897C2428400FB6D740886C2420488BCEE82A78A8FF448B7C2440448B6C2444488B5C24488BAC24C8000000FFC783FF0E0F8CB9FEFFFF488B5C2450BA9B000000488BCBE887CC53FF8B5330488BF883EA05740983FA010F85D10000004885FF0F84C8000000488B10488BCFFF523084C00F84B7000000418BDC660F1F4400008BD3488BCFE8C62FD3FF410FA3DF488BC80F838C0000004885C00F8483000000448B500C4585D2747A0FB6500A84D274060FB64004EB040FB640024438A626030000440FB68E2F030000458BC10FB6C0440F44C084D274060FB65105EB040FB651034438A627030000741683BE1C030000027C090FB69630030000EB04410FB6D18B4110450FB6C8C644243001458BC289442428488BCE885424200FB6D3E81D77A8FFFFC383FB0E0F8C52FFFFFF488B5C2458488B7C24600F1F840000000000410FB7D4488D8BF8060000E830C796FF440FB7C0410FB7D4488BCFE840C796FF41FFC44183FC0272D74C8B7424684C8BBC24C0000000EB754439A618030000756C83FD017567443BED756233D2488D0D2C31B801E837EF92FF4885C0744F80B89A0000000D7446488B0333D2488BCBFF5028488BD84084FD750B488BC8E8CEDB29FF448BE0488B13488BCBFF5230C6442430004533C9448BC0448964242833D2C644242000488BCEE85376A8FF41807E0E007421498BCEE8B41592FF4885C07414498BCEE8A71592FF488BC8E87FDB29FF8BE8EB04418B6E3C498B06498BCE0FB69E300300000FB6BE2F030000FF5030C644243001440FB6CF448BC0896C2428410FB6D5885C2420488BCEE8F075A8FF498B06498BCEFF9080000000D0E8410FB6D52401488BCE440FB6C0E83078A8FF4C8B6C2478EB07"), new(0xF46FA7, "C6862403000001BA02000000498D8F300A00004533C0E86E8897FF4C8B7424704C8BA42480000000488BBC2488000000488BAC249000000084C0751D"), new(0xF46FE3, "BA01000000498D8F300A00004533C0E8398897FF84C075054532C0EB0341B001410FB68F7E0D00000FB6962D0300000FB6C1488B9C24B8000000C0E80624013AC2742D"), new(0xF47026, "4584C07517488BCEE83D78A8FF410FB68F7E0D00000FB6962D030000C0E90680E1013ACA7407C68624030000014881C498000000415F5EC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x306AD84, "C069F400E469F400E48A7002"), new(0x2708AE4, "01110400110113000AF00860"), new(0x306AD90, "E469F400EA69F400F08A7002"), new(0x2708AF0, "2104020004341700C069F400E469F400E48A7002"), new(0x306AD9C, "EA69F400B66BF400048B7002"), new(0x2708B04, "2129080029E40E0025C410000874110004541200E469F400EA69F400F08A7002"), new(0x306ADA8, "B66BF400A76FF400248B7002"), new(0x2708B24, "2105020005D40F00EA69F400B66BF400048B7002"), new(0x306ADB4, "A76FF400E36FF400388B7002"), new(0x2708B38, "21000000EA69F400B66BF400048B7002"), new(0x306ADC0, "E36FF4002670F400488B7002"), new(0x2708B48, "21000000E469F400EA69F400F08A7002"), new(0x306ADCC, "2670F4005E70F400588B7002"), new(0x2708B58, "21000000C069F400E469F400E48A7002") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "488BC44C894018", new PortraitOwnedSceneProofFixup[] {  }), new(true, "488BC44C8940185641574881EC98000000", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Update, 0xffa780, true,
            new PortraitOwnedSceneProofSpan[] { new(0xFFA780, "48895C2420565741554883EC6080B919030000004D8BE8488BD90F8432010000"), new(0xFFA7A0, "4C89BC249000000041BF38000000418BFFC68119030000000F1F840000000000488B8328030000488B043848C1E82085C074138BC8E836B183FF4885C07507C68319030000014883C71C4881FFC00100007CCD80BB19030000000F85CA000000"), new(0xFFA800, "4889AC248000000033FF4C89B424880000000F1F400066660F1F840000000000488B8328030000420F100C38660F6FC166480F7ECE660F73D80866410F7EC6660F6FC1660F73D80866480F7EC548C1EE2048C1ED080F114C244085F6742A8BCEE8ABB083FF488B8B280300004C8BC08B5424440FB64931E804DB88FF85C0740833F64532F64032ED448B442440450FB6CEC644243001400FB6D789742428488BCB40886C2420E8C53C9DFFFFC74983C71C83FF0E0F8C66FFFFFF4C8BB42488000000488BAC2480000000"), new(0xFFA8CA, "4C8BBC2490000000"), new(0xFFA8D2, "80BB18030000000F8409010000440FB6831A030000498DBDF8060000488BCFC683180300000033D2E861828BFF0FB6931B030000488BCFE802728BFF0FB6931C030000488BCFE8D3838BFF0FB6931D030000488BCFE854848BFF488B832803000033D2488BCF440FB780C0010000E88B8C8BFF488B3544C5AA014885F67427488B06488BCEFF900801000084C07417488D8EF8060000E8A3898BFF0FB6D0488BCFE868898BFF410FB6857E0D0000C0E80624013A831F030000745BBA02000000498D8D300A00004533C0E88F4E8CFF84C07527BA01000000498D8D300A00004533C0E8774E8CFF84C0750F0FB6931F030000488BCBE8A43E9DFF410FB6857E0D0000C0E80624013A831F0300007407C6831803000001488B9C24980000004883C460415D5F5EC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x3075BA8, "80A7FF00A0A7FF00CC9A7102"), new(0x2719ACC, "010D06000D3413000DB209D007700660"), new(0x3075BB4, "A0A7FF0000A8FF00DC9A7102"), new(0x2719ADC, "2108020008F4120080A7FF00A0A7FF00CC9A7102"), new(0x3075BC0, "00A8FF00CAA8FF00F09A7102"), new(0x2719AF0, "2112040012E4110008541000A0A7FF0000A8FF00DC9A7102"), new(0x3075BCC, "CAA8FF00D2A8FF00089B7102"), new(0x2719B08, "21000000A0A7FF0000A8FF00DC9A7102"), new(0x3075BD8, "D2A8FF00F9A9FF00189B7102"), new(0x2719B18, "2100000080A7FF00A0A7FF00CC9A7102") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2420", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2420565741554883EC6080B91903000000", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Update, 0x10d6c30, true,
            new PortraitOwnedSceneProofSpan[] { new(0x10D6C30, "4889742410574883EC20F6812003000020498BF0488BF90F84E3000000BA02000000"), new(0x10D6C52, "48895C24304533C0488D8E300A0000E8CA8B7EFF84C00F85BD000000BA01000000488D8E300A00004533C0E8AE8B7EFF84C00F85A100000080A720030000DF488D8EF80600000FB6972003000080E201E869AE7DFF440FB68720030000488D8EF806000041D0E833D24180E001E89CBE7DFF0FB69720030000488D8EF8060000C0EA0280E201E813C07DFF0FB69720030000488D8EF8060000C0EA0380E201E88AC07DFF0FB69720030000488BCFC0EA0480E201E8657B8FFF0FB6867E0D00000FB68F20030000C0E80232C1A810740980C920888F20030000488B5C2430"), new(0x10D6D30, "488B7424384883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x3082A24, "306C0D01526C0D0188D87202"), new(0x272D888, "010A04000A6407000A320670"), new(0x3082A30, "526C0D01306D0D0194D87202"), new(0x272D894, "2105020005340600306C0D01526C0D0188D87202"), new(0x3082A3C, "306D0D013B6D0D01A8D87202"), new(0x272D8A8, "21000000306C0D01526C0D0188D87202") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "4889742410", new PortraitOwnedSceneProofFixup[] {  }), new(true, "4889742410574883EC20F6812003000020", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Setup, 0x9d3960, true,
            new PortraitOwnedSceneProofSpan[] { new(0x9D3960, "48895C240848896C24104889742418574883EC40498BF8C6810604000000448BC2C7411401000000498BF133D24533C9488BD9E83899FFFF0F57C0488D0D8EAC11020F11434833C00F1143580F1143680F1143780F1183880000000F1183980000000F1183A80000000F1183B80000000F1183C8000000488983D80000000F10078B530C0F114348F20F104F10F20F114B580FB74718668943600FB647758883D00000000FB647768883D10000000FB647778883D20000000FB647788883D30000000FB64774668983640300000F10070F1183880300000F104F100F118B980300000F1047200F1183A80300000F104F300F118BB80300000F1047400F1183C80300000F104F500F118BD80300000F1047600F1183E8030000F20F104F70F20F118BF80300008B47788983000400008B442470898318040000488B442478488983200400004889B310040000E8AF30D800488BF84885C07441488BC8E81F5AF3FF4C8B0F4533C033D2488BCF41FF91480100004C8B0F4533C0488BCF418D500641FF9148010000488B074533C0488BCF418D5003FF9048010000488D3D3FAA7C01BE0E00000033ED0F1F8400000000008B0F448B848BA40300004585C074290FB68419EA0300000FB6D1440FB68C19DC030000488BCBC644243001896C242888442420E828AAFFFF4883C7044883EE0175BE0FB783F8030000488B6C2458488B742460668943620FB783FA03000066894364488B5C24504883C4405FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x300EBAC, "60399D007D3B9D0054356802"), new(0x2683554, "0114080014640C0014540B0014340A0014721070") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C240848896C24104889742418", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.Render, 0x9cd4d0, true,
            new PortraitOwnedSceneProofSpan[] { new(0x9CD4D0, "48897C242041564883EC2083790800448BF2488BF90F840A0300008B510C85D20F88FF020000488D0D33111202"), new(0x9CD4FD, "4889742440E85996D800488BF04885C00F84DD0200008B5708"), new(0x9CD516, "48895C243083EA010F847201000083EA010F846602000083EA010F847402000083EA020F849D01000083FA010F85A302000080BF0903000000"), new(0x9CD54F, "48896C24387423488BD0488BCFE84F16000084C00F94C1888F0903000084C07448C6870A03000001EB3F80BF0A030000007436488B00488BCEFF90C00000004885C07425488B06488BCEFF90C000000083B8F8020000007510488B07488BCFFF5048C6870A030000000FB68F0903000084C9750C388F0A030000750433DBEB05BB010000000FB68637090000C1E80683E0013BC3741E84C9750C388F0A030000750433D2EB02B201488D8EF8060000E8ED5EEEFF488D8EF8060000E8E144EEFF84C0742D488B9EF00700004885DB7421488B03488BCBFF90F00000004885C07510488B03488BCB488B5718FF90F8000000488D8EF8060000E85449EEFF488B6C243884C0742D"), new(0x9CD655, "488B9E600800004885DB7421488B03488BCBFF90F00000004885C07510488B03488BCB488B5718FF90F8000000488B074C8BC6418BD6488BCFFF5050E955010000488B0D7302F301E87E5970FF4885C00F8440010000488B10488BC8488B1D18810C02FF5240488D8828A90200E8F91EC7FF8B57104C8BC0488BCBE89BD189FFC7470805000000488B057502F301BA20000000488B4828448D42F0488B01FF501033DB4885C0740A488BC8E81BB889FFEB03488BC3448B4714488BC88B571048894718E803B989FF488B053402F301BA0001000041B810000000488B4830488B01FF50104885C0740B488BC8E85A1EA5FF488BD8488BCB48895F20E8AB1FA5FF488B07488BCFFF5018488B57204885D27412488B82E0000000C6800602000000488B5720488B05F801F30148634F10488994C8A0000000C7470802000000488B07488BD6488BCFFF503884C07448C7470803000000488B07488BD6488BCFFF505884C07431488B07488BCFC7470806000000FF5040488B5F384885DB74188B5710488B0DF37F0C02E86ED089FF488B4F30488BD0FFD3488B5C2430"), new(0x9CD7F0, "488B742440"), new(0x9CD7F5, "488B7C24484883C420415EC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x300E4EC, "D0D49C00FDD49C00882B6802"), new(0x2682B88, "010B04000B7409000B3207E0"), new(0x300E4F8, "FDD49C0016D59C00942B6802"), new(0x2682B94, "2105020005640800D0D49C00FDD49C00882B6802"), new(0x300E504, "16D59C004FD59C00A82B6802"), new(0x2682BA8, "2105020005340600FDD49C0016D59C00942B6802"), new(0x300E510, "4FD59C0055D69C00BC2B6802"), new(0x2682BBC, "210502000554070016D59C004FD59C00A82B6802"), new(0x300E51C, "55D69C00F0D79C00D02B6802"), new(0x2682BD0, "2100000016D59C004FD59C00A82B6802"), new(0x300E528, "F0D79C00F5D79C00E02B6802"), new(0x2682BE0, "21000000FDD49C0016D59C00942B6802"), new(0x300E534, "F5D79C0001D89C00F02B6802"), new(0x2682BF0, "21000000D0D49C00FDD49C00882B6802") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48897C2420", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48897C242041564883EC2083790800", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.DestroyContext, 0x2edfc0, true,
            new PortraitOwnedSceneProofSpan[] { new(0x2EDFC0, "40534883EC20488D0513DAE201488BD9488901F6C201742CF6C204751D488B0574F96002488BD3488B4828488B01FF5020488BC34883C4205BC3BA20000000E8FCD1D6FF488BC34883C4205BC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x2FA9F80, "C0DF2E000DE02E00B80F5F02"), new(0x25F0FB8, "0106020006320230") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "40534883EC20", new PortraitOwnedSceneProofFixup[] {  }), new(false, "40534883EC20488D0513DAE201488BD9", new PortraitOwnedSceneProofFixup[] { new(9, 13, 0x211B9E0) })),
        new(PortraitOwnedSceneNativeKind.ConstructContext, 0x268f20, false,
            new PortraitOwnedSceneProofSpan[] { new(0x268F20, "488D05A117EE01C741080100000048890133C048894110884118488BC1C3CC") },
            new PortraitOwnedSceneProofSpan[] {  },
            new PortraitOwnedSceneProofSpan[] { new(0x268F3E, "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC") }, new(true, "488D05A117EE01", new PortraitOwnedSceneProofFixup[] { new(3, 7, 0x214A6C8) }), new(false, "488D05A117EE01C7410801000000", new PortraitOwnedSceneProofFixup[] { new(3, 7, 0x214A6C8) })),
        new(PortraitOwnedSceneNativeKind.CleanupContext, 0x2690b0, false,
            new PortraitOwnedSceneProofSpan[] { new(0x2690B0, "8B411083F808731E8BD0488B055F35690283AC905C83030001790BC784905C83030000000000C6411800C3CC") },
            new PortraitOwnedSceneProofSpan[] {  },
            new PortraitOwnedSceneProofSpan[] { new(0x2690DB, "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC") }, new(true, "8B411083F808", new PortraitOwnedSceneProofFixup[] {  }), new(false, "8B411083F808731E8BD0488B055F356902", new PortraitOwnedSceneProofFixup[] { new(13, 17, 0x28FC620) })),
        new(PortraitOwnedSceneNativeKind.ConfigureContext, 0x269020, true,
            new PortraitOwnedSceneProofSpan[] { new(0x269020, "48895C2408574883EC2080791800488BD98BFA74298B411083F8087321448BC8488B05D93569024283AC885C83030001790C42C784885C830300000000008979108BD744894114488B0D823E69024881C120010000E83688000083FF08730E488B059A356902FF84B85C830300B001884318488B5C24304883C4205FC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x2FA58B8, "209026009D90260098A25E02"), new(0x25EA298, "010A04000A3406000A320670") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48895C2408", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48895C2408574883EC2080791800", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.ConfigureDefaultContext, 0x268f60, false,
            new PortraitOwnedSceneProofSpan[] { new(0x268F60, "4533C94C8BC14438491874218B411083F8087319488B0DA536690283AC815C83030001790844898C815C830300418950104589481483FA0777794863C24883C02B8BCA48C1E006480305423F6902448908C7400483C02A3FC74010C3D3133FC74014C3D3133FC74018C3D3133FC74020CDCC4C3EC74024CDCC4C3EC74028CDCC4C3EC7402C0000803FC740300000803FC740340000803FC740380000803FC7403C0000803F488B0514366902FF84885C830300B00141884018C3CC") },
            new PortraitOwnedSceneProofSpan[] {  },
            new PortraitOwnedSceneProofSpan[] { new(0x26901A, "CCCCCCCCCCCC") }, new(true, "4533C94C8BC1", new PortraitOwnedSceneProofFixup[] {  }), new(false, "4533C94C8BC14438491874218B4110", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.CreateClientObject, 0x1756410, true,
            new PortraitOwnedSceneProofSpan[] { new(0x1756410, "44884424185657415441554883EC284863FA488BF181FFF90000007D3E4533ED83FFFF754D418BFD488D4118418BD59044382875144584C074314881FAEF0000007D06443868107422FFC748FFC24883C0104881FAF00000007CD5B8FFFFFFFF4883C428415D415C5F5EC383FFFF74EBEB1A4584C0741581FFF80000007DDC488BC74803C044386CC12875CF48895C24508BC70FAF4108"), new(0x17564A7, "48896C24584C897424684C897C24204C63F84C0339498BCFE8CC3618FF498D9F7023000033D2488D0514D2A40041B890010000498907488D05C4D4A400498987A00100008D4A104C892B4C896B084C896B104C896B184C896B20E8CAF193FE498D8FB02300004C896808488903488918488B432048034318488943384C896B284C896B30488B0348894328E839D114FF4D8DB7002900004D89AFB0270000498D5E304D89AFB8270000BD2000000041BC000000E00F1F440000BA0800000045892E448BC244896BD4488D4B086644896BD84C8D0DA1773AFF44896BDC6644896BE04C8963E844886BF04C8963F844882BE8944B90FE4D8D7678488D5B784883ED0175B6498D8F00380000E8DA182A008D97C80000004533C9458BC4498BCFE8566E14FF4C8B742468488B6C2458440FB664246041C68790000000034863DF488D43014803C04C893CC6C644C6080144886CC60C8D8710FFFFFF83F8087708"), new(0x1756605, "498BCFE8B32E1BFF4584E474358D470181FFF7000000772A0FAF46084898480306498987F8220000488D43024803C04C892CC6C644C6080366897CC60E44886CC60CB201488D0D60A71501E80B8F3AFF4C8B7C24208BC7488B5C24504883C428415D415C5F5EC3") },
            new PortraitOwnedSceneProofSpan[] { new(0x30E9D74, "10647501A764750174177C02"), new(0x27C1774, "0191070091340A000F420BD009C0077006600000"), new(0x30E9D80, "A76475010566750188177C02"), new(0x27C1788, "210F06000FF404000AE40D0005540B0010647501A764750174177C02"), new(0x30E9D8C, "056675016C667501A4177C02"), new(0x27C17A4, "2100020000F4040010647501A764750174177C02") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "4488442418", new PortraitOwnedSceneProofFixup[] {  }), new(true, "44884424185657415441554883EC28", new PortraitOwnedSceneProofFixup[] {  })),
        new(PortraitOwnedSceneNativeKind.DeleteClientObject, 0x1756840, true,
            new PortraitOwnedSceneProofSpan[] { new(0x1756840, "48896C242041564883EC204863EA4C8BF181FDF80000000F87EF000000"), new(0x175685D, "4889742438488BF548C1E6044803F10FB646183C030F84CF000000"), new(0x1756878, "48897C2440488B7E104885FF0F84B80000003C01750A807E1C000F85AA000000"), new(0x1756898, "48895C24304584C07411488B0741B00133D2488BCFFF9048010000488B07488BCFFF5010488B17488BCF8D58FFFF92E001000083FB0C7724488D0D29978AFE4863C38B9481586975014803D1FFE2488B0733D2488BCFFF90E8010000488B5C2430498D461C4533C0BAF90000004C894610448846184488461C"), new(0x1756911, "8078FC0375130FB748023BCD750B4C8940F4448840FC4488004883C0104883EA0175DDB201488D0D73A41501E82E8C3AFF488B7C2440"), new(0x1756947, "488B742438"), new(0x175694C, "488B6C24484883C420415EC3E6687501E6687501E6687501E6687501E6687501E6687501E6687501E6687501F4687501F4687501E6687501E6687501E6687501") },
            new PortraitOwnedSceneProofSpan[] { new(0x30E9DB0, "406875015D687501D8177C02"), new(0x27C17D8, "010B04000B5409000B3207E0"), new(0x30E9DBC, "5D68750178687501E4177C02"), new(0x27C17E4, "2105020005640700406875015D687501D8177C02"), new(0x30E9DC8, "7868750198687501F8177C02"), new(0x27C17F8, "21050200057408005D68750178687501E4177C02"), new(0x30E9DD4, "98687501116975010C187C02"), new(0x27C180C, "21050200053406007868750198687501F8177C02"), new(0x30E9DE0, "116975014769750120187C02"), new(0x27C1820, "210000007868750198687501F8177C02"), new(0x30E9DEC, "476975014C69750130187C02"), new(0x27C1830, "210000005D68750178687501E4177C02"), new(0x30E9DF8, "4C6975018C69750140187C02"), new(0x27C1840, "21000000406875015D687501D8177C02") },
            new PortraitOwnedSceneProofSpan[] {  }, new(true, "48896C2420", new PortraitOwnedSceneProofFixup[] {  }), new(true, "48896C242041564883EC204863EA", new PortraitOwnedSceneProofFixup[] {  })),
    });
    internal static IReadOnlyList<PortraitOwnedSceneProofVTable> VTables { get; } = Array.AsReadOnly(new PortraitOwnedSceneProofVTable[] {
        new(0x219f838, new uint[] { 0x9D9180, 0x9CD2D0, 0x9CD3E0, 0x9CD810, 0x9CD990, 0x9CD9F0, 0x9CDA50, 0x9CDE30, 0x9DA930, 0x9DA580, 0x9DB610 }),
        new(0x219f8a0, new uint[] { 0x9D9230, 0x9CD2D0, 0x9CD3E0, 0x9CF980, 0x9CD990, 0x9CD9F0, 0x9CDA50, 0x9CFBF0, 0x9DA930, 0x9DA580, 0x9CFB10 }),
        new(0x219fab8, new uint[] { 0x9D9130, 0x9CD2D0, 0x9D3B90, 0x9D3F70, 0x9D40D0, 0x9D4200, 0x9D4300, 0x9D3CB0, 0x9D5FF0, 0x9DA580, 0x9D3D30 }),
        new(0x21c3800, new uint[] { 0xD15A10, 0x9CD2D0, 0x9CD3E0, 0x9CD810, 0x9CD990, 0x9CD9F0, 0x9CDA50, 0x9CDE30, 0x9DA930, 0x9DA580, 0xC78140 }),
        new(0x21c4388, new uint[] { 0xD159D0, 0xCABB40, 0x9CD3E0, 0x9CD810, 0x9CD990, 0x9CD9F0, 0x9CDA50, 0x9CDE30, 0x9DA930, 0x9DA580, 0xCABB70 }),
        new(0x21c46a8, new uint[] { 0xD15990, 0xCBE5E0, 0x9CD3E0, 0x9CD810, 0x9CD990, 0x9CD9F0, 0x9CDA50, 0x9CDE30, 0x9DA930, 0x9DA580, 0xCBE640 }),
        new(0x21ca220, new uint[] { 0xDE4710, 0xDBB9C0, 0x9CD3E0, 0x9CD810, 0x9CD990, 0x9CD9F0, 0x9CDA50, 0x9CDE30, 0x9DA930, 0x9DA580, 0xDBBC60 }),
        new(0x21f73a0, new uint[] { 0xFA3530, 0x9CD2D0, 0x9CD3E0, 0x9CD810, 0x9CD990, 0x9CD9F0, 0x9CDA50, 0x9CDE30, 0x9DA930, 0x9DA580, 0xF469C0 }),
        new(0x21f8440, new uint[] { 0xFA3570, 0x9CD2D0, 0x9CD3E0, 0x9CD810, 0x9CD990, 0x9CD9F0, 0x9CDA50, 0x9CDE30, 0x9DA930, 0xFA6B10, 0x9DB610 }),
        new(0x21fa290, new uint[] { 0x10998E0, 0xFFA470, 0x9CD3E0, 0x9CD810, 0x9CD990, 0x9CD9F0, 0x9CDA50, 0x9CDE30, 0x9DA930, 0x9DA580, 0xFFA780 }),
        new(0x21fb010, new uint[] { 0x1099920, 0x9CD2D0, 0x9CD3E0, 0x9CD810, 0x9CD990, 0x9CD9F0, 0x9CDA50, 0x1035260, 0x9DA930, 0x9DA580, 0x9DB610 }),
        new(0x21fde80, new uint[] { 0x1104600, 0x9CD2D0, 0x10D6C10, 0x9CD810, 0x9CD990, 0x9CD9F0, 0x9CDA50, 0x9CDE30, 0x9DA930, 0x9DA580, 0x10D6C30 }),
        new(0x214a6c8, new uint[] { 0x2EDFC0, 0x268F50, 0x3356B0, 0x3356D0, 0x2690B0 }),
    });
}
// END GENERATED OWNED39 DISK PROOF
