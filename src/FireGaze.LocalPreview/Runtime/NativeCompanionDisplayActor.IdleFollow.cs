using FireGaze.LocalPreview.Core;
using static FireGaze.LocalPreview.Runtime.NativeCompanionFollowReader;
namespace FireGaze.LocalPreview.Runtime;
public sealed unsafe partial class NativeCompanionDisplayActor
{
    // The caller must retain an Active exact lease/scene guard.
    // Calls only native idle gate; the next ordinary Update owns Follow execution.
    internal NativeIdleFollowResult TryRequestIdleFollow(NativeCompanionIdleFollowGate gate,
        ICompanionFollowSnapshotReader reader, CompanionExecutionKey key, Guid instance,
        uint territory, long requestEpoch, nint qualifiedCompanionVirtualTable, Func<bool> scopeCurrent,
        bool ordinarySpeciesOnly = false)
    {
        var refused = new NativeIdleFollowResult(NativeIdleFollowStatus.Refused, null, false, null, null, "actor-binding-or-scope");
        if (!gate.Enabled) return new(NativeIdleFollowStatus.Disabled, null, false, null, null);
        try
        {
            if (!reader.IsFrameworkThread || scopeCurrent is null || !scopeCurrent()
                || qualifiedCompanionVirtualTable <= 0 || !NativeCompanionFollowReader.LayoutValid()
                || !TryReadFollowBinding(reader, key, territory, requestEpoch, out var binding, out var state)) return refused;
            if (state.BehaviorStateRaw == 1) return new(NativeIdleFollowStatus.AlreadyFollow, null, false, 1, null);
            if (state.BehaviorStateRaw is not (0 or 5)
                || (state.BehaviorStateRaw == 5 && !ordinarySpeciesOnly)) return refused;
            var request = new NativeIdleFollowKey(instance, key.SceneEpoch, key.SourceID, key.SourceGeneration,
                territory, key.LeaseID, key.LeaseGeneration, binding.BindingRevision,
                binding.Child, binding.ChildGeneration, binding.Host, binding.HostGeneration);
            return gate.Request(new IdleFollowEnvironment(this, reader, binding, request,
                qualifiedCompanionVirtualTable, scopeCurrent, ordinarySpeciesOnly), request);
        }
        catch (Exception) { return refused; }
    }

    private sealed class IdleFollowEnvironment(NativeCompanionDisplayActor actor,
        ICompanionFollowSnapshotReader reader, CompanionFollowBinding binding, NativeIdleFollowKey key,
        nint expectedVirtualTable, Func<bool> scopeCurrent, bool ordinarySpeciesOnly) : INativeIdleFollowEnvironment
    {
        public bool IsFrameworkThread => reader.IsFrameworkThread;
        public string CaptureFailure { get; private set; } = "not-captured";
        public NativeIdleFollowRawInputs? RawInputs { get; private set; }
        public bool TryCapture(in NativeIdleFollowKey requested, out NativeIdleFollowSample sample)
        {
            sample = default; RawInputs = null; CaptureFailure = "scope-or-thread";
            if (requested != key || !reader.IsFrameworkThread || !scopeCurrent()
                || binding.Source == binding.Host || binding.Source == binding.Child || binding.Child == binding.Host) return false;
            CaptureFailure = "before-frame-or-raw-binding";
            if (!reader.TryReadNativeFrame(out var before) || !actor.TryReadFollowState(reader, binding, out var state)) return false;
            CaptureFailure = "child-vtable";
            if (!Read(reader, binding.Child, out nint table) || table != expectedVirtualTable) return false;
            CaptureFailure = "raw-peer-flags-threshold";
            if (!Read(reader, binding.Child + 0x2408, out nint peer1)
                || !Read(reader, binding.Child + 0x2410, out nint peer2)
                || !Read(reader, binding.Child + 0x118, out ulong flags)
                || !Read(reader, binding.Child + 0x2428, out float threshold)) return false;
            CaptureFailure = "child-position-getter";
            if (!PositionIsDirect(binding.Child)) return false;
            CaptureFailure = "host-position-getter";
            if (!PositionIsDirect(binding.Host) || !NullPropagation(binding.Child)) return false;
            CaptureFailure = "host-substate-unreadable";
            int? firstInt = null;
            if (!Read(reader, binding.Host + 0x588, out nint substate)) return false;
            // Keep the already-required safe copy, without naming an unknown native state.
            if (substate != 0)
            {
                if (!Read(reader, substate, out int value)) return false;
                firstInt = value;
            }
            // Standalone development probes retain optional diagnostics. The
            // ordinary runtime consumer requires the reviewed species fields.
            byte? speciesBehavior = Read(reader, binding.Child + 0x2394, out byte behavior) ? behavior : null;
            byte? speciesSpecial = Read(reader, binding.Child + 0x2395, out byte special) ? special : null;
            CaptureFailure = "species-outside-ordinary-follow";
            if (ordinarySpeciesOnly && (speciesBehavior is not (1 or 2) || speciesSpecial != 0)) return false;
            CaptureFailure = "genuine-companion-alias-or-change";
            var sourceCompanionSlot = binding.Source + FollowLayout.ContainerOffset + FollowLayout.ContainerChildOffset;
            if (!Read(reader, sourceCompanionSlot, out nint genuineCompanion)
                || genuineCompanion == binding.Child || genuineCompanion == binding.Host) return false;
            CaptureFailure = "after-binding-frame-or-scope";
            if (!actor.TryReadFollowState(reader, binding, out var after) || after != state
                || (ordinarySpeciesOnly && (!Read(reader, binding.Child + 0x2394, out byte finalBehavior)
                    || !Read(reader, binding.Child + 0x2395, out byte finalSpecial)
                    || finalBehavior != speciesBehavior || finalSpecial != speciesSpecial))
                || !Read(reader, sourceCompanionSlot, out nint finalGenuine) || genuineCompanion != finalGenuine
                || !reader.TryReadNativeFrame(out var frame) || frame != before || !scopeCurrent()) return false;
            sample = new(key, frame, state.SpawnStateRaw, state.BehaviorStateRaw, state.BehaviorSpecialRaw,
                state.ModelReady, true, true, peer1, peer2, flags, actor.CompanionId,
                true, true, threshold, state.ActualPosition, state.PhysicalHostPosition, genuineCompanion,
                speciesBehavior, speciesSpecial);
            RawInputs = new(speciesBehavior, speciesSpecial, state.SpawnStateRaw,
                state.BehaviorStateRaw, state.BehaviorSpecialRaw, substate != 0, firstInt,
                threshold, state.ActualPosition, state.PhysicalHostPosition);
            CaptureFailure = ""; return true;
        }
        private bool PositionIsDirect(nint address)
            => Read(reader, address + 0x80, out uint layoutId) && layoutId == 0
                && Read(reader, address + 0x9C, out byte positionFlags) && (positionFlags & 4) == 0;
        private bool NullPropagation(nint child)
        {
            CaptureFailure = "secondary-pointer";
            if (!Read(reader, child + 0x680, out nint secondary) || secondary != 0) return false;
            CaptureFailure = "secondary-id";
            if (!Read(reader, child + 0x688, out ushort secondaryId) || secondaryId != 0) return false;
            foreach (var offset in new[] { 0x710, 0x780, 0x7F0, 0x860 })
            {
                CaptureFailure = $"weapon-draw-{offset:X}";
                if (!Read(reader, child + offset, out nint draw) || draw != 0) return false;
            }
            foreach (var offset in new[] { 0x772, 0x7E2, 0x852, 0x8C2 })
            {
                CaptureFailure = $"weapon-flags-{offset:X}";
                if (!Read(reader, child + offset, out byte entryFlags) || (entryFlags & 4) != 0) return false;
            }
            return true;
        }
    }
}
