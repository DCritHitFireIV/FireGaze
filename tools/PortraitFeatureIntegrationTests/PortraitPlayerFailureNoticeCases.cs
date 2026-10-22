using FireGaze.PortraitPreview;

internal static class PortraitPlayerFailureNoticeCases
{
    public static void Run(string root, Action<bool, string> check)
    {
        foreach (var operation in new[] { "beauty", "undo", "redo", "restore", "template", "favorite", "source-equipment" })
        {
            using var feature = CreateFeature(root, "notice-" + operation);
            var invoke = PrepareOperation(feature, operation);
            const string technical = "NativeOperationException diagnostic sentinel";
            const string nativeNotice = "本次肖像调整未能完成，请稍后重试。";
            feature.Native.LastError = technical;
            feature.Native.RejectApply = true;
            feature.Native.RejectedApplyNotice = nativeNotice;
            var before = feature.Native.Snapshot.ToJSON();
            var cursor = feature.Store.CurrentRecordID(feature.Key!.Value);
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var count = feature.ErrorMessages.Count;
                invoke();
                check(feature.ErrorMessages.Count == count + 1 && feature.Status == nativeNotice,
                    $"{operation} rejected attempt {attempt}: native callback and outer failure produce exactly one player notice");
            }
            feature.Native.RejectedApplyNotice = null;
            var fallbackCount = feature.ErrorMessages.Count;
            invoke();
            check(feature.ErrorMessages.Count == fallbackCount + 1 && feature.StatusIsError
                && !feature.Status.Contains(technical) && feature.Native.LastError == technical,
                $"{operation}: a later silent native refusal receives its own safe fallback notice");
            check(feature.Native.Snapshot.ToJSON() == before && feature.Store.CurrentRecordID(feature.Key.Value) == cursor,
                $"{operation}: notice deduplication does not change editor state or history on failure");
        }

        foreach (var nativeReports in new[] { false, true })
        {
            using var feature = CreateFeature(root, "notice-library-rollback-" + nativeReports);
            var invoke = PrepareOperation(feature, "template");
            const string nativeNotice = "本次肖像还原未能完成，请结束预览后重试。";
            feature.Native.RejectedApplyNotice = nativeReports ? nativeNotice : null;
            feature.Native.AfterEditorApply = () => { feature.Store.Dispose(); feature.Native.RejectApply = true; };
            var count = feature.ErrorMessages.Count;
            invoke();
            check(feature.ErrorMessages.Count == count + 1 && feature.StatusIsError
                && (nativeReports ? feature.Status == nativeNotice : feature.Status.Contains("还原状态无法确认")),
                $"library rollback nativeReports={nativeReports}: failed rollback reports once and preserves the useful failure status");
        }
    }

    private static PortraitPreviewFeature CreateFeature(string root, string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        var feature = new PortraitPreviewFeature(Path.Combine(directory, "history.json"));
        feature.SetLibraryValidation([new(100, 1 << 3, 1, 2)]);
        feature.Sample();
        feature.OpenPreview();
        return feature;
    }

    private static Action PrepareOperation(PortraitPreviewFeature feature, string operation)
    {
        switch (operation)
        {
            case "beauty": return () => feature.ChangeAppearance("hair", 2);
            case "undo": feature.ChangeAppearance("hair", 2); return feature.Undo;
            case "redo": feature.ChangeAppearance("hair", 2); feature.Undo(); return feature.Redo;
            case "restore":
                var record = feature.History.First().ID;
                feature.ChangeAppearance("hair", 2);
                return () => feature.RestoreHistory(record);
            case "template":
                var pages = Enumerable.Range(0, 20).Select(i => new PortraitGlamourPlate(i, new PortraitPiece[12])).ToArray();
                pages[0].Pieces[3] = new(100, 1, 1);
                feature.SetPlateObservations(pages);
                return () => feature.ApplyGlamourPlate(0);
            case "favorite":
                feature.SaveFavorite("恢复提示测试", []);
                feature.WaitFavorites();
                var favorite = feature.Favorites.Single().ID;
                feature.ChangeAppearance("hair", 2);
                return () => feature.RestoreFavorite(favorite);
            default:
                var source = new OwnedSourceKey(OwnedSourceKind.Inventory);
                feature.SetOwnedItems(new(feature.Owner, [new(100, [new(source, 100, 1, 0, 1, 1, 1, 0, false, OwnedReadStatus.Current)])], []),
                    PortraitSlot.Body, [new(100, "装备", 1, 2, true, "")]);
                return () => feature.EquipFromSources(PortraitSlot.Body, 100, []);
        }
    }
}
