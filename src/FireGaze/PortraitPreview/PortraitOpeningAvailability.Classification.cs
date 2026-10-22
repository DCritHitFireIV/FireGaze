namespace FireGaze.PortraitPreview;

internal sealed partial record PortraitOpeningAvailability
{
    internal static PortraitOpeningAvailability Create(PortraitEditorOpeningSample? sample, string kind,
        IEnumerable<PortraitOpeningIssue>? observedIssues = null, bool opening = false)
    {
        var issues = observedIssues?.ToList() ?? [];
        void Add(string code) => issues.Add(Issue(code));
        if (sample is null)
        {
            if (issues.Count == 0) Add("StateUnavailable");
            return Finish(false, false, opening, issues);
        }
        var source = sample.Source;
        if (!sample.SaveGuardReady) Add("SaveProtectionUnavailable");
        if (source is null || source.Address <= 0 || source.Generation == 0 || source.AccountID == 0 || source.ContentID == 0)
            Add("CharacterLoading");
        else
        {
            if (source.CurrentHP == 0) Add("Unconscious");
            if (source.InCombat) Add("InCombat");
            if (source.IsCasting || source.CastActionID != 0) Add(source.CastActionID == 5 ? "Teleporting" : "Casting");
            if (source.IsMoving) Add("Moving");
            if (!float.IsFinite(source.X) || !float.IsFinite(source.Y) || !float.IsFinite(source.Z)) Add("StateUnavailable");
        }
        var editorOpen = sample.State > 0 || sample.View > 0 || sample.AgentActive || sample.ReadyIdentity is not null;
        issues.AddRange(ConditionIssues(sample.Conditions, editorOpen));
        if (!sample.IsFrameworkThread || sample.ThreadID <= 0 || sample.Agent <= 0 || sample.State < 0 || sample.View < 0)
            Add("StateUnavailable");
        if (sample.PreviewActive) Add("PreviewActive");
        if (!sample.OperationsIdle && !sample.PreviewActive && !issues.Any(issue => issue.Code is "RestoreFailed" or "Restoring" or "PhotosPending")) Add("PreviousOperationPending");
        if (kind == "gearset" && sample.CurrentGearsetID is < 0 or > 99) Add("GearsetUnavailable");
        if (editorOpen && sample.OpenType is 1 or 2 && sample.OpenType != (kind == "gearset" ? 1 : 2)) Add("OtherEditorOpen");
        if (editorOpen && (sample.State <= 0 || sample.View <= 0 || sample.OpenType is not (1 or 2))) Add("EditorLoading");
        return Finish(issues.Count == 0 && !editorOpen && !opening, editorOpen, opening, issues);
    }
}

internal sealed record PortraitOpeningFailure(long Serial, string? Entry, string TechnicalReason, string Stage, bool CanRetry,
    PortraitOpeningAvailability Availability, PortraitEditorOpeningSample? Observed)
{
    internal PortraitOpeningFailure Detached() => this with { Observed = Observed?.Detached() };
}
