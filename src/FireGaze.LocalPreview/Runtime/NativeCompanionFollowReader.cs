using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>Fallible synchronous copies only. Implementations must never advance native behavior.</summary>
public interface ICompanionFollowSnapshotReader
{
    bool IsFrameworkThread { get; }
    bool TryRead(nint address, Memory<byte> destination);
    bool TryReadNativeFrame(out uint frame);
}

internal sealed unsafe class NativeCompanionFollowReader(IFramework framework) : ICompanionFollowSnapshotReader
{
    public bool IsFrameworkThread => framework.IsInFrameworkUpdateThread;
    public bool TryRead(nint address, Memory<byte> destination)
    {
        if (!IsFrameworkThread || address <= 0 || destination.IsEmpty || address > nint.MaxValue - destination.Length) return false;
        fixed (byte* output = destination.Span)
            return ReadProcessMemory(-1, address, output, (nuint)destination.Length, out var read) && read == (nuint)destination.Length;
    }
    public bool TryReadNativeFrame(out uint frame)
    {
        frame = 0;
        return IsFrameworkThread
            && Read(this, (nint)NativeFramework.StaticAddressPointers.ppInstance, out nint nativeFramework)
            && nativeFramework > 0 && Read(this, nativeFramework + 0x16D0, out frame);
    }
    internal static bool LayoutValid()
    {
        try
        {
            return sizeof(Companion) == 0x2470 && sizeof(Character) == 0x2370 && sizeof(NativeFramework) == 0x35D8
                && Marshal.OffsetOf<NativeFramework>(nameof(NativeFramework.FrameCounter)).ToInt32() == 0x16D0
                && Marshal.OffsetOf<Companion>(nameof(Companion.Owner)).ToInt32() == 0x2400
                && Marshal.OffsetOf<Companion>(nameof(Companion.Behavior)).ToInt32() == 0x2394
                && Marshal.OffsetOf<Companion>(nameof(Companion.Special)).ToInt32() == 0x2395
                && Marshal.OffsetOf<Companion>(nameof(Companion.SpawnState)).ToInt32() == 0x2457
                && Marshal.OffsetOf<Companion>(nameof(Companion.BehaviorState)).ToInt32() == 0x2458
                && Marshal.OffsetOf<Companion>(nameof(Companion.BehaviorSpecial)).ToInt32() == 0x245A
                && sizeof(System.Numerics.Vector3) == 12
                && NativeCompanionDisplayActor.FollowLayoutValid();
        }
        catch (Exception) { return false; }
    }
    internal static bool Read<T>(ICompanionFollowSnapshotReader reader, nint address, out T value) where T : unmanaged
    {
        value = default;
        if (!reader.IsFrameworkThread || address <= 0 || address > nint.MaxValue - sizeof(T)) return false;
        var bytes = new byte[sizeof(T)];
        if (!reader.TryRead(address, bytes)) return false;
        value = MemoryMarshal.Read<T>(bytes);
        return true;
    }
    internal static int Offset<T>(string field) => Marshal.OffsetOf<T>(field).ToInt32();

    [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, byte* buffer, nuint size, out nuint read);
}
