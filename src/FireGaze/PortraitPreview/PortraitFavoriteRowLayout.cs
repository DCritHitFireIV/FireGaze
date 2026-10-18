using System.Numerics;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitFavoriteActionLayout(Vector2 Offset, Vector2 Size);

/// <summary>One uniform clipper pitch, with a scrollable metadata lane above independently wrapped actions.</summary>
internal sealed record PortraitFavoriteRowLayout(float PhotoWidth, float PhotoHeight, float TextWidth,
    float MetadataHeight, float RowHeight, IReadOnlyList<PortraitFavoriteActionLayout> Actions)
{
    public static PortraitFavoriteRowLayout For(float availableWidth, float scale, float buttonHeight,
        float textLinePitch, Vector2 spacing, IReadOnlyList<float> buttonWidths)
    {
        var photoWidth = 80 * scale;
        var photoHeight = 128 * scale;
        var textStart = photoWidth + spacing.X;
        var textWidth = Math.Max(1, availableWidth - textStart);
        var metadataHeight = textLinePitch * 5;
        var startY = metadataHeight + spacing.Y;
        var x = 0f;
        var y = startY;
        var actions = new List<PortraitFavoriteActionLayout>(buttonWidths.Count);
        foreach (var width in buttonWidths)
        {
            if (x > 0 && x + width > textWidth) { x = 0; y += buttonHeight + spacing.Y; }
            actions.Add(new(new(textStart + x, y), new(width, buttonHeight)));
            x += width + spacing.X;
        }
        var actionBottom = actions.Count == 0 ? metadataHeight : y + buttonHeight;
        return new(photoWidth, photoHeight, textWidth, metadataHeight,
            Math.Max(photoHeight, actionBottom) + spacing.Y, actions);
    }
}
