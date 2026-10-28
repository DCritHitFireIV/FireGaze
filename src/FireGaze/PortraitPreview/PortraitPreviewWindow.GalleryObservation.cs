namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private readonly PortraitFavoriteGalleryPublisher favoriteGallery = new();

    // Historical detached draw observation only. This getter has no UI, native,
    // texture-cache or disk side effect, even when invoked from diagnostics.
    public PortraitFavoriteGallerySnapshot FavoriteGalleryObservation => favoriteGallery.Read();

    private PortraitFavoriteGalleryContext FavoriteGalleryContext
        => feature.SourceCharacter is { } owner
            ? new(owner.AccountID, owner.ContentID, feature.PreviewSessionRevision) : default;

    private void InvalidateFavoriteGalleryFrame()
        => favoriteGallery.Begin(FavoriteGalleryContext, false, false);
}
