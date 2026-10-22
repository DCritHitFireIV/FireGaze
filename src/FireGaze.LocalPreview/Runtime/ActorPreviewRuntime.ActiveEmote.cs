using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;

namespace FireGaze.LocalPreview.Runtime;

public sealed unsafe partial class ActorPreviewRuntime
{
    /// <summary>Observes an already playing emote on the current private display.
    /// Pending requests and residual metadata cannot supply a GPose entry action.</summary>
    public (ushort EmoteId, byte Pose)? GetActivePreviewEmote()
    {
        if (disposed || !framework.IsInFrameworkUpdateThread || deferredEmote
            || !BindingCurrent() || visualActor is not { IsReady: true } owned
            || !effects.TryGetValue(PreviewSlot.Emote, out var effect)) return null;
        long expectedRevision = revision;
        var source = Bound();
        var display = owned.Actor;
        // Native actor lookup may re-enter a cancellation or replacement. Resolve
        // the managed binding first, then finish with allocation and timeline reads.
        if (!BindingCurrent() || Bound() != source || source == null || display == null
            || disposed || revision != expectedRevision || deferredEmote || visualActor != owned
            || !owned.IsReady || !owned.IsCurrentAllocation(display)
            || display == source || display->DrawObject == null || !display->DrawObject->IsVisible
            || display->EmoteController.OwnerObject != (BattleChara*)display
            || display->Timeline.OwnerObject != display || display->Timeline.TimelineSequencer.Parent != display
            || !effects.TryGetValue(PreviewSlot.Emote, out var current) || current != effect
            || !PreviewEmoteIdentityMatches(display, effect.Id) || !IsEmoteTimeline(display, effect.Id)) return null;
        return (effect.Id, display->EmoteController.CPoseState);
    }

    private bool PreviewEmoteIdentityMatches(FFXIVClientStructs.FFXIV.Client.Game.Character.Character* display, ushort id)
    {
        if (data.GetExcelSheet<Emote>().GetRowOrDefault(id) is not { } row) return false;
        if (row.EmoteMode.RowId is not (1 or 2)) return display->EmoteController.EmoteId == id;
        // The native sit mode transition can clear EmoteId while its intro/loop
        // is already playing. Only the matching sit mode and mode argument can
        // establish this exceptional identity; a residual clip alone cannot.
        if (display->EmoteController.EmoteId != 0 && display->EmoteController.EmoteId != id) return false;
        return display->Mode == (CharacterModes)row.EmoteMode.Value.ConditionMode
            && display->ModeParam == (byte)row.EmoteMode.RowId;
    }
}
