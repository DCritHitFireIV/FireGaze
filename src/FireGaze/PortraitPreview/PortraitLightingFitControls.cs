using System.Numerics;

namespace FireGaze.PortraitPreview;

/// <summary>Limited monotone RGB targets and a subject region belonging to one editor source.</summary>
internal sealed class PortraitLightingFitControls
{
    private readonly int[][] points = [[64,128,192],[64,128,192],[64,128,192]];
    private string source = "";
    public PortraitSampleRegion? Region { get; private set; }
    public IReadOnlyList<int> GetPoints(int channel) => Array.AsReadOnly(points[channel].ToArray());
    public void SetPoint(int channel, int point, int value)
    {
        if (channel is < 0 or > 2 || point is < 0 or > 2) return;
        var row = points[channel];
        row[point] = Math.Clamp(value, point == 0 ? 0 : row[point-1], point == 2 ? 255 : row[point+1]);
    }
    public void ResetCurves()
    {
        foreach (var row in points) { row[0]=64; row[1]=128; row[2]=192; }
    }
    public void BindSource(string key)
    {
        if (source == key) return;
        source=key;
        Region=null;
    }
    public bool SelectRegion(Vector2 from, Vector2 to)
    {
        if (!PortraitSampleRegion.TryFromDrag(from,to,out var region)) return false;
        Region=region;
        return true;
    }
    public bool TryTarget(out PortraitColorToneTarget? target) => PortraitColorToneTarget.TryCreate(points[0],points[1],points[2],out target);
}
