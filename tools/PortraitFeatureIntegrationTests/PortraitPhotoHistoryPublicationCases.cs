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
        check(feature.Store.Count == 3 && feature.History.Count == 0 && feature.CanUndo,
            "real feature keeps rapid intermediate edits undoable without publishing empty photo history");
        feature.Store.SynchronizePhotographedRecords(new HashSet<long> { first, last });
        feature.RefreshRows();
        check(feature.History.Select(row => row.ID).SequenceEqual(new[] { last, first })
            && feature.History[0].IsCurrent,
            "real feature publishes only exact photographed history and marks the true navigation cursor");
        feature.Undo();
        check(feature.Native.Snapshot.Composition.CameraYaw == .25f && feature.CanRedo
            && feature.History.All(row => !row.IsCurrent),
            "undo restores an unphotographed intermediate state without inventing a photo for it");
        feature.Redo();
        check(feature.Native.Snapshot.Composition.CameraYaw == .5f
            && feature.History.Single(row => row.ID == last).IsCurrent,
            "redo retains both the correct native state and existing photo-history cursor");
        feature.Store.SynchronizePhotographedRecords(new HashSet<long> { first });
        feature.RefreshRows();
        check(feature.History.Count == 1 && feature.History[0].ID == first && feature.CanUndo,
            "photo eviction removes its gallery reference without destroying session undo");
    }
}
