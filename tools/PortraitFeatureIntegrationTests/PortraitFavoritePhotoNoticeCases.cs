using FireGaze.PortraitPreview;

internal static class PortraitFavoritePhotoNoticeCases
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "favorite-photo-notices");
        Directory.CreateDirectory(directory);
        using var feature = new PortraitPreviewFeature(Path.Combine(directory, "history.json"));
        feature.Sample(); feature.OpenPreview();
        feature.FavoritePhotoForTest = PortraitFavoritePhotoStatus.Pending;
        feature.SaveFavorite("待配图收藏", []); feature.WaitFavorites();
        feature.Native.Ready = false;
        feature.SaveFavorite("这次不能保存", []);
        var failure = feature.FavoritesStatus;
        feature.SampleLibrary();
        check(feature.FavoritesStatus == failure && !failure.Contains("正在自动生成"),
            "an earlier pending photo cannot overwrite a later rejected favorite action");

        feature.Native.Ready = true;
        feature.SaveFavorite("另一待配图收藏", []); feature.WaitFavorites();
        feature.RestoreFavorite(feature.Favorites[0].ID);
        var restored = feature.FavoritesStatus;
        feature.SampleLibrary();
        check(restored.StartsWith("已恢复收藏") && feature.FavoritesStatus == restored,
            "an earlier pending photo cannot overwrite a later explicit favorite restore result");

        var delayedResults = new List<(bool Preserved, string Case)>();
        foreach (var failNewAction in new[] { false, true })
        {
            var delayedDirectory = Path.Combine(root, "favorite-delayed-" + failNewAction);
            Directory.CreateDirectory(delayedDirectory);
            using var delayed = new PortraitPreviewFeature(Path.Combine(delayedDirectory, "history.json"));
            delayed.Sample(); delayed.OpenPreview();
            delayed.SaveFavorite("已有收藏", []); delayed.WaitFavorites();
            var savedID = delayed.Favorites.Single().ID;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            delayed.FavoritePhotoForTest = PortraitFavoritePhotoStatus.Pending;
            delayed.SaveFavoriteWhenReleasedForTest("延迟完成的收藏", entered, release);
            string latest;
            try
            {
                if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Favorite worker never entered.");
                if (failNewAction)
                {
                    delayed.Native.Ready = false;
                    delayed.SaveFavorite("这次不能保存", []);
                }
                else delayed.RestoreFavorite(savedID);
                latest = delayed.FavoritesStatus;
            }
            finally { release.Set(); }
            delayed.WaitFavorites(); delayed.SampleLibrary();
            check(delayed.FavoriteMetadataDrainSucceeded && delayed.Favorites.Any(entry => entry.Name == "延迟完成的收藏"),
                "late favorite completion still persists accepted metadata after a newer " + (failNewAction ? "failure" : "restore"));
            delayedResults.Add((delayed.FavoritesStatus == latest, failNewAction ? "failure" : "restore"));
            Console.WriteLine($"OBSERVE delayed favorite after {delayedResults[^1].Case}: preserved={delayedResults[^1].Preserved}");
        }
        foreach (var result in delayedResults)
            check(result.Preserved, "late favorite completion cannot overwrite a newer explicit " + result.Case + " notice");
    }
}

namespace FireGaze.PortraitPreview
{
    internal sealed unsafe partial class PortraitPreviewFeature
    {
        // Delay the real metadata write at its worker boundary, without changing the
        // production queue, completion publication or persistence behavior.
        public void SaveFavoriteWhenReleasedForTest(string name, ManualResetEventSlim entered, ManualResetEventSlim release)
        {
            var snapshot = Native.Snapshot.Copy();
            var owner = Owner!.Value;
            QueueFavoriteWrite(() =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Favorite worker was not released.");
                return favoritesStore.Add(snapshot, owner.ContentID, "来源角色", name, []);
            }, (owner.ContentID, snapshot));
        }
    }
}
