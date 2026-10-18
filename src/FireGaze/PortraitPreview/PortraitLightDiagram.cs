using System.Numerics;

namespace FireGaze.PortraitPreview;

internal readonly record struct PortraitLightDisc(Vector2 Center,float Radius);

/// <summary>Shared hit/drawing geometry; these are direction diagrams, not camera-space positions.</summary>
internal static class PortraitLightDiagram
{
    public static bool TryCreate(Vector2 origin,Vector2 size,out PortraitLightDisc disc,float padding=16)
    {
        disc=default;
        if (!Finite(origin) || !Finite(size) || !float.IsFinite(padding) || padding<0) return false;
        var radius=Math.Min(size.X,size.Y)*.5f-padding;
        if (radius<=0) return false;
        disc=new(origin+size*.5f,radius);
        return Finite(disc.Center) && float.IsFinite(radius);
    }

    public static bool TryAnglesAtPointer(PortraitLightDisc disc,Vector2 pointer,PortraitLightView view,
        bool negativeHidden,out PortraitLightAngles angles)
    {
        angles=default;
        return Valid(disc) && Finite(pointer)
            && PortraitLightDirection.TryFromProjection((pointer-disc.Center)/disc.Radius,view,negativeHidden,out angles);
    }

    public static bool TryMarker(PortraitLightDisc disc,PortraitLightAngles angles,PortraitLightView view,out Vector2 marker)
    {
        marker=default;
        if (!Valid(disc) || !PortraitLightDirection.TryProject(angles.VerticalDegrees,angles.HorizontalDegrees,view,out var point)) return false;
        marker=disc.Center+point*disc.Radius;
        return Finite(marker);
    }

    public static bool TryHiddenSide(PortraitLightAngles angles,PortraitLightView view,out bool negative)
    {
        negative=false;
        if (view is not (PortraitLightView.Top or PortraitLightView.Side)
            || !PortraitLightDirection.TryDirection(angles.VerticalDegrees,angles.HorizontalDegrees,out var direction)) return false;
        negative=view==PortraitLightView.Top ? direction.Y<0 : direction.X<0;
        return true;
    }

    private static bool Valid(PortraitLightDisc disc)=>Finite(disc.Center) && float.IsFinite(disc.Radius) && disc.Radius>0;
    private static bool Finite(Vector2 vector)=>float.IsFinite(vector.X) && float.IsFinite(vector.Y);
}
