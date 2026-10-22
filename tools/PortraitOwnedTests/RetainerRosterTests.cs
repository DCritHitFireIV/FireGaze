using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FireGaze.PortraitPreview;

internal static unsafe class RetainerRosterTests
{
    internal static (int Passed, int Failed) Run()
    {
        var passed = 0; var failed = 0;
        var alice = new OwnedCharacterKey(1, 101); var bob = new OwnedCharacterKey(1, 202);
        void Check(bool condition, string name) { if (condition) passed++; else { failed++; Console.WriteLine("FAIL retainer-roster " + name); } }
        var state = new OwnedRetainerRosterState();
        Check(state.Observe(alice, null) is null, "unknown current-character roster stays unknown");
        var ids = new List<ulong> { 11, 22 };
        var first = state.Observe(alice, ids); ids.Clear();
        Check(first is { Status: OwnedReadStatus.Current } && first.RetainerIDs.SequenceEqual(new ulong[] { 11, 22 }), "current roster detaches caller memory");
        var stale = state.Observe(alice, null);
        Check(stale is { Status: OwnedReadStatus.PossiblyStale } && stale.RetainerIDs.Count == 2, "temporarily unavailable roster is marked stale");
        Check(state.Observe(bob, null) is null, "character switch clears old roster instead of carrying denominator");
        state.Observe(alice, [11, 22]); Check(state.Observe(new(2, 101), null) is null, "account epoch change clears old roster");
        state.Observe(alice, [11, 22]); Check(state.Observe(null, null) is null && state.Observe(alice, null) is null, "logout clears roster proof");
        Check(state.Observe(alice, []) is { RetainerIDs.Count: 0, Status: OwnedReadStatus.Current }, "known zero hired retainers remains authoritative");
        Check(state.Observe(bob, [11, 11]) is null && state.Observe(bob, [0, 22]) is null, "invalid roster cannot establish denominator");
        var manager = (RetainerManager*)NativeMemory.AllocZeroed((nuint)sizeof(RetainerManager));
        var finder = (ItemFinderModule*)NativeMemory.AllocZeroed((nuint)sizeof(ItemFinderModule));
        try
        {
            manager->Retainers[0].RetainerId = 11; manager->Retainers[7].RetainerId = 22; manager->MaxRetainerEntitlement = 10;
            finder->CharacterContentId = 101; finder->IsRetainerManagerReady = true;
            Check(!OwnedNativeInventoryReader.TryReadRetainerRoster(alice, (nint)manager, (nint)finder, out _), "unready manager cannot establish roster");
            manager->IsReady = true;
            Check(OwnedNativeInventoryReader.TryReadRetainerRoster(alice, (nint)manager, (nint)finder, out var read) && read.SequenceEqual(new ulong[] { 11, 22 }), "assigned IDs count two hired retainers despite ten slots and entitlement");
            Check(!OwnedNativeInventoryReader.TryReadRetainerRoster(bob, (nint)manager, (nint)finder, out _), "other character item finder cannot authorize roster");
            finder->IsRetainerManagerReady = false;
            Check(!OwnedNativeInventoryReader.TryReadRetainerRoster(alice, (nint)manager, (nint)finder, out _), "stale item finder readiness cannot authorize roster");
            finder->IsRetainerManagerReady = true;
            manager->Retainers[8].RetainerId = 11;
            Check(!OwnedNativeInventoryReader.TryReadRetainerRoster(alice, (nint)manager, (nint)finder, out _), "duplicate native roster IDs fail closed");
            for (var index = 0; index < manager->Retainers.Length; index++) manager->Retainers[index].RetainerId = (ulong)(index + 1);
            Check(OwnedNativeInventoryReader.TryReadRetainerRoster(alice, (nint)manager, (nint)finder, out read) && read.Count == 10, "ten assigned retainers are supported without truncation");
            for (var index = 0; index < manager->Retainers.Length; index++) manager->Retainers[index].RetainerId = 0;
            Check(OwnedNativeInventoryReader.TryReadRetainerRoster(alice, (nint)manager, (nint)finder, out read) && read.Count == 0, "ready native zero roster is distinguished from unknown");
            Check(!OwnedNativeInventoryReader.TryReadRetainerRoster(alice, 0, (nint)finder, out _) && !OwnedNativeInventoryReader.TryReadRetainerRoster(alice, (nint)manager, 0, out _), "missing native boundary cannot establish roster");
        }
        finally { NativeMemory.Free(finder); NativeMemory.Free(manager); }
        return (passed, failed);
    }
}
