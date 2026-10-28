using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FireGaze.LocalPreview.Core;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>
/// Reads the detached ObjectKind census used to avoid probing a known-full native
/// companion pool. It is intentionally read-only and Framework-thread only; it never
/// reserves, writes, or calls the allocator.
/// </summary>
internal static unsafe class NativeCompanionCapacityReader
{
    // _battleCharas starts at +0x10. The CN allocator's
    // `lea rax,[rcx+0x18]` points to entry 0's ObjectKind (+0x08), not to the
    // start of the entry array. Keep the entry base and field offset separate so
    // the census reads entry 0 through entry 239 without skipping or over-reading.
    private const int ManagerSlotsOffset = 0x10;
    private const int EntrySize = 0x10;
    private const int ObjectKindOffset = 0x08;
    private const int AllocatorSlots = 240;

    public static bool TryRead(ClientObjectManager* manager, out CompanionCapacityObservation observation)
    {
        observation = CompanionCapacityObservation.Unknown("client object manager unavailable");
        if (manager == null || sizeof(ClientObjectManager) != 0xFA0)
        {
            observation = CompanionCapacityObservation.Unknown("client object manager layout is not verified");
            return false;
        }

        // The SDK exposes 249 entries, while the CN allocator's automatic path
        // intentionally stops at index 239. Do not consume the unqualified tail.
        if (manager->BattleCharas.Length < AllocatorSlots)
        {
            observation = CompanionCapacityObservation.Unknown("client object manager has too few slots");
            return false;
        }

        Span<byte> kinds = stackalloc byte[AllocatorSlots];
        var entries = (byte*)manager + ManagerSlotsOffset;
        for (var i = 0; i < AllocatorSlots; i++)
            kinds[i] = *(entries + (i * EntrySize) + ObjectKindOffset);

        return CompanionCapacityObservation.TryRead(kinds, out observation, AllocatorSlots);
    }
}
