namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitPlatePhotoFit(int Width, int Height, int OffsetX, int OffsetY);

/// <summary>Contains every visible card edge in the existing photo canvas without changing its aspect ratio.</summary>
internal static class PortraitPlatePhotoGeometry
{
    public static bool TryContain(int width, int height, int canvasWidth, int canvasHeight,
        out PortraitPlatePhotoFit fit)
    {
        fit = default;
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384
            || canvasWidth is <= 0 or > 16384 || canvasHeight is <= 0 or > 16384) return false;
        var scale = Math.Min(canvasWidth / (double)width, canvasHeight / (double)height);
        var outputWidth = Math.Max(1, (int)Math.Round(width * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(height * scale));
        fit = new(outputWidth, outputHeight, (canvasWidth - outputWidth) / 2, (canvasHeight - outputHeight) / 2);
        return outputWidth <= canvasWidth && outputHeight <= canvasHeight;
    }
}
