using System.Runtime.InteropServices;

namespace FireGaze.PortraitPreview;

// Audited against the frozen current EXE. These are local ABI contracts, not SDK delegates.
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate nint ConstructContext(nint context);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate nint DestroyContext(nint context, byte flags);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate byte ConfigureContext(nint context, uint slot, uint cameraType);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate byte ConfigureDefaultContext(nint context, uint slot);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void CleanupContext(nint context);
internal readonly record struct ContextState(uint Slot, uint CameraType, bool Active);
internal enum ContextMutationKind { Configure, ConfigureDefault, Cleanup }
internal readonly record struct ContextMutation(nint Context, ContextMutationKind Kind,
    ContextState? Previous, uint? RequestedSlot, uint? RequestedCameraType)
{
    public bool Touches(uint slot)
    {
        if (slot >= 8) return false;
        // An unreadable previous context cannot certify any owned slot safe.
        if (Previous is not { } previous) return true;
        return RequestedSlot == slot || previous.Slot == slot
            && (Kind == ContextMutationKind.Cleanup || previous.Active);
    }
}

// This staged boundary only emits detached pre-mutation evidence. The consumer
// must qualify thread/phase/identity before attempting retirement. It never
// synthesizes Cleanup, decrements a counter, or installs a hook. Native originals
// keep priority even when observation or the fault reporter fails.
internal sealed class PortraitContextInterception(Func<nint, ContextState> read,
    Action<ContextMutation> before, Action<Exception> fault)
{
    private int faulted;
    public bool IsFaulted => Volatile.Read(ref faulted) != 0;
    public byte Configure(nint context, uint slot, uint cameraType, ConfigureContext original)
    {
        Observe(context, ContextMutationKind.Configure, slot, cameraType);
        return original(context, slot, cameraType);
    }
    public byte ConfigureDefault(nint context, uint slot, ConfigureDefaultContext original)
    {
        Observe(context, ContextMutationKind.ConfigureDefault, slot, 0);
        return original(context, slot);
    }
    public void Cleanup(nint context, CleanupContext original)
    {
        Observe(context, ContextMutationKind.Cleanup, null, null);
        original(context);
    }
    private void Observe(nint context, ContextMutationKind kind, uint? slot, uint? cameraType)
    {
        ContextState? previous = null;
        try { previous = read(context); }
        catch (Exception error) { Fault(error); }
        try { before(new(context, kind, previous, slot, cameraType)); }
        catch (Exception error) { Fault(error); }
    }
    private void Fault(Exception error)
    {
        if (Interlocked.Exchange(ref faulted, 1) != 0) return;
        try { fault(error); }
        catch { /* Reporting failure cannot suppress a native original. */ }
    }
}
