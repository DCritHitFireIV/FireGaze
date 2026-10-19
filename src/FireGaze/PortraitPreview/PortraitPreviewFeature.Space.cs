namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    public PortraitComposition? CurrentComposition
        => session.IsActive && observedEditor is { } snapshot ? snapshot.Composition : null;

    public PortraitLightingSpaceBinding? CurrentLightingSpaceBinding
        => session.IsActive && observedEditor is { } snapshot && observedKey is { } key
            && PortraitLightingSpaceBinding.TryCreate(observedIdentity, key, snapshot, out var binding) ? binding : null;

    /// <summary>Commit one orbit/light gesture as one local portrait history operation.</summary>
    public void ApplyLightingSpace(PortraitLightingSpaceBinding expected, PortraitComposition desired) => Mutate(() =>
    {
        if (!CancelLightingFitForEdit() || !session.IsActive || !target.CanBlockSave || expected is null
            || !target.TryResolve(out var identity, out _, out _)
            || target.CaptureEditorState(identity) is not { } before || GetHistoryKey(identity, before) is not { } key
            || !expected.TryPrepare(identity, key, before, desired, out var merged))
        {
            Error("肖像已变化，请重新调整空间灯光。");
            return;
        }
        if (before.Composition == merged) return;
        CancelLightDrag();
        if (!target.SetLightingSpace(identity, before, merged))
        {
            Error(target.LastError ?? "空间灯光未能应用到肖像预览。");
            return;
        }
        // A failed post-write read never fabricates a snapshot or creates a history record.
        if (target.CaptureEditorState(identity) is not { } after || GetHistoryKey(identity, after) != key
            || !PortraitLightingSpaceBinding.MatchesSnapshot(before with { Composition = merged }, after)) return;
        observedEditor = after;
        _ = session.ObserveLook(after.Look);
        RecordDirectChange("空间灯光与相机");
    });
}
