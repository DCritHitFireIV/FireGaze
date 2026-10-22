using FireGaze.PortraitPreview;

internal static class PortraitStoragePlayerMessageCases
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var failures = new List<string>();
        void Expect(bool passed, string name)
        {
            if (passed) check(true, name);
            else { failures.Add(name); Console.WriteLine("FAIL " + name); }
        }

        var favoriteDirectory = Path.Combine(root, "favorites-load-private-path");
        Directory.CreateDirectory(Path.Combine(favoriteDirectory, "portrait-favorites-v1.json"));
        using (var feature = new PortraitPreviewFeature(Path.Combine(favoriteDirectory, "history.json")))
        {
            var evidence = feature.FavoritesLoadErrorForTest;
            Expect(!string.IsNullOrWhiteSpace(evidence), "the actual unreadable favorite destination retains its original error evidence");
            Expect(feature.FavoritesStatus.Contains("收藏") && feature.FavoritesStatus.Contains("原文件已保留")
                && !feature.FavoritesStatus.Contains(evidence!, StringComparison.Ordinal)
                && !feature.FavoritesStatus.Contains(favoriteDirectory, StringComparison.Ordinal),
                "an unreadable favorite file explains preservation without showing an OS error or private path");
        }

        var historyDirectory = Path.Combine(root, "history-load-private-path");
        Directory.CreateDirectory(historyDirectory);
        var historyFile = Path.Combine(historyDirectory, "history.json");
        File.WriteAllText(historyFile, "{");
        using (var feature = new PortraitPreviewFeature(historyFile))
        {
            var evidence = feature.Store.LoadError;
            Expect(feature.Store.IsReadOnly && !string.IsNullOrWhiteSpace(evidence) && File.ReadAllText(historyFile) == "{",
                "a real corrupt history remains intact and keeps its diagnostic error");
            Expect(feature.HistoryStatus.Contains("历史记录") && feature.HistoryStatus.Contains("原文件已保留")
                && !feature.HistoryStatus.Contains(evidence!, StringComparison.Ordinal),
                "corrupt history shows a player explanation instead of the JSON parser exception");
        }

        using (var feature = new PortraitPreviewFeature(Path.Combine(root, "favorite-failure-message", "history.json")))
        {
            const string evidence = "PRIVATE_STORAGE_FAILURE_marker";
            var failed = PortraitFavoritePersistence.RunAsync(() => throw new IOException(evidence)).GetAwaiter().GetResult();
            Expect(failed.Status == PortraitFavoritesWriteStatus.PersistenceFailed && failed.Reason.Contains(evidence),
                "the actual asynchronous exception retains technical evidence in its detached result");
            feature.PublishFavoriteForTest(failed);
            Expect(feature.StatusIsError && feature.FavoritesStatus.Contains("未能保存")
                && !feature.FavoritesStatus.Contains(evidence) && !feature.Status.Contains(evidence),
                "unexpected favorite write exceptions stay out of both the library and persistent workspace status");

            foreach (var (status, storage, expected) in new (PortraitFavoritesWriteStatus, PortraitStorageCode?, string)[]
            {
                (PortraitFavoritesWriteStatus.InvalidInput, null, "名称"),
                (PortraitFavoritesWriteStatus.NotFound, null, "不存在"),
                (PortraitFavoritesWriteStatus.ReadOnlyCorrupt, null, "原文件"),
                (PortraitFavoritesWriteStatus.TooLarge, null, "容量"),
                (PortraitFavoritesWriteStatus.TooLarge, PortraitStorageCode.Capacity, "空间不足"),
                (PortraitFavoritesWriteStatus.PersistenceFailed, PortraitStorageCode.Busy, "正在"),
                (PortraitFavoritesWriteStatus.PersistenceFailed, PortraitStorageCode.StorageError, "本地存储"),
                (PortraitFavoritesWriteStatus.Conflict, null, "更新"),
            })
            {
                feature.PublishFavoriteForTest(new(status, evidence, StorageCode: storage));
                Expect(feature.StatusIsError && feature.FavoritesStatus.Contains(expected)
                    && !feature.FavoritesStatus.Contains(evidence) && !feature.Status.Contains(evidence)
                    && feature.Favorites.Count == 0,
                    "favorite structured failure provides specific player guidance without raw details: " + status + "/" + storage);
            }
            feature.PublishFavoriteForTest(new(PortraitFavoritesWriteStatus.Saved));
            Expect(feature.FavoritesStatus.Contains("已保存") && !feature.FavoritesStatus.Contains(evidence),
                "a later successful favorite save clears the failed library message");
        }
        check(failures.Count == 0, "storage player messages: " + string.Join("; ", failures));
    }
}
