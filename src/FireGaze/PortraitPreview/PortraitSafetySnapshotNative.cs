using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitSafetySnapshotLayout(int FocusOffset, int FrameOffset,
    int EditorStateOffset, int StateViewOffset, int MovingOffset);

/// <summary>Only fallible copies and named SDK layout checks. No allocation, getter or native call on construction.</summary>
internal static unsafe class PortraitSafetySnapshotNative
{
    public static bool TryGetLayout(out PortraitSafetySnapshotLayout layout)
    {
        layout = default;
        try
        {
            var focus = checked((int)Marshal.OffsetOf<NativeFramework>(nameof(NativeFramework.CursorInputs))
                + (int)Marshal.OffsetOf<CursorInputData>(nameof(CursorInputData.IsGameWindowFocused)));
            var frame = checked((int)Marshal.OffsetOf<NativeFramework>(nameof(NativeFramework.FrameCounter)));
            var editorState = checked((int)Marshal.OffsetOf<AgentBannerEditor>(nameof(AgentBannerEditor.EditorState)));
            var view = checked((int)Marshal.OffsetOf<AgentBannerEditorState>(nameof(AgentBannerEditorState.CharaView)));
            var moving = checked((int)Marshal.OffsetOf<AgentMap>(nameof(AgentMap.IsPlayerMoving)));
            // These sizes and offsets belong to the SDK/game pair whose getter contract was audited.
            // A layout drift is unavailable evidence, never a reason to guess another focus field.
            if (sizeof(NativeFramework) != 0x35D8 || sizeof(CursorInputData) != 0x30
                || focus != 0xA28 || frame != 0x16D0 || editorState != 0x28 || view != 0x3C8
                || moving != 0x5B08) return false;
            layout = new(focus, frame, editorState, view, moving);
            return true;
        }
        catch (Exception) { return false; }
    }

    public static bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        if (address <= 0 || !OperatingSystem.IsWindows()) return false;
        fixed (T* result = &value)
        {
            if (!ReadProcessMemory((nint)(-1), address, result, (nuint)sizeof(T), out var copied)
                || copied != (nuint)sizeof(T))
            { value = default; return false; }
        }
        return true;
    }

    public static bool TryReadBytes(nint address, int count, out byte[] values)
    {
        values = [];
        if (address <= 0 || count is <= 0 or > 112 || !OperatingSystem.IsWindows()) return false;
        var copy = new byte[count];
        fixed (byte* result = copy)
            if (!ReadProcessMemory((nint)(-1), address, result, (nuint)count, out var copied)
                || copied != (nuint)count) return false;
        values = copy;
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint copied);
}
