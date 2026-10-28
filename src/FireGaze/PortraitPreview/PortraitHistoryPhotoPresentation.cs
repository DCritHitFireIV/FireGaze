namespace FireGaze.PortraitPreview;

internal enum PortraitHistoryPhotoStatus
{
    Saved,
    Loading,
    Missing,
    Capacity,
    StorageError,
    ReadError,
    Generating,
}

internal static class PortraitHistoryPhotoPresentation
{
    public static IReadOnlyList<PortraitHistoryListItem> SelectPhotographed(
        IReadOnlyList<PortraitHistoryListItem> records, Func<long,bool> hasPhotograph)
        => records.Where(record => hasPhotograph(record.ID)).ToArray();

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
        PortraitHistoryPhotoStatus.ReadError => "照片读取失败",
        PortraitHistoryPhotoStatus.Generating => "照片正在自动生成…",
        _ => "暂无照片",
    };

    public static string FavoriteLabel(PortraitFavoritePhotoStatus status) => status switch
    {
        PortraitFavoritePhotoStatus.Available => "照片已保存，正在载入",
        PortraitFavoritePhotoStatus.Capacity => "照片空间已满",
        PortraitFavoritePhotoStatus.StorageError => "照片读取或写入失败",
        PortraitFavoritePhotoStatus.Pending => "照片正在自动生成…",
        PortraitFavoritePhotoStatus.SourceNotReady => "当前照片来源尚未准备好",
        _ => "请用原角色在原肖像中载入补拍",
    };
}
