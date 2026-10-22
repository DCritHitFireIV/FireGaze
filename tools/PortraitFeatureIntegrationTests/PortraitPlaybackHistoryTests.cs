using FireGaze.PortraitPreview;

internal static class PortraitPlaybackHistoryTests
{
    internal static void Run(string root, Action<bool, string> check)
    {
        using (var feature = NewPaused(root, "playback-no-edits"))
        {
            feature.OpenPreview(); feature.SetSyncPlate(true);
            feature.EditComposition(c => c with { BannerTimeline = 2, PoseClassJob = 3, AnimationProgress = .1f, Paused = false });
            feature.Sample(pointer: true); feature.Sample(pointer: false); feature.Sample(advanceMilliseconds: 600);
            check(feature.Store.Count == 1, "selecting and playing another pose does not create a history record");
            feature.EditComposition(c => c with { AnimationProgress = .6f }); feature.Sample(advanceMilliseconds: 3000);
            check(feature.Store.Count == 1 && feature.ObservedSnapshot?.Composition is { BannerTimeline: 2, Paused: false, AnimationProgress: .6f },
                "ignored playback still updates the live observed editor rather than freezing preview state");
            check(feature.MirroredSnapshot?.Composition is { BannerTimeline: 2, Paused: false, AnimationProgress: .6f },
                "local plate synchronization keeps the actual playing pose and progress");
            feature.FlushOnly();
            check(feature.Store.Count == 1, "closing while only playing cannot flush a playback-only history record");
        }

        using (var feature = NewPaused(root, "playback-pause-final"))
        {
            feature.EditComposition(c => c with { BannerTimeline = 2, PoseClassJob = 3, AnimationProgress = .1f, Paused = false });
            feature.Sample(pointer: true); feature.Sample(pointer: false); feature.Sample(advanceMilliseconds: 600);
            feature.EditComposition(c => c with { Paused = true, AnimationProgress = .4f }); feature.Sample(pointer: true);
            feature.EditComposition(c => c with { AnimationProgress = .8f }); feature.Sample(pointer: true, advanceMilliseconds: 3000);
            check(feature.Store.Count == 1, "a paused timeline drag remains pending while its continuous gesture is held");
            feature.Sample(pointer: false);
            var stored = Current(feature);
            check(feature.Store.Count == 2 && stored.Composition is { BannerTimeline: 2, PoseClassJob: 3, Paused: true, AnimationProgress: .8f },
                "pausing commits the final chosen pose and frozen frame once after release");
            feature.Sample(advanceMilliseconds: 600); feature.FlushOnly();
            check(feature.Store.Count == 2, "idle samples and close cannot duplicate the paused final frame");
            feature.EditComposition(c => c with { Paused = false, AnimationProgress = .9f }); feature.Sample(); feature.Sample(advanceMilliseconds: 600);
            check(feature.Store.Count == 2, "resuming a recorded paused pose does not append a play record");
            feature.EditComposition(c => c with { Paused = true, AnimationProgress = 1.2f }); feature.Sample(); feature.Sample(advanceMilliseconds: 600);
            check(feature.Store.Count == 3 && Current(feature).Composition.AnimationProgress == 1.2f,
                "pausing the same pose later records its new frozen frame");
        }

        foreach (var edit in new[] { "camera", "background", "appearance" })
        using (var feature = NewPaused(root, "playback-edit-" + edit))
        {
            feature.EditComposition(c => c with { BannerTimeline = 2, PoseClassJob = 3, AnimationProgress = .1f, Paused = false });
            feature.Sample(pointer: true); feature.Sample(pointer: false);
            if (edit == "camera") feature.EditComposition(c => c with { CameraYaw = .3f });
            else if (edit == "background") feature.EditComposition(c => c with { Background = 1 });
            else
            {
                var look = feature.Native.Snapshot.Look.Copy(); look.Pieces[(int)PortraitSlot.Body] = new(600, 0, 0);
                feature.Native.Snapshot = feature.Native.Snapshot with { Look = look };
            }
            feature.Sample(pointer: true); feature.EditComposition(c => c with { AnimationProgress = .7f });
            feature.Sample(pointer: true); feature.Sample(pointer: false);
            var stored = Current(feature);
            check(feature.Store.Count == 2 && stored.Composition is { BannerTimeline: 2, PoseClassJob: 3, Paused: false },
                $"{edit} history owns the real playing state so its photograph can match the visible editor");
            check(edit == "camera" ? stored.Composition.CameraYaw == .3f : edit == "background" ? stored.Composition.Background == 1
                : stored.Look.Pieces[(int)PortraitSlot.Body].ItemID == 600,
                $"{edit} editing remains fully recorded while animation is playing");
            check(!feature.History[0].Label.Contains("播放") && !feature.History[0].Label.Contains("姿势"),
                $"{edit} history label does not misreport the ignored playback controls");
            feature.Sample(advanceMilliseconds: 600);
            check(feature.Store.Count == 2, $"{edit} sampling during playback does not duplicate the same non-animation edit");
            feature.EditComposition(c => c with { Paused = true, AnimationProgress = .9f }); feature.Sample(); feature.Sample(advanceMilliseconds: 600);
            check(feature.Store.Count == 3 && Current(feature).Composition is { BannerTimeline: 2, Paused: true, AnimationProgress: .9f },
                $"a later pause after {edit} retains the final playing pose as a separate history edit");
        }

        using (var feature = NewPaused(root, "playback-direct-and-restore"))
        {
            feature.EditComposition(c => c with { BannerTimeline = 2, Paused = false, AnimationProgress = .5f }); feature.Sample();
            var look = feature.Native.Snapshot.Look.Copy(); look.Pieces[(int)PortraitSlot.Body] = new(700, 0, 0);
            feature.Native.Snapshot = feature.Native.Snapshot with { Look = look };
            feature.RecordDirect();
            check(feature.Store.Count == 2 && Current(feature).Composition is { BannerTimeline: 2, Paused: false }
                && Current(feature).Look.Pieces[(int)PortraitSlot.Body].ItemID == 700,
                "direct equipment history retains the real playing pose and actual look for a matching photograph");
            feature.Sample(advanceMilliseconds: 600); feature.FlushOnly();
            check(feature.Store.Count == 2, "a direct edit during playback cannot create a second passive pose entry");
            feature.Undo(); var count = feature.Store.Count; feature.Sample(advanceMilliseconds: 600);
            check(feature.Store.Count == count && feature.CanRedo,
                "undo rebases passive playback sampling without erasing redo or adding a phantom pose edit");
        }

        using (var feature = new PortraitPreviewFeature(Path.Combine(root, "playback-unpaused-baseline.json")))
        {
            feature.Sample();
            feature.EditComposition(c => c with { BannerTimeline = 2, AnimationProgress = .6f }); feature.Sample(); feature.Sample(advanceMilliseconds: 600);
            check(feature.Store.Count == 1, "an initially playing editor also ignores later playing pose changes");
            feature.EditComposition(c => c with { Paused = true, AnimationProgress = .8f }); feature.Sample(); feature.Sample(advanceMilliseconds: 600);
            check(feature.Store.Count == 2 && Current(feature).Composition is { BannerTimeline: 2, Paused: true, AnimationProgress: .8f },
                "the first pause from an initially playing editor records the final action");
        }
    }

    private static PortraitPreviewFeature NewPaused(string root, string name)
    {
        var feature = new PortraitPreviewFeature(Path.Combine(root, name + ".json"));
        feature.EditComposition(c => c with { Paused = true, AnimationProgress = .25f });
        feature.Sample();
        return feature;
    }

    private static PortraitEditorSnapshot Current(PortraitPreviewFeature feature)
        => PortraitEditorSnapshot.FromJSON(feature.Store.GetRecords(feature.Key!.Value)[0].SnapshotJSON)!;
}
