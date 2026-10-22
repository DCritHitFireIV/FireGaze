using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;
using SceneCameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager;
using OffscreenManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.OffscreenRenderingManager;
using RenderTargetManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Static SDK addresses and process-native copying are isolated here. Default construction denies
/// every read; no production caller enables this API until the full native admission gate is closed.
/// ReadProcessMemory is used for fallible copies, not unchecked native pointer dereferences.
/// </summary>
internal sealed unsafe class PortraitOwnedSceneSlotNativeApi(Func<bool>? admission = null) : IPortraitOwnedSceneSlotNativeApi
{
    public bool TryReadRoots(out PortraitOwnedSceneSlotRoots roots)
    {
        roots = default;
        if (!Admitted()) return false;
        try
        {
            if (!TryRead((nint)RenderManager.StaticAddressPointers.ppInstance, out nint renderer)
                || !TryRead((nint)SceneCameraManager.StaticAddressPointers.ppInstance, out nint cameras)
                || !TryRead((nint)OffscreenManager.StaticAddressPointers.ppInstance, out nint offscreen)
                || !TryRead((nint)RenderTargetManager.StaticAddressPointers.ppInstance, out nint targets)) return false;
            // The ClientObjectManager SDK address is inline storage, not a pointer global.
            var candidate = new PortraitOwnedSceneSlotRoots(renderer, cameras, offscreen, targets,
                (nint)ClientObjectManager.StaticAddressPointers.pInstance);
            if (!candidate.IsValid || !Admitted()) return false;
            roots = candidate; return true;
        }
        catch (Exception) { return false; }
    }

    public bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        if (address == 0 || !OperatingSystem.IsWindows() || !Admitted()) return false;
        fixed (T* result = &value)
        {
            if (!ReadProcessMemory((nint)(-1), address, result, (nuint)sizeof(T), out var copied)
                || copied != (nuint)sizeof(T) || !Admitted())
            { value = default; return false; }
        }
        return true;
    }

    private bool Admitted()
    {
        try { return admission?.Invoke() == true; }
        catch (Exception) { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, void* buffer, nuint size, out nuint copied);
}
