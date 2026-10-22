namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitWorkspaceRegion(float X, float Y, float Width, float Height);
internal readonly record struct PortraitWorkspaceGeometry(bool Stacked, PortraitWorkspaceRegion Navigation,
    PortraitWorkspaceRegion Image, PortraitWorkspaceRegion Options, float BodyHeight, float FooterHeight, float Gap);

/// <summary>One body budget keeps the image, editing controls and fixed footer inside the window.</summary>
internal static class PortraitWorkspaceLayout
{
    // Logical-pixel minimums keep each wide-workbench column usable while the
    // splitter positions are moved or the window is resized.
    internal const float MinimumNavigationWidth = 160f;
    internal const float MinimumImageWidth = 220f;
    internal const float MinimumOptionsWidth = 320f;

    internal static (float Navigation, float Image) MoveDivider(PortraitWorkspaceGeometry layout,
        float scale, int divider, float delta)
    {
        scale = float.IsFinite(scale) && scale > 0 ? scale : 1f;
        var navigation = layout.Navigation.Width;
        var image = layout.Image.Width;
        if (layout.Stacked || !float.IsFinite(delta)) return (navigation / scale, image / scale);
        if (image <= 0)
            navigation = Math.Clamp(navigation + delta, MinimumNavigationWidth * scale,
                MathF.Max(MinimumNavigationWidth * scale, navigation + layout.Options.Width - MinimumOptionsWidth * scale));
        else if (divider == 0)
        {
            var pair = navigation + image;
            navigation = Math.Clamp(navigation + delta, MinimumNavigationWidth * scale,
                MathF.Max(MinimumNavigationWidth * scale, pair - MinimumImageWidth * scale));
            image = pair - navigation;
        }
        else if (divider == 1)
            image = Math.Clamp(image + delta, MinimumImageWidth * scale,
                MathF.Max(MinimumImageWidth * scale, image + layout.Options.Width - MinimumOptionsWidth * scale));
        return (navigation / scale, image / scale);
    }

    internal static PortraitWorkspaceGeometry For(float width, float height, float scale, bool imageVisible = true,
        float? navigationLogicalWidth = null, float? imageLogicalWidth = null)
    {
        scale = float.IsFinite(scale) && scale > 0 ? scale : 1f;
        width = float.IsFinite(width) && width > 0 ? width : 1f;
        height = float.IsFinite(height) && height > 0 ? height : 1f;
        var stacked = width / scale < 980f;
        var gap = MathF.Min(12f * scale, MathF.Min(width, height) * .04f);
        var footer = MathF.Min((stacked ? 72f : 40f) * scale, height * .26f);
        var body = MathF.Max(1f, height - footer - gap);
        var navigation = MathF.Min(176f * scale, width * (stacked ? .28f : .18f));
        if (!imageVisible)
        {
            if (!stacked)
            {
                var hiddenAvailable = MathF.Max(1f, width - gap);
                var hiddenMinimumNavigation = MinimumNavigationWidth * scale;
                var hiddenMinimumOptions = MinimumOptionsWidth * scale;
                navigation = Math.Clamp((navigationLogicalWidth ?? 176f) * scale,
                    hiddenMinimumNavigation, MathF.Max(hiddenMinimumNavigation, hiddenAvailable - hiddenMinimumOptions));
            }
            return new(stacked, new(0, 0, navigation, body), default,
                new(navigation + gap, 0, MathF.Max(1f, width - navigation - gap), body), body, footer, gap);
        }
        if (stacked)
        {
            var imageHeight = MathF.Min(246f * scale, body * .43f);
            var editingY = imageHeight + gap;
            var editingHeight = MathF.Max(1f, body - editingY);
            return new(true, new(0, editingY, navigation, editingHeight), new(0, 0, width, imageHeight),
                new(navigation + gap, editingY, MathF.Max(1f, width - navigation - gap), editingHeight), body, footer, gap);
        }
        var available = MathF.Max(1f, width - 2f * gap);
        var minimumNavigation = MinimumNavigationWidth * scale;
        var minimumImage = MinimumImageWidth * scale;
        var minimumOptions = MinimumOptionsWidth * scale;
        var navigationDesired = (navigationLogicalWidth ?? 176f) * scale;
        var navigationMaximum = MathF.Max(minimumNavigation, available - minimumImage - minimumOptions);
        var navigationWidth = Math.Clamp(navigationDesired, minimumNavigation, navigationMaximum);
        var imageDesired = (imageLogicalWidth ?? MathF.Min(320f, width / scale * .28f)) * scale;
        var imageMaximum = MathF.Max(minimumImage, available - navigationWidth - minimumOptions);
        var imageWidth = Math.Clamp(imageDesired, minimumImage, imageMaximum);
        var imageX = navigationWidth + gap;
        var optionX = imageX + imageWidth + gap;
        return new(false, new(0, 0, navigationWidth, body), new(imageX, 0, imageWidth, body),
            new(optionX, 0, MathF.Max(1f, width - optionX), body), body, footer, gap);
    }
}
