using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>
/// Cold qualification of this process and loaded CN SDK. Construction reads disk,
/// never calls a game function. Construct outside a per-frame path. Subsequent
/// verification compares complete runtime code spans before every native boundary.
/// </summary>
internal sealed class NativeCompanionIdleGateCodeImage : INativeIdleGateCodeImage
{
    private readonly ICompanionFollowSnapshotReader reader;
    public bool IsFrameworkThread => reader.IsFrameworkThread;
    public nint ModuleBase { get; }
    public int ImageSize { get; }
    public string GameSha256 { get; }
    public string SdkSha256 { get; }
    public Guid SdkMvid { get; }

    internal NativeCompanionIdleGateCodeImage(ICompanionFollowSnapshotReader reader)
    {
        this.reader = reader;
        using var process = Process.GetCurrentProcess();
        var module = process.MainModule ?? throw new InvalidOperationException("Current native module unavailable.");
        var sdk = typeof(Companion).Assembly;
        ModuleBase = module.BaseAddress;
        ImageSize = module.ModuleMemorySize;
        GameSha256 = Hash(module.FileName);
        SdkSha256 = Hash(sdk.Location);
        SdkMvid = sdk.ManifestModule.ModuleVersionId;
    }

    public bool TryRead(nint address, Memory<byte> destination)
        => IsFrameworkThread && reader.TryRead(address, destination);

    public bool IsExecutable(nint address, int length)
    {
        if (!IsFrameworkThread || address <= 0 || length <= 0 || address > nint.MaxValue - length
            || VirtualQuery(address, out var region, (nuint)Marshal.SizeOf<MemoryRegion>()) == 0
            || region.State != 0x1000 || (region.Protect & (0x100 | 1)) != 0
            || (region.Protect & (0x10 | 0x20 | 0x40 | 0x80)) == 0
            || region.BaseAddress <= 0 || region.RegionSize > (nuint)nint.MaxValue
            || region.BaseAddress > nint.MaxValue - (nint)region.RegionSize) return false;
        return address >= region.BaseAddress && address + length <= region.BaseAddress + (nint)region.RegionSize;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryRegion
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nuint VirtualQuery(nint address, out MemoryRegion region, nuint length);
}
