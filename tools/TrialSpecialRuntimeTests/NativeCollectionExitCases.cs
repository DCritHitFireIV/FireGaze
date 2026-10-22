using System.Reflection;
using System.Runtime.InteropServices;
using FireGaze.LocalPreview.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

unsafe static class NativeCollectionExitCases
{
    // Missing production behavior remains a normal failing assertion in the RED run.
    public static void Run(Action<bool, string> check)
    {
        var type = typeof(ActorPreviewRuntime).Assembly.GetType("FireGaze.LocalPreview.Runtime.NativeCollectionExit");
        var snapshotType = typeof(ActorPreviewRuntime).Assembly.GetType("FireGaze.LocalPreview.Runtime.NativeCollectionExitSnapshot");
        var method = type?.GetMethod("Execute");
        check(method != null && snapshotType != null,
            "special-bar exit has a source-bound native collection removal coordinator");
        if (method == null || snapshotType == null) return;
        var run = typeof(NativeCollectionExitCases).GetMethod(nameof(RunTyped), BindingFlags.NonPublic | BindingFlags.Static)!;
        run.MakeGenericMethod(snapshotType).Invoke(null, [method, check]);
        CaptureCases(type!, check);
    }

    static void CaptureCases(Type type, Action<bool, string> check)
    {
        var capture = type.GetMethod("Capture");
        check(capture != null, "special-bar exit observes the native local-player binding before retaining a source snapshot");
        if (capture == null) return;
        var source = (Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
        var other = (Character*)NativeMemory.AllocZeroed((nuint)sizeof(Character));
        var control = (Control*)NativeMemory.AllocZeroed((nuint)sizeof(Control));
        var actual = (nint*)NativeMemory.AllocZeroed((nuint)sizeof(nint));
        nint oldControl = Control.Addresses.Instance.Value, oldPlayer = Control.Addresses.GetLocalPlayer.Value;
        object? Read(nint manager = 200) => capture.Invoke(null, [Pointer.Box(source, typeof(Character*)), (uint)843, manager]);
        try
        {
            Control.Addresses.Instance.Value = (nint)control;
            Control.Addresses.GetLocalPlayer.Value = (nint)actual;
            control->LocalPlayer = (BattleChara*)source; control->LocalPlayerEntityId = 3; *actual = (nint)source;
            source->ObjectKind = ObjectKind.Pc; source->ObjectIndex = 0; source->EntityId = 3;
            *(uint*)((byte*)source + 0x74) = 2;
            source->Mount.MountId = 223; source->OrnamentData.OrnamentId = 56;
            byte[] before = new ReadOnlySpan<byte>(source, sizeof(Character)).ToArray();
            var state = Read();
            check(state != null && (nint)state.GetType().GetProperty("Source")!.GetValue(state)! == (nint)source
                && (uint)state.GetType().GetProperty("Generation")!.GetValue(state)! == 2
                && (ushort)state.GetType().GetProperty("Mount")!.GetValue(state)! == 223
                && (ushort)state.GetType().GetProperty("Accessory")!.GetValue(state)! == 56,
                "a consistent native source yields current allocation and real collection state");
            control->LocalPlayer = (BattleChara*)other;
            check(Read() == null, "a stale managed player with changed native Control cannot create an exit snapshot");
            control->LocalPlayer = (BattleChara*)source; *actual = (nint)other;
            check(Read() == null, "a stale native GetLocalPlayer binding cannot create an exit snapshot");
            *actual = (nint)source; control->LocalPlayerEntityId = 4;
            check(Read() == null, "a conflicting native local-player entity id prevents an exit snapshot");
            control->LocalPlayerEntityId = 3; source->ObjectKind = ObjectKind.BattleNpc;
            check(Read() == null, "a non-player managed allocation cannot authorize native collection removal");
            source->ObjectKind = ObjectKind.Pc;
            check(Read(0) == null, "an unavailable native action manager cannot authorize an exit");
            source->EntityId = control->LocalPlayerEntityId = 0xE0000000;
            check(Read() == null, "an invalid entity sentinel cannot authorize native collection removal");
            source->EntityId = control->LocalPlayerEntityId = 3;
            check(new ReadOnlySpan<byte>(source, sizeof(Character)).SequenceEqual(before),
                "exit snapshot capture never modifies source character bytes");
        }
        finally
        {
            Control.Addresses.Instance.Value = oldControl; Control.Addresses.GetLocalPlayer.Value = oldPlayer;
            NativeMemory.Free(source); NativeMemory.Free(other); NativeMemory.Free(control); NativeMemory.Free(actual);
        }
    }

    static void RunTyped<T>(MethodInfo method, Action<bool, string> check) where T : struct
    {
        object Snapshot(nint source = 100, uint generation = 2, uint actor = 3, uint territory = 843,
            ushort mount = 223, ushort accessory = 56, nint manager = 200)
            => Activator.CreateInstance(typeof(T), source, generation, actor, territory, mount, accessory, manager)!;
        var entry = (T)Snapshot();
        T? current = entry;
        int mounts = 0, accessories = 0;
        ushort removed = 0;
        Action? onMount = null;
        bool accepted = true;
        void Execute() => method.Invoke(null, [entry, (Func<T?>)(() => current),
            (Func<bool>)(() => { mounts++; onMount?.Invoke(); return accepted; }),
            (Func<ushort, bool>)(id => { accessories++; removed = id; return true; })]);
        void Reset() { mounts = accessories = 0; removed = 0; accepted = true; onMount = null; current = entry; }

        onMount = () => current = (T)Snapshot(mount: 0);
        Execute();
        check(mounts == 1 && accessories == 1 && removed == 56,
            "matching real state removes mount and accessory once while allowing the accepted dismount to reach zero");
        foreach (var changed in new object[] { Snapshot(source: 101), Snapshot(generation: 4), Snapshot(actor: 5),
            Snapshot(territory: 1185), Snapshot(manager: 201), Snapshot(mount: 224), Snapshot(accessory: 58) })
        {
            Reset(); current = (T)changed; Execute();
            check(mounts == 0 && accessories == 0,
                "a changed actor allocation identity territory manager or real collection state defeats the old exit snapshot");
        }
        Reset(); current = null; Execute();
        check(mounts == 0 && accessories == 0, "logout or a lost native source prevents all old collection removals");
        foreach (var invalid in new object[] { Snapshot(source: 0), Snapshot(manager: 0),
            Snapshot(actor: 0), Snapshot(actor: 0xE0000000) })
        {
            entry = (T)invalid; Reset(); Execute();
            check(mounts == 0 && accessories == 0,
                "an absent source manager or native entity identity cannot authorize collection removal");
        }
        entry = (T)Snapshot();
        Reset(); onMount = () => current = (T)Snapshot(accessory: 58); Execute();
        check(mounts == 1 && accessories == 0,
            "a new native accessory during dismount cannot be toggled or replaced by the old accessory id");
        Reset(); onMount = () => current = (T)Snapshot(source: 101); Execute();
        check(mounts == 1 && accessories == 0,
            "a native reentry changing local player prevents the second collection operation");
        Reset(); accepted = false; onMount = () => current = (T)Snapshot(mount: 0); Execute();
        check(mounts == 1 && accessories == 0,
            "a rejected dismount cannot authorize a later changed state as our own removal");
        entry = (T)Snapshot(mount: 0); Reset(); Execute();
        check(mounts == 0 && accessories == 1 && removed == 56,
            "an accessory-only genuine state makes no native mount request");
        entry = (T)Snapshot(mount: 0, accessory: 0); Reset(); Execute();
        check(mounts == 0 && accessories == 0,
            "a pure local trial makes no native collection requests on final exit");
    }
}
