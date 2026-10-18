using System.Numerics;

namespace FireGaze.PortraitPreview;

/// <summary>Explicit user-selected subject region in normalized image coordinates; no segmentation claim.</summary>
internal readonly record struct PortraitSampleRegion(float Left, float Top, float Right, float Bottom)
{
    public static bool TryFromDrag(Vector2 start, Vector2 end, out PortraitSampleRegion region)
    {
        region = default;
        if (!Valid(start) || !Valid(end) || start.X == end.X || start.Y == end.Y) return false;
        region = new(Math.Min(start.X,end.X), Math.Min(start.Y,end.Y), Math.Max(start.X,end.X), Math.Max(start.Y,end.Y));
        return true;
    }

    public bool TryMask(int width, int height, out bool[] mask)
    {
        mask = [];
        if (!Valid(new(Left,Top)) || !Valid(new(Right,Bottom)) || Right <= Left || Bottom <= Top
            || width <= 0 || height <= 0 || (long)width * height > 1_048_576) return false;
        var selected = new bool[width * height];
        var count = 0;
        for (var y=0;y<height;y++)
        for (var x=0;x<width;x++)
        {
            var horizontal=(x+.5)/width; var vertical=(y+.5)/height;
            if (horizontal < Left || horizontal >= Right || vertical < Top || vertical >= Bottom) continue;
            selected[y*width+x]=true; count++;
        }
        if (count < 64) return false;
        mask = selected;
        return true;
    }

    private static bool Valid(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y)
        && point.X is >=0 and <=1 && point.Y is >=0 and <=1;
}
