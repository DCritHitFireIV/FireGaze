using System;
using System.Diagnostics;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace FireGaze.LocalPreview.Runtime;

public sealed unsafe partial class ActorPreviewRuntime
{
    private TrialSpecialHotbarCatalog? specialActionCatalog;
    private long specialActionRevision;
    private long specialActionPendingUntil;
    private NativeCharacter* specialActionActor;
    private uint specialActionGeneration;
    private ushort specialActionTimeline;
    private ushort specialActionPreviewId;
    private bool specialActionOnMount;
    private NativePreviewActor? specialActionDisplay;
    private nint specialActionDraw;
    private NativeCharacter* specialActionRoot;
    private nint specialActionRootDraw;

    /// <summary>A native cosmetic timeline currently owns the clone's base animation lane.
    /// The short start grace allows the native timeline request to seat before movement resumes.</summary>
    public bool SpecialActionActive
    {
        get
        {
            if (specialActionTimeline == 0 || disposed || revision != specialActionRevision
                || !TryGetSpecialActionActor(specialActionOnMount, specialActionPreviewId, out var actor)
                || visualActor != specialActionDisplay || actor != specialActionActor
                || (nint)actor->DrawObject != specialActionDraw
                || SpecialGeneration(actor) != specialActionGeneration
                || specialActionDisplay?.IsCurrentAllocation(specialActionRoot) != true
                || (nint)specialActionRoot->DrawObject != specialActionRootDraw) return false;
            return actor->Timeline.TimelineSequencer.TimelineIds[0] == specialActionTimeline
                || Stopwatch.GetTimestamp() < specialActionPendingUntil;
        }
    }

    /// <summary>Plays only an action listed by the current trial's MountAction/OrnamentAction table.
    /// No ActionManager call, server operation or write to the genuine player is performed.</summary>
    public bool TryPlaySpecialAction(uint actionId)
    {
        if (disposed || !framework.IsInFrameworkUpdateThread || ownCall || nativeDepth != 0
            || actionId == 0 || !BindingCurrent()) return false;
        var currentRevision = revision;
        var owned = visualActor;
        var source = Bound();
        var sourceGeneration = SpecialGeneration(source);
        bool mount = effects.TryGetValue(PreviewSlot.Mount, out var effect);
        if (!mount && !effects.TryGetValue(PreviewSlot.Accessory, out effect)) return false;
        specialActionCatalog ??= new TrialSpecialHotbarCatalog(data.Excel);
        var bar = specialActionCatalog.GetBar(mount, effect.Id, revision);
        if (bar == null) return false;
        TrialSpecialAction? action = null;
        foreach (var candidate in bar.Actions)
            if (candidate is { Kind: TrialSpecialActionKind.NativeAction } && candidate.Id == actionId) { action = candidate; break; }
        if (action == null || !TryGetSpecialActionActor(mount, effect.Id, out var actor)) return false;

        var actorGeneration = SpecialGeneration(actor);
        var display = owned?.Actor;
        if (display == null) return false;
        var displayDraw = display->DrawObject;
        var draw = actor->DrawObject;
        bool Current() => !disposed && revision == currentRevision && visualActor == owned
            && Bound() == source && source != null
            && TryGetSpecialActionActor(mount, effect.Id, out var current) && current == actor
            && owned?.IsCurrentAllocation(display) == true && display->DrawObject == displayDraw
            && SpecialGeneration(source) == sourceGeneration
            && actor->DrawObject == draw && SpecialGeneration(actor) == actorGeneration;
        if (!Current()) return false;
        var previousOwnCall = ownCall;
        ownCall = true;
        try
        {
            // Lease before the native call, which can re-enter hooks. A replaced preview
            // revision or native allocation invalidates it without restoring old state.
            specialActionRevision = currentRevision;
            specialActionActor = actor;
            specialActionGeneration = actorGeneration;
            specialActionTimeline = action.Timeline;
            specialActionPreviewId = effect.Id;
            specialActionOnMount = mount;
            specialActionDisplay = owned;
            specialActionDraw = (nint)draw;
            specialActionRoot = display;
            specialActionRootDraw = (nint)displayDraw;
            specialActionPendingUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
            actor->Timeline.BaseOverride = 0;
            actor->Timeline.TimelineSequencer.PlayTimeline(action.Timeline);
            bool current = Current();
            if (!current) specialActionTimeline = 0;
            return current;
        }
        catch (Exception ex)
        {
            specialActionTimeline = 0;
            log?.Warning(ex, "[LocalPreview] The local special action could not be played.");
            return false;
        }
        finally { ownCall = previousOwnCall; }
    }

    private bool TryGetSpecialActionActor(bool mount, ushort expectedId, out NativeCharacter* actor)
    {
        actor = null;
        var currentRevision = revision;
        var owned = visualActor;
        if (disposed || !framework.IsInFrameworkUpdateThread || owned is not { IsReady: true }
            || !BindingCurrent()) return false;
        var source = Bound();
        var sourceGeneration = SpecialGeneration(source);
        var display = owned.Actor;
        // The owned-actor lookup is native and can re-enter another plugin. Complete
        // every service/native lookup before checking the final raw hierarchy.
        if (!BindingCurrent() || Bound() != source || source == null || disposed
            || revision != currentRevision || display == null || display == source
            || visualActor != owned || owned is not { IsReady: true }
            || !owned.IsCurrentAllocation(display) || SpecialGeneration(source) != sourceGeneration
            || display->DrawObject == null || !display->DrawObject->IsVisible
            || display->Timeline.OwnerObject != display
            || display->Timeline.TimelineSequencer.Parent != display) return false;
        var slot = mount ? PreviewSlot.Mount : PreviewSlot.Accessory;
        if (!effects.TryGetValue(slot, out var effect) || effect.Id != expectedId) return false;
        if (mount)
        {
            if (display->Mount.OwnerObject != display || display->Mount.MountId != expectedId
                || !owned.IsCurrentMountChild(display->Mount.MountObject)) return false;
            actor = display->Mount.MountObject;
        }
        else
        {
            if (display->OrnamentData.OwnerObject != display || display->OrnamentData.OrnamentId != expectedId) return false;
            actor = display;
        }
        return true;
    }

    private static uint SpecialGeneration(NativeCharacter* actor) => actor == null ? 0 : *(uint*)((byte*)actor + 0x74);
}
