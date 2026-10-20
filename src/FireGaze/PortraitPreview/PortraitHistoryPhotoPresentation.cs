namespace FireGaze.PortraitPreview;

internal enum PortraitHistoryPhotoStatus
{
    Saved,
    Loading,
    Missing,
    Capacity,
    StorageError,
}

internal static class PortraitHistoryPhotoPresentation
{
    public static PortraitHistoryPhotoStatus Resolve(bool hasTexture, bool isLoading,
        bool hasDurableImage, PortraitStorageCode storageCode)
    {
        if (hasTexture) return PortraitHistoryPhotoStatus.Saved;
        if (storageCode == PortraitStorageCode.Capacity) return PortraitHistoryPhotoStatus.Capacity;
        if (storageCode is PortraitStorageCode.StorageError or PortraitStorageCode.InvalidPath
            or PortraitStorageCode.Conflict) return PortraitHistoryPhotoStatus.StorageError;
        if (isLoading || storageCode == PortraitStorageCode.Busy) return PortraitHistoryPhotoStatus.Loading;
        return hasDurableImage ? PortraitHistoryPhotoStatus.Loading : PortraitHistoryPhotoStatus.Missing;
    }

    public static string Label(PortraitHistoryPhotoStatus status) => status switch
    {
        PortraitHistoryPhotoStatus.Saved => "已保存照片",
        PortraitHistoryPhotoStatus.Loading => "照片加载中",
        PortraitHistoryPhotoStatus.Missing => "暂无照片",
        PortraitHistoryPhotoStatus.Capacity => "照片空间已满",
        PortraitHistoryPhotoStatus.StorageError => "照片写盘失败",
        _ => "暂无照片",
    };

    public static string FavoriteLabel(PortraitFavoritePhotoStatus status) => status switch
    {
        PortraitFavoritePhotoStatus.Available => "照片已保存，正在载入",
        PortraitFavoritePhotoStatus.Capacity => "照片空间已满",
        PortraitFavoritePhotoStatus.StorageError => "照片读取或写入失败",
        _ => "暂无照片",
    };
}
