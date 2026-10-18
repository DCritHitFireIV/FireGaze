namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitHistoryPhotoLayout(int Columns, float Width, float ImageHeight)
{
    public static PortraitHistoryPhotoLayout For(float available, float gap, float scale)
    {
        available = Math.Max(1, available);
        gap = Math.Max(0, gap);
        var desired = Math.Max(1, 128 * scale);
        var columns = Math.Max(1, (int)((available + gap) / (desired + gap)));
        var width = Math.Max(1, (available - (columns - 1) * gap) / columns);
        return new(columns, width, width * 1.6f);
    }
}
