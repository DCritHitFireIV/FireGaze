using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private IReadOnlyList<PortraitOpeningIssue> openingNativeIssues = [];
    private IReadOnlyList<PortraitOpeningIssue> ReadEditorOpeningIssues() => openingNativeIssues;

    private PortraitEditorOpeningSample? ReadEditorOpeningSample()
    {
        openingNativeIssues = [];
        if (disposed || !Plugin.Framework.IsInFrameworkUpdateThread)
        { openingNativeIssues = [PortraitOpeningAvailability.Issue("StateUnavailable")]; return null; }
        try
        {
            var source = new SafetySnapshotSource(this);
            var before = source.ReadBoundary();
            var observedIssues = PortraitOpeningAvailability.ConditionIssues(before.Conditions,
                before.EditorState > 0 || before.EditorView > 0).ToList();
            void AddIssue(string code) => observedIssues.Add(PortraitOpeningAvailability.Issue(code));
            if (!target.CanBlockSave) AddIssue("SaveProtectionUnavailable");
            if (plateMirrorClosePending) AddIssue(string.IsNullOrWhiteSpace(plateMirrorCloseError) ? "Restoring" : "RestoreFailed");
            if (photos.IsBusy || !photos.IsPhysicallyIdle) AddIssue("PhotosPending");
            if (before.Player is not { } boundaryPlayer || before.SourceAddress <= 0 || before.SourceGeneration == 0 || CurrentCharacter() is null)
                AddIssue("CharacterLoading");
            else
            {
                if (boundaryPlayer.CurrentHP == 0) AddIssue("Unconscious");
                if (boundaryPlayer.InCombat == true) AddIssue("InCombat");
                if (boundaryPlayer.IsCasting == true || boundaryPlayer.CastActionID is > 0)
                    AddIssue(boundaryPlayer.CastActionID == 5 ? "Teleporting" : "Casting");
                if (boundaryPlayer.IsMoving == true) AddIssue("Moving");
            }
            openingNativeIssues = Array.AsReadOnly(observedIssues.ToArray());
            // This SDK byte also reflects DirectInput acquisition. Both known states are
            // usable; SameOpeningBoundary still requires the exact same second reading.
            if (before.FrameworkAddress <= 0 || before.Frame is not { } frame || before.FocusByte is not (0 or 1)
                || before.Conditions is not { Length: 112 } flags || before.SourceAddress <= 0 || before.SourceGeneration == 0
                || before.Player is not { InCombat: { } combat, IsMoving: { } moving, IsCasting: { } casting,
                    CastActionID: { } cast } player || before.EditorAgent <= 0 || before.EditorState < 0 || before.EditorView < 0
                || CurrentCharacter() is not { } owner) return null;

            var editor = (AgentBannerEditor*)before.EditorAgent;
            var agentActive = editor->IsAgentActive();
            var gearsets = RaptureGearsetModule.Instance();
            var currentGearset = gearsets == null ? -1 : gearsets->CurrentGearsetIndex;
            PortraitEditorOpeningIdentity? ready = null;
            if (before.EditorState > 0 && target.TryResolve(out var identity, out _, out _) && target.IsCurrent(identity))
                ready = new((long)identity.Editor, (long)identity.State, (long)identity.Model, identity.Generation, identity.Character);
            // Native getters above can reenter plugins. Copy the state/backlinks only after them,
            // then confirm the whole root/source boundary without invoking another target getter.
            long stateOwner = 0, viewOwner = 0;
            var openType = -1;
            var openerSourceID = -1;
            if (before.EditorState > 0)
            {
                var stateAddress = (nint)before.EditorState;
                if (!PortraitSafetySnapshotNative.TryRead<AgentBannerEditorState>(stateAddress, out var state)) return null;
                stateOwner = (long)state.AgentBannerEditor;
                var nativeOpenType = state.OpenType;
                var enabledIndex = state.OpenerEnabledGearsetIndex;
                openType = (int)nativeOpenType;
                if (nativeOpenType == AgentBannerEditorState.EditorOpenType.AdventurerPlate) openerSourceID = 0;
                else if (gearsets != null && enabledIndex is >= 0 and < 100)
                    openerSourceID = gearsets->EnabledGearsetIndex2EntryIndex[enabledIndex];
                if (before.EditorView <= 0 || !PortraitSafetySnapshotNative.TryRead<nint>((nint)before.EditorView
                    + (int)Marshal.OffsetOf<CharaViewPortrait>(nameof(CharaViewPortrait.Agent)), out var viewAgent)) return null;
                viewOwner = (long)viewAgent;
            }
            var idle = !SourceCensusBlocksOperations && !NativeLiveEnabled && nativeLiveController?.IsPhysicallyIdle != false
                && !LightingFitMutationBlocked && !lightDrag.IsActive && !photos.IsBusy && photos.IsPhysicallyIdle
                && liveImageService?.IsPhysicallyIdle != false && lightingFitCapture?.IsPhysicallyIdle != false
                && window.IsSourceCensusIdle && !plateMirrorClosePending && !target.IsPlateMirrorActive;
            var after = source.ReadBoundary();
            if (!SameOpeningBoundary(before, after) || CurrentCharacter() != owner
                || RaptureGearsetModule.Instance() != gearsets || (gearsets == null ? -1 : gearsets->CurrentGearsetIndex) != currentGearset) return null;
            return new(true, Environment.CurrentManagedThreadId, frame, before.EditorAgent, before.EditorState,
                before.EditorView, stateOwner, viewOwner, agentActive, openType, openerSourceID, currentGearset,
                new(before.SourceAddress, before.SourceGeneration, owner.AccountID, owner.ContentID, player.TerritoryID,
                    player.JobID, player.X, player.Y, player.Z, player.CurrentHP, combat, moving, casting, cast),
                flags.ToArray(), session.IsActive, target.CanBlockSave, idle, ready);
        }
        catch (Exception)
        {
            openingNativeIssues = Array.AsReadOnly(openingNativeIssues.Append(PortraitOpeningAvailability.Issue("StateUnavailable")).ToArray());
            return null;
        }
    }

    private static bool SameOpeningBoundary(PortraitSafetyBoundary first, PortraitSafetyBoundary second)
        => first.FrameworkAddress == second.FrameworkAddress && first.Frame == second.Frame && first.FocusByte == second.FocusByte
            && first.SourceAddress == second.SourceAddress && first.SourceGeneration == second.SourceGeneration
            && first.ConditionAddress == second.ConditionAddress && first.ConditionMaxEntries == second.ConditionMaxEntries
            && first.Player == second.Player && first.EditorAgent == second.EditorAgent && first.EditorState == second.EditorState
            && first.EditorView == second.EditorView && first.Conditions is not null && second.Conditions is not null
            && first.Conditions.SequenceEqual(second.Conditions);
}
