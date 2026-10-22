using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

internal static unsafe class GazeCaptureTests
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var flags in new byte[] { 0, 1, 2, 3 })
        {
            using var fixture = new EditorFixture { Paused = true };
            using var target = new NativePortraitTarget(fixture, _ => true, _ => { });
            target.TryResolve(out var identity, out _, out _);
            fixture.SetHeadDirection(fixture.View, .12345679f, -.2345679f);
            fixture.SetEyeDirection(fixture.View, .024642944f, -.03250122f);
            fixture.Actor->LookAt.BannerCameraFollowFlag = (LookAtContainer.BannerCameraFollowFlags)flags;
            var original = target.CaptureEditorState(identity)!;
            var writesBefore = fixture.Calls;
            // Model the observed native solved-output drift independently of the requested control.
            fixture.Data.HeadDirection = new((Half).1236f, (Half)(-.2348f));
            fixture.Data.EyeDirection = new((Half).024734497f, (Half)(-.03253174f));
            var drifted = target.CaptureEditorState(identity)!;
            check(original.SameForHistory(drifted), $"derived gaze drift creates no history change with follow flags {flags}");
            check(drifted.Composition.HeadDirection == new PortraitVector2(.12345679f, -.2345679f)
                && drifted.Composition.EyeDirection == new PortraitVector2(.024642944f, -.03250122f),
                $"capture retains full precision gaze control with follow flags {flags}");
            check(fixture.Calls == writesBefore, $"gaze capture adds no native writes with follow flags {flags}");

            fixture.DeferGazeExport = true;
            fixture.SetEyeDirection(fixture.View, .024643044f, -.03250122f);
            fixture.Actor->LookAt.BannerCameraFollowFlag = (LookAtContainer.BannerCameraFollowFlags)flags;
            var smallEdit = target.CaptureEditorState(identity)!;
            check(!smallEdit.SameForHistory(drifted) && smallEdit.Composition.EyeDirection.X == .024643044f,
                $"a genuine gaze control change below 0.0001 remains a history edit with follow flags {flags}");

            fixture.Actor->LookAt.BannerCameraFollowFlag ^= LookAtContainer.BannerCameraFollowFlags.Head;
            var headToggled = target.CaptureEditorState(identity)!;
            check(!headToggled.SameForHistory(smallEdit), $"head-follow toggle remains a history edit from flags {flags}");
            fixture.Actor->LookAt.BannerCameraFollowFlag ^= LookAtContainer.BannerCameraFollowFlags.Eyes;
            var eyesToggled = target.CaptureEditorState(identity)!;
            check(!eyesToggled.SameForHistory(headToggled), $"eye-follow toggle remains a history edit from flags {flags}");

            fixture.View->CameraYaw += .00001f;
            check(!target.CaptureEditorState(identity)!.SameForHistory(eyesToggled),
                $"a camera change remains independently recorded with follow flags {flags}");
        }

        using (var fixture = new EditorFixture { Paused = true, DeferGazeExport = true })
        using (var target = new NativePortraitTarget(fixture, _ => true, _ => { }))
        {
            target.TryResolve(out var identity, out _, out _);
            fixture.SetHeadDirection(fixture.View, .12345679f, -.2345679f);
            fixture.SetEyeDirection(fixture.View, .3456789f, -.456789f);
            var original = target.CaptureEditorState(identity)!;
            var desired = original with { Composition = original.Composition with {
                HeadDirection = new(.2345679f, -.3456789f), EyeDirection = new(.456789f, -.5678912f),
                HeadFacingCamera = true, EyesFacingCamera = true } };
            check(target.ApplyEditorState(identity, desired)
                && target.CaptureEditorState(identity)!.SameForHistory(desired),
                "full float gaze restores before asynchronous derived export catches up");
            fixture.Data.HeadDirection = new((Half)(-.7f), (Half).8f);
            fixture.Data.EyeDirection = new((Half).9f, (Half)(-.8f));
            check(target.ApplyEditorState(identity, original)
                && target.CaptureEditorState(identity)!.SameForHistory(original),
                "full float gaze undo survives stale derived export after camera-follow restore");
            check(target.ApplyEditorState(identity, desired)
                && target.CaptureEditorState(identity)!.SameForHistory(desired),
                "full float gaze redo preserves exact controls and camera-follow flags");
            fixture.Actor->LookAt.BannerEyeDirection.X = float.NaN;
            check(target.CaptureEditorState(identity) is null, "nonfinite raw gaze control is rejected before entering history");
        }

        VerifyUndoChain(check);
    }

    private static void VerifyUndoChain(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "firegaze-gaze-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new PortraitHistoryStore(Path.Combine(directory, "history.json"));
            using var fixture = new EditorFixture { Paused = true };
            using var target = new NativePortraitTarget(fixture, _ => true, _ => { });
            target.TryResolve(out var identity, out _, out _);
            fixture.SetEyeDirection(fixture.View, -.23278809f, .08642578f);
            var before = target.CaptureEditorState(identity)!;
            var key = new PortraitHistoryKey(1, PortraitHistoryEntryPoint.GearsetPortrait, 0, "gaze-fixture");
            var baseline = store.Record(key, before.ToJSON()).Record!;
            var requested = before with { Composition = before.Composition with {
                HeadDirection = new(-.6723633f, -.52197266f), EyeDirection = new(.024642944f, -.03250122f),
                HeadFacingCamera = true, EyesFacingCamera = true } };
            var historical = store.Record(key, requested.ToJSON()).Record!;
            store.TryGetRecord(key, baseline.ID, out var baselineSelection);
            var previousOperation = store.CommitRestore(baselineSelection).Record!;
            store.TryGetRecord(key, historical.ID, out var restoreSelection);
            target.ApplyEditorState(identity, requested);
            store.CommitRestore(restoreSelection);
            var restored = target.CaptureEditorState(identity)!;

            fixture.Data.EyeDirection = new((Half).024734497f, (Half)(-.03253174f));
            var settled = target.CaptureEditorState(identity)!;
            if (!restored.SameForHistory(settled)) store.Record(key, settled.ToJSON());
            var hasUndo = store.TryGetUndo(key, out var undo);
            check(hasUndo && undo.Record.ID == previousOperation.ID,
                "native solved-eye drift does not insert a spurious operation before undo selection");
            var previous = hasUndo ? PortraitEditorSnapshot.FromJSON(undo.Record.SnapshotJSON) : null;
            var applied = previous is not null && target.ApplyEditorState(identity, previous);
            if (applied) store.CommitNavigation(undo);
            check(applied && target.CaptureEditorState(identity)!.SameForHistory(before),
                "undo after derived gaze settles restores the actual preceding complete snapshot");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
