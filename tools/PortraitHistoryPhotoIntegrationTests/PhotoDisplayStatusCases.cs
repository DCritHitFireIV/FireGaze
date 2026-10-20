using FireGaze.PortraitPreview;

internal static class PhotoDisplayStatusCases
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            checks++;
            Console.WriteLine("PASS " + name);
        }

        Check(PortraitHistoryPhotoPresentation.Resolve(hasTexture: true, isLoading: false,
            hasDurableImage: true, storageCode: PortraitStorageCode.Success)
            == PortraitHistoryPhotoStatus.Saved, "resident history image is reported as saved");
        Check(PortraitHistoryPhotoPresentation.Resolve(hasTexture: false, isLoading: true,
            hasDurableImage: true, storageCode: PortraitStorageCode.Success)
            == PortraitHistoryPhotoStatus.Loading, "queued history image is reported as loading");
        Check(PortraitHistoryPhotoPresentation.Resolve(hasTexture: false, isLoading: false,
            hasDurableImage: false, storageCode: PortraitStorageCode.Success)
            == PortraitHistoryPhotoStatus.Missing, "uncaptured history image is reported as missing");
        Check(PortraitHistoryPhotoPresentation.Resolve(hasTexture: false, isLoading: false,
            hasDurableImage: false, storageCode: PortraitStorageCode.Capacity)
            == PortraitHistoryPhotoStatus.Capacity, "capacity refusal is reported separately");
        Check(PortraitHistoryPhotoPresentation.Resolve(hasTexture: false, isLoading: false,
            hasDurableImage: true, storageCode: PortraitStorageCode.StorageError)
            == PortraitHistoryPhotoStatus.StorageError, "write failure is reported separately");
        Check(PortraitHistoryPhotoPresentation.Label(PortraitHistoryPhotoStatus.Saved) == "已保存照片",
            "saved photo label is explicit");
        Check(PortraitHistoryPhotoPresentation.Label(PortraitHistoryPhotoStatus.Loading) == "照片加载中",
            "loading photo label is explicit");
        Check(PortraitHistoryPhotoPresentation.Label(PortraitHistoryPhotoStatus.Missing) == "暂无照片",
            "missing photo label remains distinct");
        Check(PortraitHistoryPhotoPresentation.Label(PortraitHistoryPhotoStatus.Capacity) == "照片空间已满",
            "capacity photo label is explicit");
        Check(PortraitHistoryPhotoPresentation.Label(PortraitHistoryPhotoStatus.StorageError) == "照片写盘失败",
            "storage failure label is explicit");
        Check(PortraitHistoryPhotoPresentation.FavoriteLabel(PortraitFavoritePhotoStatus.Available) == "照片已保存，正在载入",
            "favorite image availability is not confused with an empty slot");
        Check(PortraitHistoryPhotoPresentation.FavoriteLabel(PortraitFavoritePhotoStatus.Missing) == "暂无照片",
            "missing favorite photo stays distinct from a saved image");
        Check(PortraitHistoryPhotoPresentation.FavoriteLabel(PortraitFavoritePhotoStatus.Capacity) == "照片空间已满",
            "favorite capacity refusal stays visible");
        Check(PortraitHistoryPhotoPresentation.FavoriteLabel(PortraitFavoritePhotoStatus.StorageError) == "照片读取或写入失败",
            "favorite storage faults are not hidden as a missing photo");
        return checks;
    }

    internal static int RunServiceCases(PortraitHistoryPhotos photos, PortraitHistoryRecord record)
    {
        var method = typeof(PortraitHistoryPhotos).GetMethod("GetHistoryPhotoStatus")
            ?? throw new InvalidOperationException("history photo status API is missing");
        var checks = 0;
        void Check(PortraitHistoryPhotoStatus expected, string name)
        {
            var actual = (PortraitHistoryPhotoStatus)method.Invoke(photos, [record.ID])!;
            if (actual != expected) throw new InvalidOperationException($"{name}: expected {expected}; got {actual}");
            checks++;
            Console.WriteLine("PASS " + name);
        }

        photos.SetRecords([record], 0);
        Check(PortraitHistoryPhotoStatus.Missing, "history record without a saved image reports missing");
        photos.SetRecords([record], PortraitHistoryStore.MaximumBytes);
        Check(PortraitHistoryPhotoStatus.Capacity, "history record at the photo capacity limit reports capacity refusal");
        return checks;
    }
}
