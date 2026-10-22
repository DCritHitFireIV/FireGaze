using FireGaze.PortraitPreview;

internal static class PortraitPhotoHistoryPublicationCases
{
    internal static void Run(string root, Action<bool, string> check)
    {
        using var feature = new PortraitPreviewFeature(Path.Combine(root, "photographic-history-feature.json"));
        feature.Store.EnablePhotoBackedPublication();
        feature.Sample(); feature.OpenPreview();
        var first = feature.Store.CurrentRecordID(feature.Key!.Value)!.Value;
        feature.EditComposition(c => c with { CameraYaw = .25f }); feature.RecordDirect();
        feature.EditComposition(c => c with { CameraYaw = .5f }); feature.RecordDirect();
        var last = feature.Store.CurrentRecordID(feature.Key!.Value)!.Value;
        check(feature.Store.Count == 3 && feature.History.Count == 3 && feature.CanUndo
            && feature.History[0].ID == last && feature.History[0].IsCurrent,
            "real operation history immediately exposes every admitted edit when no photos exist");
        feature.Store.SynchronizePhotographedRecords(new HashSet<long> { first, last });
        feature.RefreshRows();
        check(feature.History.Count == 3 && feature.History[0].IsCurrent
            && feature.Store.GetPublishedRecords(feature.Key!.Value).Select(row => row.ID).SequenceEqual(new[] { last, first }),
            "photograph availability leaves operation history complete and limits only the photographed query");
        feature.Undo();
        check(feature.Native.Snapshot.Composition.CameraYaw == .25f && feature.CanRedo
            && feature.History.Count == 4 && feature.History.Single(row => row.IsCurrent).ID != first
            && feature.History.Single(row => row.IsCurrent).ID != last,
            "undo exposes the real unphotographed intermediate state and its audit record without inventing a photo");
        feature.Redo();
        check(feature.Native.Snapshot.Composition.CameraYaw == .5f
            && feature.History.Single(row => row.ID == last).IsCurrent,
            "redo retains both the correct native state and existing photo-history cursor");
        feature.Store.SynchronizePhotographedRecords(new HashSet<long> { first });
        feature.RefreshRows();
        check(feature.History.Count == 5 && feature.History.Single(row => row.IsCurrent).ID == last && feature.CanUndo,
            "photo eviction cannot hide operation history or destroy session undo");
    }
}
