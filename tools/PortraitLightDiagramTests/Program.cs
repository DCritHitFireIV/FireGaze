using System.Numerics;
using FireGaze.PortraitPreview;

var passed=0;var failed=0;
void Check(bool condition,string label){Console.WriteLine((condition?"PASS ":"FAIL ")+label);if(condition)passed++;else failed++;}
#if DIAGRAM63
// A wrong canvas radius/axis mapping would cause the drawn marker and native pointer angle to disagree.
Check(PortraitLightDiagram.TryCreate(new(20,30),new(240,200),out var disc)
    && disc.Center==new Vector2(140,130) && disc.Radius==84,"diagram is centered inside padded rectangular canvas");
Check(PortraitLightDiagram.TryAnglesAtPointer(disc,new(140,130),PortraitLightView.Top,false,out var above)
    && above.VerticalDegrees==-90 && above.HorizontalDegrees==0,"top canvas center preserves the upper hidden hemisphere");
Check(PortraitLightDiagram.TryAnglesAtPointer(disc,new(140,130),PortraitLightView.Top,true,out var below)
    && below.VerticalDegrees==90,"explicit lower hidden hemisphere produces opposite vertical pole");
Check(PortraitLightDiagram.TryAnglesAtPointer(disc,new(224,130),PortraitLightView.Top,false,out var right)
    && Math.Abs(right.VerticalDegrees)<.001 && Math.Abs(right.HorizontalDegrees-90)<.001,"right rim maps to positive horizontal side");
Check(PortraitLightDiagram.TryAnglesAtPointer(disc,new(5000,130),PortraitLightView.Top,false,out var clamped)
    && Math.Abs(clamped.HorizontalDegrees-90)<.001,"drag outside canvas clamps to circle without unbounded angles");
Check(PortraitLightDiagram.TryAnglesAtPointer(disc,new(140,130),PortraitLightView.Side,true,out var left)
    && Math.Abs(left.VerticalDegrees)<.001 && Math.Abs(left.HorizontalDegrees+90)<.001,"side center preserves negative hidden lateral hemisphere");
Check(PortraitLightDiagram.TryMarker(disc,new(0,0),PortraitLightView.Top,out var marker)
    && marker==new Vector2(140,46),"forward top projection is drawn at exact matching rim");
Check(PortraitLightDiagram.TryHiddenSide(new(-30,0),PortraitLightView.Top,out var negativeTop) && !negativeTop,
    "top projection retains positive native hidden height");
Check(PortraitLightDiagram.TryHiddenSide(new(0,-30),PortraitLightView.Side,out var negativeSide) && negativeSide,
    "side projection retains negative native hidden lateral side");
Check(!PortraitLightDiagram.TryCreate(new(float.NaN,0),new(200,200),out _),"nonfinite origin cannot create drawing coordinates");
Check(!PortraitLightDiagram.TryCreate(Vector2.Zero,new(20,200),out _),"too-small canvas cannot become a division-by-zero target");
Check(!PortraitLightDiagram.TryAnglesAtPointer(disc,new(float.PositiveInfinity,0),PortraitLightView.Top,false,out _),"nonfinite pointer input cannot reach native angle math");
Check(!PortraitLightDiagram.TryMarker(disc,new(float.NaN,0),PortraitLightView.Top,out _),"invalid live angles cannot create marker coordinates");
Check(!PortraitLightDiagram.TryHiddenSide(new(0,0),(PortraitLightView)99,out _),"unknown view cannot silently flip a hidden hemisphere");
foreach(var scale in new[]{.75f,1,1.5f,2})
{
    Check(PortraitLightDiagram.TryCreate(new Vector2(20,30)*scale,new Vector2(240,200)*scale,out var scaled,16*scale)
        && Vector2.Distance(scaled.Center,new Vector2(140,130)*scale)<.001f && Math.Abs(scaled.Radius-84*scale)<.001,
        $"diagram keeps matching hit geometry at {scale:P0} font scale");
}
#else
Check(false,"portrait light diagram geometry is absent; UI coordinates cannot map to guarded direction editing");
#endif
#if LIGHT_WINDOW_LAYOUT
// A fixed physical minimum would hide the lower controls at large font scales on a short display.
foreach(var scale in new[]{.75f,1,1.5f,2})
foreach(var work in new[]{new Vector2(3840,2160),new Vector2(1920,1080),new Vector2(1280,720),new Vector2(800,600)})
{
    var bounds=PortraitLightWindowLayout.ForViewport(work,scale);
    Check(bounds.Maximum.X<work.X && bounds.Maximum.Y<work.Y && bounds.Minimum.X<=bounds.Maximum.X && bounds.Minimum.Y<=bounds.Maximum.Y,
        $"light window controls remain reachable within {work.X}x{work.Y} at {scale:P0} scale");
    Check(bounds.Preferred.X<=bounds.Maximum.X && bounds.Preferred.Y<=bounds.Maximum.Y && bounds.Preferred.X>=bounds.Minimum.X && bounds.Preferred.Y>=bounds.Minimum.Y,
        "light window preferred size is constrained between its current viewport bounds");
}
var normal=PortraitLightWindowLayout.ForViewport(new(1920,1080),1);
Check(normal.Minimum==new Vector2(290,360) && normal.Preferred==new Vector2(340,450) && normal.Maximum==new Vector2(560,650),
    "fitting light window preserves familiar dimensions");
foreach(var work in new[]{Vector2.Zero,new Vector2(float.NaN,float.PositiveInfinity),new Vector2(16,32)})
{
    var bounds=PortraitLightWindowLayout.ForViewport(work,2);
    Check(float.IsFinite(bounds.Maximum.X) && float.IsFinite(bounds.Maximum.Y) && bounds.Maximum.X>0 && bounds.Maximum.Y>0,
        "unavailable work area cannot reach ImGui as nonfinite light window dimensions");
}
#else
Check(false,"light window has no viewport-constrained minimum and can hide controls on a short display");
#endif
Console.WriteLine($"PortraitLightDiagramTests: {passed} passed, {failed} failed");
return failed==0?0:1;
