using System.Diagnostics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;

namespace FireGaze.PortraitPreview;

/// <summary>Explicit current-process reads only. Construction performs no process, SDK or memory access.</summary>
internal sealed unsafe class PortraitNativePhaseCurrentProcess(Func<nint, bool> isAddressReserved) : IPortraitNativePhaseProcess
{
    public bool TryReadModule(out PortraitNativePhaseProcessModule module)
    {
        module = null!;
        using var process = Process.GetCurrentProcess();
        var current = process.MainModule;
        if (current is null || !string.Equals(Path.GetFileName(current.FileName), "ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase)) return false;
        module = new(Path.GetFileName(current.FileName), current.BaseAddress, current.ModuleMemorySize, File.ReadAllBytes(current.FileName));
        return true;
    }

    public bool TryReadSDK(out PortraitNativePhaseSDKImage image)
    {
        image = null!;
        var assembly = typeof(CharaView).Assembly;
        if (string.IsNullOrEmpty(assembly.Location)) return false;
        image = new(File.ReadAllBytes(assembly.Location), assembly.ManifestModule.ModuleVersionId);
        return true;
    }

    public nint SDKManagerRender => RenderManager.Addresses.Render.Value;
    public nint SDKSourceRender => CharaView.Addresses.Render.Value;
    public bool IsAddressReserved(nint address) => isAddressReserved(address);
    public bool TryReadMemory(nint address, Span<byte> destination)
    {
        if (address <= 0 || destination.IsEmpty || destination.Length > 64 || address > nint.MaxValue - destination.Length) return false;
        fixed (byte* output = destination)
            return ReadProcessMemory(new nint(-1), address, output, (nuint)destination.Length, out var read) && read == (nuint)destination.Length;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, void* destination, nuint length, out nuint read);
}
