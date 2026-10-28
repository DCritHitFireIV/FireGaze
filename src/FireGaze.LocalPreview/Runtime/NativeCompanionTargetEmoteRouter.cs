using Dalamud.Plugin.Services;
using Dalamud.Hooking;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Runtime;

public enum NativeCompanionBeckonSubmitStatus { Queued, NativeRefused, Cancelled, Invalidated }
public enum NativeCompanionBeckonObservationStatus { Pending, QueueNoLongerMatches, Invalidated, NotQueued }

public readonly record struct NativeCompanionBeckonReceipt(
    NativeCompanionBeckonSubmitStatus Status, uint EmoteID, uint SourceGeneration,
    uint BeforeQueuedEmote, uint AfterQueuedEmote, byte BeforeTargeted, byte AfterTargeted,
    byte BeforeSameOwner, byte AfterSameOwner, ushort BeforeOwnerIndex, ushort AfterOwnerIndex,
    bool QueueChanged, bool OwnerMarked, string? Reason)
{
    public bool AfterStateKnown { get; init; }
    // Private-to-adapter identity, never a UI DTO or a dereference authority.
    internal nint Child { get; init; }
    internal nint Source { get; init; }
    internal uint ChildGeneration { get; init; }
    internal ushort ChildIndex { get; init; }
    internal ushort SourceIndex { get; init; }
    internal nint PhysicalOwner { get; init; }
}

public readonly record struct NativeCompanionBeckonObservation(
    NativeCompanionBeckonObservationStatus Status, uint SourceGeneration, uint CurrentQueuedEmote,
    byte CurrentTargeted, byte CurrentSameOwner, ushort CurrentOwnerIndex, string? Reason);

/// <summary>
/// The client already dispatches a targeted emote to the selected Companion. An independently
/// allocated display child has a native host owner, so the native same-owner flag is false.
/// Adapt that one display-only flag after the genuine dispatcher has accepted the exact sender
/// and target. Physical Owner and all gameplay actor state remain under native ownership.
/// </summary>
public sealed unsafe class NativeCompanionTargetEmoteRouter : IDisposable, INativePhysicalCloseState
{
    public const string ReceiveEmoteSignature = "4D 85 C0 0F 84 ?? ?? ?? ?? 48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 48 83 EC 30 0F 29 74 24 20";
    private const int GenerationOffset = 0x74;
    private const int QueuedEmoteOffset = 0x244c;
    private const int TargetedEmoteOffset = 0x245d;
    private const int SameOwnerOffset = 0x245e;
    private const int OwnerObjectIndexOffset = 0x2454;
    public const uint BeckonEmoteID = 8;
    // Reviewed CN call sites at 6176E0/6177E1 do not consume a result. The entire
    // 174C670..174C793 function writes a queue only; it is a void four-argument ABI.
    private delegate void ReceiveEmote(Companion* companion, uint emote, NativeCharacter* source, GameObject* target);
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Func<nint, bool> isOwnedChild;
    private readonly Func<nint, uint, bool>? admitInteraction;
    private Hook<ReceiveEmote>? receive;
    private bool disposed;
    private NativePhysicalCloseState? physicalClose;
    private NativePhysicalCloseState PhysicalClose => physicalClose ??= new(this);
    public bool PhysicalCloseComplete => disposed && PhysicalClose.Complete;
    public string? PhysicalCloseFailure => PhysicalClose.Failure;
    public bool Enabled => !disposed && receive is { IsEnabled: true };

    public NativeCompanionTargetEmoteRouter(IGameInteropProvider interop, IFramework framework,
        IPluginLog log, Func<nint, bool> isOwnedChild, Func<nint, uint, bool>? admitInteraction = null)
    {
        this.framework = framework; this.log = log; this.isOwnedChild = isOwnedChild; this.admitInteraction = admitInteraction;
        if (sizeof(Companion) != 0x2470 || sizeof(NativeCharacter) != 0x2370
            || Marshal.OffsetOf<Companion>(nameof(Companion.Special)).ToInt32() != 0x2395
            || Marshal.OffsetOf<Companion>(nameof(Companion.BehaviorSpecial)).ToInt32() != 0x245A)
            throw new InvalidOperationException("Companion emote layout does not match the reviewed CN client.");
        try
        {
            receive = interop.HookFromSignature<ReceiveEmote>(ReceiveEmoteSignature, OnReceive);
            receive.Enable();
        }
        catch (Exception error)
        {
            disposed = true;
            PhysicalClose.CloseHook(ref receive);
            PhysicalClose.Finish();
            if (!PhysicalCloseComplete) throw new NativePhysicalCloseException(this, error);
            throw;
        }
    }

    /// <summary>Submits one exact owned child to the genuine native beckon queue.</summary>
    public NativeCompanionBeckonReceipt SubmitBeckon(nint childAddress, nint sourceAddress,
        Func<bool>? scopeCurrent = null)
    {
        if (!Enabled) return Refused("Native receive-emote hook is unavailable.");
        return SubmitBeckonCore(framework.IsInFrameworkUpdateThread, (Companion*)childAddress,
            (NativeCharacter*)sourceAddress, isOwnedChild, admitInteraction,
            () => Enabled && ScopeIsCurrent(scopeCurrent),
            (child, emote, source, target) => receive!.Original(child, emote, source, target));
    }

    /// <summary>Observes one previously queued request without issuing native work.</summary>
    public NativeCompanionBeckonObservation ObserveBeckon(nint childAddress, nint sourceAddress,
        NativeCompanionBeckonReceipt receipt, Func<bool>? scopeCurrent = null)
        => ObserveBeckonCore(framework.IsInFrameworkUpdateThread, (Companion*)childAddress,
            (NativeCharacter*)sourceAddress, receipt, isOwnedChild,
            () => Enabled && ScopeIsCurrent(scopeCurrent));

    internal unsafe delegate void BeckonSubmit(Companion* child, uint emote, NativeCharacter* source, GameObject* target);

    internal static NativeCompanionBeckonReceipt SubmitBeckonForTesting(bool frameworkThread,
        Companion* child, NativeCharacter* source, Func<nint, bool> owned,
        Func<nint, uint, bool>? admit, Func<bool>? scopeCurrent, BeckonSubmit submit)
        => SubmitBeckonCore(frameworkThread, child, source, owned, admit, scopeCurrent, submit);

    internal static NativeCompanionBeckonObservation ObserveBeckonForTesting(bool frameworkThread,
        Companion* child, NativeCharacter* source, NativeCompanionBeckonReceipt receipt,
        Func<nint, bool> owned, Func<bool>? scopeCurrent)
        => ObserveBeckonCore(frameworkThread, child, source, receipt, owned, scopeCurrent);

    private static NativeCompanionBeckonReceipt SubmitBeckonCore(bool frameworkThread,
        Companion* child, NativeCharacter* source, Func<nint, bool> owned,
        Func<nint, uint, bool>? admit, Func<bool>? scopeCurrent, BeckonSubmit submit)
    {
        if (!frameworkThread || child == null || source == null || submit is null)
            return Cancelled("Framework or exact actor scope is unavailable.");
        if (!ScopeIsCurrent(scopeCurrent)) return Cancelled("The companion request scope changed before submission.");
        if (!IsLocalSource(source) || !Owned(owned, child) || !IsLocalSource(source) || !ValidSource(source))
            return Cancelled("The exact local companion is no longer current.");
        // Placement-generating Specials are deliberately outside this queue seam. Until the
        // shared occupancy and evaluated-bone adapters are admitted, no native call is issued.
        if (!CanGrantLogicalOwnerReaction(child, BeckonEmoteID))
            return Refused("This native reaction can place the companion and is not admitted yet.");
        var generation = *(uint*)((byte*)source + GenerationOffset);
        var childGeneration = *(uint*)((byte*)child + GenerationOffset);
        var childIndex = child->ObjectIndex;
        var sourceIndex = source->ObjectIndex;
        var physicalOwner = (nint)child->Owner;
        var before = Capture(child);
        if (before.QueuedEmote != 0)
            return Refused(before, before, generation, "A native interaction is already queued.");
        var submitted = false;
        try
        {
            if (admit is not null && !admit((nint)child, BeckonEmoteID))
                return Cancelled("Interaction admission was refused.");
            if (!ScopeIsCurrent(scopeCurrent) || !IsLocalSource(source)
                || !Owned(owned, child) || !IsLocalSource(source) || *(uint*)((byte*)source + GenerationOffset) != generation
                || *(uint*)((byte*)child + GenerationOffset) != childGeneration
                || child->ObjectIndex != childIndex || source->ObjectIndex != sourceIndex
                || (nint)child->Owner != physicalOwner)
                return Cancelled("The companion request scope changed before native submission.");
            if (!CanGrantLogicalOwnerReaction(child, BeckonEmoteID))
                return Refused("Placement-generating reaction changed before submission.");
            var admittedQueue = Capture(child);
            if (admittedQueue != before)
                return Refused(before, admittedQueue, generation, "Native queue changed during interaction admission.");
            submitted = true;
            submit(child, BeckonEmoteID, source, (GameObject*)child);
            if (!ScopeIsCurrent(scopeCurrent) || !IsLocalSource(source)
                || !Owned(owned, child) || !IsLocalSource(source) || *(uint*)((byte*)source + GenerationOffset) != generation
                || *(uint*)((byte*)child + GenerationOffset) != childGeneration
                || child->ObjectIndex != childIndex || source->ObjectIndex != sourceIndex
                || (nint)child->Owner != physicalOwner)
                return Invalidated(before, before, generation,
                    "The source changed during native submission.");
            var after = Capture(child);
            var changed = !before.Equals(after);
            var matches = after.QueuedEmote == BeckonEmoteID && after.Targeted == 1
                && after.OwnerIndex == OwnerIndex(source);
            if (!changed || !matches)
                return Refused(before, after, generation, "Native dispatcher did not accept this beckon.");
            if (!RouteAfterNative(child, BeckonEmoteID, source, (GameObject*)child, generation, owned))
                return Invalidated(before, after, generation, "Native ownership changed after queue submission.", afterKnown: true);
            after = Capture(child);
            return new(NativeCompanionBeckonSubmitStatus.Queued, BeckonEmoteID, generation,
                before.QueuedEmote, after.QueuedEmote, before.Targeted, after.Targeted,
                before.SameOwner, after.SameOwner, before.OwnerIndex, after.OwnerIndex,
                true, child->IsEmoteFromOwner, null)
                { Child = (nint)child, Source = (nint)source, ChildGeneration = childGeneration,
                    ChildIndex = childIndex, SourceIndex = sourceIndex, PhysicalOwner = physicalOwner,
                    AfterStateKnown = true };
        }
        catch (Exception)
        {
            // An exception after crossing the native boundary cannot prove cancellation
            // or absence of a queued operation. Keep the result explicitly uncertain.
            return submitted ? Invalidated(before, before, generation, "Native submission completion is unknown.")
                : Cancelled("Interaction admission raised an exception.");
        }
    }

    private static NativeCompanionBeckonObservation ObserveBeckonCore(bool frameworkThread,
        Companion* child, NativeCharacter* source, NativeCompanionBeckonReceipt receipt,
        Func<nint, bool> owned, Func<bool>? scopeCurrent)
    {
        if (receipt.Status != NativeCompanionBeckonSubmitStatus.Queued)
            return new(NativeCompanionBeckonObservationStatus.NotQueued, receipt.SourceGeneration,
                0, 0, 0, 0, "No native beckon was queued.");
        if (!frameworkThread || child == null || source == null || !ScopeIsCurrent(scopeCurrent)
            || (nint)child != receipt.Child || (nint)source != receipt.Source
            || !IsLocalSource(source) || !Owned(owned, child) || !IsLocalSource(source) || !ValidSource(source)
            || *(uint*)((byte*)source + GenerationOffset) != receipt.SourceGeneration
            || *(uint*)((byte*)child + GenerationOffset) != receipt.ChildGeneration
            || child->ObjectIndex != receipt.ChildIndex || source->ObjectIndex != receipt.SourceIndex
            || (nint)child->Owner != receipt.PhysicalOwner)
            return new(NativeCompanionBeckonObservationStatus.Invalidated, receipt.SourceGeneration,
                0, 0, 0, 0, "The exact source or companion was replaced.");
        var current = Capture(child);
        var pending = current.QueuedEmote == receipt.AfterQueuedEmote
            && current.Targeted == receipt.AfterTargeted && current.SameOwner == receipt.AfterSameOwner
            && current.OwnerIndex == receipt.AfterOwnerIndex;
        return new(pending ? NativeCompanionBeckonObservationStatus.Pending
            : NativeCompanionBeckonObservationStatus.QueueNoLongerMatches, receipt.SourceGeneration,
            current.QueuedEmote, current.Targeted, current.SameOwner, current.OwnerIndex,
            pending ? "Native queue is still pending." : "Native queue no longer matches; visual placement remains unverified.");
    }

    private static bool ScopeIsCurrent(Func<bool>? scope) { try { return scope?.Invoke() ?? true; } catch { return false; } }
    private static bool IsLocalSource(NativeCharacter* source)
        => Control.Addresses.GetLocalPlayer.Value != 0 && (NativeCharacter*)Control.GetLocalPlayer() == source;
    private static bool ValidSource(NativeCharacter* source) => source->ObjectIndex < 200 && (source->ObjectIndex & 1) == 0;
    private static bool Owned(Func<nint, bool> owned, Companion* child) { try { return owned((nint)child); } catch { return false; } }
    private static ushort OwnerIndex(NativeCharacter* source) => (ushort)(source->ObjectIndex / 2);
    private readonly record struct QueueEvidence(uint QueuedEmote, byte Targeted, byte SameOwner, ushort OwnerIndex);
    private static QueueEvidence Capture(Companion* child)
        => new(*(uint*)((byte*)child + QueuedEmoteOffset), *((byte*)child + TargetedEmoteOffset),
            *((byte*)child + SameOwnerOffset), *(ushort*)((byte*)child + OwnerObjectIndexOffset));
    private static NativeCompanionBeckonReceipt Cancelled(string reason)
        => new(NativeCompanionBeckonSubmitStatus.Cancelled, BeckonEmoteID, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false, reason);
    private static NativeCompanionBeckonReceipt Refused(string reason)
        => new(NativeCompanionBeckonSubmitStatus.NativeRefused, BeckonEmoteID, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false, reason);
    private static NativeCompanionBeckonReceipt Refused(QueueEvidence before, QueueEvidence after, uint generation, string reason)
        => new(NativeCompanionBeckonSubmitStatus.NativeRefused, BeckonEmoteID, generation,
            before.QueuedEmote, after.QueuedEmote, before.Targeted, after.Targeted,
            before.SameOwner, after.SameOwner, before.OwnerIndex, after.OwnerIndex,
            !before.Equals(after), false, reason) { AfterStateKnown = true };
    private static NativeCompanionBeckonReceipt Invalidated(QueueEvidence before, QueueEvidence after, uint generation, string reason, bool afterKnown = false)
        => new(NativeCompanionBeckonSubmitStatus.Invalidated, BeckonEmoteID, generation,
            before.QueuedEmote, after.QueuedEmote, before.Targeted, after.Targeted,
            before.SameOwner, after.SameOwner, before.OwnerIndex, after.OwnerIndex,
            !before.Equals(after), false, reason) { AfterStateKnown = afterKnown };

    private void OnReceive(Companion* companion, uint emote, NativeCharacter* source, GameObject* target)
    {
        var eligible = !disposed && framework.IsInFrameworkUpdateThread && source != null
            && Control.Addresses.GetLocalPlayer.Value != 0 && source == (NativeCharacter*)Control.GetLocalPlayer();
        var sourceGeneration = eligible ? *(uint*)((byte*)source + GenerationOffset) : 0;
        // A local coordinator may reserve a single shoulder/placement before the client queues
        // a child reaction. Never suppress another actor's native emote dispatch.
        try
        {
            if (eligible && companion != null && target == (GameObject*)companion && isOwnedChild((nint)companion)
                && admitInteraction is not null && !admitInteraction((nint)companion, emote)) return;
        }
        catch (Exception ex)
        {
            eligible = false;
            log.Warning(ex, "[LocalPreview] Local companion interaction admission could not be resolved.");
        }
        receive!.Original(companion, emote, source, target);
        if (!eligible || disposed) return;
        try { RouteAfterNative(companion, emote, source, target, sourceGeneration, isOwnedChild); }
        catch (Exception ex) { log.Warning(ex, "[LocalPreview] Local companion emote ownership could not be resolved."); }
    }

    internal static bool RouteAfterNative(Companion* companion, uint emote, NativeCharacter* source,
        GameObject* target, uint sourceGeneration, Func<nint, bool> isOwnedChild)
    {
        if (companion == null || source == null || target != (GameObject*)companion
            || emote is 0 or > ushort.MaxValue || Control.Addresses.GetLocalPlayer.Value == 0
            || (NativeCharacter*)Control.GetLocalPlayer() != source || !isOwnedChild((nint)companion)
            || Control.Addresses.GetLocalPlayer.Value == 0 || (NativeCharacter*)Control.GetLocalPlayer() != source
            || *(uint*)((byte*)source + GenerationOffset) != sourceGeneration
            || source->ObjectIndex >= 200 || (source->ObjectIndex & 1) != 0
            || *(uint*)((byte*)companion + QueuedEmoteOffset) != emote
            || *((byte*)companion + TargetedEmoteOffset) != 1
            || companion->EmoteOwnerObjectIndex != source->ObjectIndex / 2
            || !CanGrantLogicalOwnerReaction(companion, emote)) return false;
        companion->IsEmoteFromOwner = true;
        return true;
    }

    private static bool CanGrantLogicalOwnerReaction(Companion* companion, uint emote)
        // Special (+0x2395) is the native species-data cache used by the emote-8
        // consumer at 1750D9D. BehaviorSpecial (+0x245A) is the current behavior
        // substate used by the idle gate; those are different fields and meanings.
        // Only the reviewed beckon branches are gated. Other emotes retain their
        // previous behavior until their native reaction paths are reviewed.
        => emote != BeckonEmoteID || companion->Special is not (1 or 2 or 7 or 9);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        PhysicalClose.CloseHook(ref receive);
        PhysicalClose.Finish();
    }
}
