namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitWorkspaceRegion(float X, float Y, float Width, float Height);
internal readonly record struct PortraitWorkspaceGeometry(bool Stacked, PortraitWorkspaceRegion Navigation,
    PortraitWorkspaceRegion Image, PortraitWorkspaceRegion Options, float BodyHeight, float FooterHeight, float Gap);

/// <summary>One body budget keeps the image, editing controls and fixed footer inside the window.</summary>
internal static class PortraitWorkspaceLayout
{
    internal static PortraitWorkspaceGeometry For(float width, float height, float scale)
    {
        scale = float.IsFinite(scale) && scale > 0 ? scale : 1f;
        width = float.IsFinite(width) && width > 0 ? width : 1f;
        height = float.IsFinite(height) && height > 0 ? height : 1f;
        var stacked = width / scale < 980f;
        var gap = MathF.Min(12f * scale, MathF.Min(width, height) * .04f);
        var footer = MathF.Min((stacked ? 72f : 40f) * scale, height * .26f);
        var body = MathF.Max(1f, height - footer - gap);
        var navigation = MathF.Min(176f * scale, width * (stacked ? .28f : .18f));
        if (stacked)
        {
            var imageHeight = MathF.Min(246f * scale, body * .43f);
            var editingY = imageHeight + gap;
            var editingHeight = MathF.Max(1f, body - editingY);
            return new(true, new(0, editingY, navigation, editingHeight), new(0, 0, width, imageHeight),
                new(navigation + gap, editingY, MathF.Max(1f, width - navigation - gap), editingHeight), body, footer, gap);
        }
        var imageWidth = MathF.Min(320f * scale, width * .28f);
        var imageX = navigation + gap;
        var optionX = imageX + imageWidth + gap;
        return new(false, new(0, 0, navigation, body), new(imageX, 0, imageWidth, body),
            new(optionX, 0, MathF.Max(1f, width - optionX), body), body, footer, gap);
    }
}
