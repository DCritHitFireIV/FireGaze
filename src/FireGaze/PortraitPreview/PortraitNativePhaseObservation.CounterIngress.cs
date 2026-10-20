using System.Runtime.InteropServices;
using RenderManager = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Manager;

namespace FireGaze.PortraitPreview;

internal enum PortraitNativePhaseTargetSet { PhaseObservation, CounterIngress }

// Complete native leaf ABI, independently audited against the pinned image.
// The uint is a slot, never a native frame. Neither function returns a value.
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal unsafe delegate void PortraitNativeSlotCounter(RenderManager* manager, uint slot);

internal static class PortraitNativeCounterIngressContract
{
    internal static IReadOnlyList<PortraitNativePhaseEntryContract> Inventory { get; } = Array.AsReadOnly<PortraitNativePhaseEntryContract>([
        new(PortraitNativeObservedPhase.CounterAdd, 0x2BA770, "83FA0873098BC2FF84815C830300C3CC", typeof(PortraitNativeSlotCounter), false, true),
        new(PortraitNativeObservedPhase.CounterSub, 0x2BA790, "83FA0873218BC2BA00000000488D0C8183815C830300FF8B815C8303000F48C289815C830300C3CC", typeof(PortraitNativeSlotCounter), false, true),
    ]);

    internal static IReadOnlyList<PortraitNativePhaseEntryContract> For(PortraitNativePhaseTargetSet set) => set switch {
        PortraitNativePhaseTargetSet.PhaseObservation => PortraitNativePhaseObservationBinding.Inventory,
        PortraitNativePhaseTargetSet.CounterIngress => Inventory,
        _ => [],
    };

    internal static PortraitNativePhaseEntryContract? Find(PortraitNativeObservedPhase kind)
        => PortraitNativePhaseObservationBinding.Inventory.Concat(Inventory).SingleOrDefault(entry => entry.Phase == kind);

    internal static unsafe bool ABIValid()
    {
        var invoke = typeof(PortraitNativeSlotCounter).GetMethod("Invoke")!;
        return invoke.ReturnType == typeof(void)
            && invoke.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(RenderManager*), typeof(uint) });
    }
}
