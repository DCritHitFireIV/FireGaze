using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace FireGaze.LocalPreview.Runtime;

public sealed unsafe partial class ActorPreviewRuntime
{
    // Both current CN timeline functions return their native success in AL. The
    // installed ClientStructs declares them void; using those delegates for a
    // managed detour loses success even when Original already started the intro.
    // Keep the generated addresses and the audited BOOL/U1 return ABI instead.
    // See docs/local-preview-timeline-abi-2026-10-17.md.
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool PlayTimelineNative(ActionTimelineSequencer* sequence, ushort id, void* param);
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool PlayActionTimelineNative(TimelineContainer* timeline, ushort intro, ushort loop, void* param);
    // Reviewed current CN BattleChara virtual Update and Character DisableDraw.
    // Update calls the RenderFlags consumer before drawing. These signatures must
    // resolve uniquely; no fixed runtime addresses or managed method rewriting.
    private const string CharacterUpdateSignature = "4C 8B DC 53 48 81 EC E0 01 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 B0 01 00 00 80 89 6C 23 00 00 40";
    private const string CharacterDisableSignature = "40 53 48 83 EC 20 80 B9 9A 00 00 00 00 48 8B D9 7D ?? 48 81 C1 80 1B 00 00";
    private Hook<GameObject.Delegates.Update>? characterUpdate;
    private Hook<GameObject.Delegates.DisableDraw>? characterDisable;
    private Hook<CompanionContainer.Delegates.SetupCompanion>? companionSetup;
    private Hook<OrnamentContainer.Delegates.SetupOrnament>? ornamentSetup;
    private Hook<PlayTimelineNative>? timelinePlay;
    private Hook<PlayActionTimelineNative>? actionTimelinePlay;
    private bool hmsVisibilityProtection;
    private long hmsVisibilityRepairs;
    private Dictionary<ushort, ushort>? emoteTimelineIdentities;
    public long HmsVisibilityRepairs => hmsVisibilityRepairs;
    public void SetHmsVisibilityProtection(bool enabled) => hmsVisibilityProtection = !disposed && enabled;

    private void InstallOriginalHmsNativeBoundaries(IGameInteropProvider interop)
    {
        characterUpdate = Install(interop.HookFromSignature<GameObject.Delegates.Update>(CharacterUpdateSignature, CharacterUpdateDetour));
        characterDisable = Install(interop.HookFromSignature<GameObject.Delegates.DisableDraw>(CharacterDisableSignature, CharacterDisableDetour));
        companionSetup = Install(interop.HookFromAddress<CompanionContainer.Delegates.SetupCompanion>(CompanionContainer.Addresses.SetupCompanion.Value, CompanionSetupDetour));
        ornamentSetup = Install(interop.HookFromAddress<OrnamentContainer.Delegates.SetupOrnament>(OrnamentContainer.Addresses.SetupOrnament.Value, OrnamentSetupDetour));
        timelinePlay = Install(interop.HookFromAddress<PlayTimelineNative>(ActionTimelineSequencer.Addresses.PlayTimeline.Value, TimelinePlayDetour));
        actionTimelinePlay = Install(interop.HookFromAddress<PlayActionTimelineNative>(TimelineContainer.Addresses.PlayActionTimeline.Value, ActionTimelinePlayDetour));
    }

    private void CharacterUpdateDetour(GameObject* obj)
    {
        // HMS writes bit2 directly. Ignore that bit exclusively on our current
        // private display allocation, immediately before native consumption.
        if (framework.IsInFrameworkUpdateThread && !disposed && hmsVisibilityProtection && visualActor?.IsCurrentAllocation((NativeCharacter*)obj) == true)
        {
            var flags = (ulong)obj->RenderFlags;
            if ((flags & 2) != 0) { obj->RenderFlags = (FFXIVClientStructs.FFXIV.Client.Game.Object.VisibilityFlags)(flags & ~2UL); hmsVisibilityRepairs++; }
        }
        characterUpdate!.Original(obj);
    }

    private void CharacterDisableDetour(GameObject* obj)
    {
        // HMS Revert has no public pre-event. Its synchronous source teardown
        // enters this native boundary before source/clone draw resources vanish.
        if (framework.IsInFrameworkUpdateThread && !disposed && !ownCall && HasActivePreview && obj != null && (NativeCharacter*)obj == Bound())
        {
            try { BeforeHmsLifecycle(); }
            catch (Exception ex) { ReportHandoff(ex); }
        }
        characterDisable!.Original(obj);
    }

    private void CompanionSetupDetour(CompanionContainer* container, short id, uint param)
    {
        if (!framework.IsInFrameworkUpdateThread || Bypass || container == null || container->OwnerObject != Bound()) { companionSetup!.Original(container,id,param); return; }
        var source = Bound();
        if (source == null || (NativeCharacter*)Control.GetLocalPlayer() != source || container != &source->CompanionData) { companionSetup!.Original(container,id,param); return; }
        var sourceGeneration = *(uint*)((byte*)source + 0x74);
        var expectedRevision = revision;
        var previousId = container->CompanionId;
        var requestedId = unchecked((ushort)id);
        var intent = new NativeIntent(PreviewSlot.Minion,requestedId);
        RunNative(intent, () => {
            companionSetup!.Original(container,id,param);
            // Current CN can retain CompanionId == 0 after the actual minion exists.
            // Prefer the live child identity; a newly set positive canonical id is
            // accepted only while a child is still pending. A stale cached id must
            // not turn an unchanged unrelated child or no-op into native acceptance.
            bool accepted = false;
            if (!disposed && revision == expectedRevision && BindingCurrent() && Bound() == source && (NativeCharacter*)Control.GetLocalPlayer() == source
                && *(uint*)((byte*)source + 0x74) == sourceGeneration && container->OwnerObject == source)
            {
                var child = container->CompanionObject;
                accepted = requestedId == 0
                    ? child == null && container->CompanionId == 0
                    : child != null ? child->BaseId == requestedId
                    : container->CompanionId == requestedId && previousId != requestedId;
            }
            handoff.ReportDecision(intent,accepted);
            return true;
        }, false);
    }

    private void OrnamentSetupDetour(OrnamentContainer* container, short id, uint param)
    {
        if (!framework.IsInFrameworkUpdateThread || Bypass || container == null || container->OwnerObject != Bound()) { ornamentSetup!.Original(container,id,param); return; }
        var intent = new NativeIntent(PreviewSlot.Accessory,unchecked((ushort)id));
        RunNative(intent, () => {
            ornamentSetup!.Original(container,id,param);
            handoff.ReportDecision(intent, container->OwnerObject == Bound() && container->OrnamentId == unchecked((ushort)id));
            return true;
        }, false);
    }

    private bool IsSourceEmoteTimeline(ushort timeline, out ushort emote)
    {
        emote = 0;
        if (!framework.IsInFrameworkUpdateThread || Bypass || timeline is 0 or 1 or 3 or 34) return false;
        var source = Bound();
        if (source == null) return false;
        {
            // Original HMS plays cosmetic timelines with absent or stale EmoteId.
            // Resolve known emote content without treating ordinary movement/idle
            // as input. Shared animation rows still end the trial but never invent
            // a unique emote identity for history or GPose.
            if (emoteTimelineIdentities is null)
            {
                emoteTimelineIdentities = new();
                foreach (var entry in data.GetExcelSheet<Lumina.Excel.Sheets.Emote>())
                {
                    if (entry.RowId == 0 || entry.RowId > ushort.MaxValue) continue;
                    foreach (var variant in entry.ActionTimeline)
                    {
                        if (variant.RowId is 0 or 1 or 3 or 34 || variant.RowId > ushort.MaxValue) continue;
                        var key=(ushort)variant.RowId;
                        if (emoteTimelineIdentities.TryGetValue(key,out var previous) && previous != entry.RowId) emoteTimelineIdentities[key]=0;
                        else if (!emoteTimelineIdentities.ContainsKey(key)) emoteTimelineIdentities[key]=(ushort)entry.RowId;
                    }
                }
            }
            return emoteTimelineIdentities.TryGetValue(timeline,out emote);
        }
    }

    private bool TimelinePlayDetour(ActionTimelineSequencer* sequence, ushort id, void* param)
    {
        if (!framework.IsInFrameworkUpdateThread) return timelinePlay!.Original(sequence,id,param);
        var source = Bound();
        if (source == null || sequence != &source->Timeline.TimelineSequencer || !IsSourceEmoteTimeline(id,out var emote)) return timelinePlay!.Original(sequence,id,param);
        var intent = new NativeIntent(PreviewSlot.Emote,emote);
        return RunNative(intent, () => {
            bool accepted = timelinePlay!.Original(sequence,id,param);
            handoff.ReportDecision(intent,accepted);
            if (accepted && emote!=0) Raise(ExternalEmoteAccepted,emote);
            return accepted;
        },false);
    }

    private bool ActionTimelinePlayDetour(TimelineContainer* timeline, ushort intro, ushort loop, void* param)
    {
        if (!framework.IsInFrameworkUpdateThread) return actionTimelinePlay!.Original(timeline,intro,loop,param);
        var source = Bound();
        if (source == null || timeline != &source->Timeline || !(IsSourceEmoteTimeline(intro,out var emote) || IsSourceEmoteTimeline(loop,out emote))) return actionTimelinePlay!.Original(timeline,intro,loop,param);
        var intent = new NativeIntent(PreviewSlot.Emote,emote);
        return RunNative(intent, () => {
            bool accepted = actionTimelinePlay!.Original(timeline,intro,loop,param);
            handoff.ReportDecision(intent,accepted);
            if (accepted && emote!=0) Raise(ExternalEmoteAccepted,emote);
            return accepted;
        },false);
    }
}
