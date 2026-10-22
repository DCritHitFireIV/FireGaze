using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RTManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager;

namespace FireGaze.PortraitPreview;

// Actual receiver entries. The resize callback wrappers have a different ABI.
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate void PortraitNativeResourceCleanup(RTManager* manager);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal unsafe delegate byte PortraitNativeResourceRecreate(RTManager* manager);

internal readonly record struct PortraitNativeResourceSpan(uint Begin, uint End, uint Unwind);
internal sealed record PortraitNativeResourceBody(PortraitNativeObservedPhase Phase,
    IReadOnlyList<PortraitNativeResourceSpan> Spans, string SHA256);
internal enum PortraitNativeResourceTransition { Unqualified, Transitioning, CleanupReturned, RecreateReturned, OriginalThrew }
internal readonly record struct PortraitNativeResourceState(long Generation, int Depth, PortraitNativeResourceTransition Transition,
    nint Receiver, byte? NativeReturn)
{ public bool Ready => false; }

internal static unsafe class PortraitNativeResourceIngressContract
{
    internal static IReadOnlyList<PortraitNativePhaseEntryContract> Inventory { get; } = Array.AsReadOnly<PortraitNativePhaseEntryContract>([
        new(PortraitNativeObservedPhase.ResourceCleanup, 0x2DBB90, "40534883EC20488B05E3AB6102488BD980787A000F8425040000", typeof(PortraitNativeResourceCleanup), false),
        new(PortraitNativeObservedPhase.ResourceRecreate, 0x2DBFE0, "4055574155488D6C24B94881ECA0000000488B0560E65F024833C448894527", typeof(PortraitNativeResourceRecreate), false),
    ]);
    private static readonly PortraitNativeResourceBody[] bodies = [
        new(PortraitNativeObservedPhase.ResourceCleanup, Array.AsReadOnly<PortraitNativeResourceSpan>([
            new(0x2DBB90,0x2DBBAA,0x25F05F8), new(0x2DBBAA,0x2DBF33,0x25F0600), new(0x2DBF33,0x2DBF60,0x25F0620),
            new(0x2DBF60,0x2DBFA4,0x25F063C), new(0x2DBFA4,0x2DBFD5,0x25F0650)]), "B0541DF842C6E0D5FE3F2A44E686600478E0E69E44999BA7A2556968E775667A"),
        new(PortraitNativeObservedPhase.ResourceRecreate, Array.AsReadOnly<PortraitNativeResourceSpan>([
            new(0x2DBFE0,0x2DC01C,0x25F0660), new(0x2DC01C,0x2DC1B6,0x25F0678), new(0x2DC1B6,0x2DC1CE,0x25F069C),
            new(0x2DC1CE,0x2DCCA6,0x25F06AC)]), "CA012D9CD2C3A2828FF3E67E3FC4D23934646789765607FAB570B5CF3A7E9298"),
    ];
    internal static PortraitNativeResourceBody? Find(PortraitNativeObservedPhase phase) => bodies.SingleOrDefault(body => body.Phase == phase);
    internal static bool ABIValid() => Valid(typeof(PortraitNativeResourceCleanup), typeof(void)) && Valid(typeof(PortraitNativeResourceRecreate), typeof(byte));
    private static bool Valid(Type type, Type result) => type.GetMethod("Invoke") is { } invoke && invoke.ReturnType == result
        && invoke.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(RTManager*) })
        && type.GetCustomAttributes(typeof(UnmanagedFunctionPointerAttribute), false).SingleOrDefault() is UnmanagedFunctionPointerAttribute { CallingConvention: CallingConvention.Cdecl };

    internal static bool TryReadCompleteBody(IPortraitNativePhaseProcess process, nint moduleBase,
        PortraitNativeResourceBody body, out string hash)
    {
        hash = "";
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> chunk = stackalloc byte[64];
        foreach (var range in body.Spans)
        {
            if (range.Begin >= range.End || moduleBase <= 0 || moduleBase > nint.MaxValue - (nint)range.End) return false;
            for (var rva = range.Begin; rva < range.End;)
            {
                var count = (int)Math.Min(64u, range.End - rva);
                if (!process.TryReadMemory(moduleBase + (nint)rva, chunk[..count])) return false;
                digest.AppendData(chunk[..count]); rva += (uint)count;
            }
        }
        hash = Convert.ToHexString(digest.GetHashAndReset()); return hash == body.SHA256;
    }
}
