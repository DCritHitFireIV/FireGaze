using System.Numerics;
#if HAS_NEARBY_WINDOW_LAYOUT
using FireGaze.UI;
#endif

var passed = 0;
var failed = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (ok) passed++; else failed++;
}
#if HAS_NEARBY_WINDOW_LAYOUT
void Fits(NearbyWindowBounds bounds, Vector2 origin, Vector2 extent, string name)
{
    Check(float.IsFinite(bounds.Position.X) && float.IsFinite(bounds.Position.Y)
        && bounds.Size.X > 0 && bounds.Size.Y > 0
        && bounds.Position.X >= origin.X && bounds.Position.Y >= origin.Y
        && bounds.Position.X + bounds.Size.X <= origin.X + extent.X + .01f
        && bounds.Position.Y + bounds.Size.Y <= origin.Y + extent.Y + .01f, name);
}

var bounds = NearbyWindowLayout.Place(Vector2.Zero, new(1920,1080), new(600,100), new(400,700), new(420,570), 1);
Check(bounds.Position == new Vector2(1012,100), "new child opens next to its owner when there is space on the right");
bounds = NearbyWindowLayout.Place(Vector2.Zero, new(1920,1080), new(1450,50), new(400,700), new(420,570), 1);
Check(bounds.Position == new Vector2(1018,50), "right-edge owner opens its child to the left");
bounds = NearbyWindowLayout.Place(new(2000,100), new(1920,1080), new(2600,200), new(400,700), new(420,570), 1);
Check(bounds.Position == new Vector2(3012,200), "secondary viewport origin is retained instead of jumping to desktop origin");
bounds = NearbyWindowLayout.Place(Vector2.Zero, new(800,600), new(200,100), new(500,450), new(600,500), 1);
Fits(bounds, Vector2.Zero, new(800,600), "crowded screen keeps a nearby overlapping child fully reachable");
Check(bounds.Position.X > 0 && bounds.Position.Y > 0, "crowded screen does not default to the top-left origin");
bounds = NearbyWindowLayout.Place(Vector2.Zero, new(800,600), new(200,100), new(500,450), new(1600,1200), 1);
Fits(bounds, Vector2.Zero, new(800,600), "oversized child is bounded to the viewport work area");
bounds = NearbyWindowLayout.Place(new(50,80), new(800,600), new(-5000,-5000), new(500,450), new(400,300), 1);
Fits(bounds, new(50,80), new(800,600), "stale off-screen owner cannot send a child outside the viewport");
bounds = NearbyWindowLayout.Place(Vector2.Zero, new(1920,1080), new(600,100), new(400,700), new(420,570), 2);
Check(bounds.Position == new Vector2(1024,100) && bounds.Size == new Vector2(420,570), "DPI scales gap once and does not rescale screen-pixel child sizes");
bounds = NearbyWindowLayout.Place(new(300,400), new(2,3), new(300,400), new(100,100), new(800,900), 2);
Fits(bounds, new(300,400), new(2,3), "tiny work areas still produce finite reachable sizes");
bounds = NearbyWindowLayout.Place(Vector2.Zero, new(800,600), new(float.NaN,float.PositiveInfinity), new(-1,float.NaN), new(float.NaN,-2), float.NaN);
Fits(bounds, Vector2.Zero, new(800,600), "malformed remembered geometry is normalized without propagating NaN");
NearbyWindowIntegrationCases.Run(Check);
PortraitWindowPlacementCases.Run(Check);
PortraitWorkspaceGeometryCases.Run(Check);
PortraitWorkspaceToolbarCases.Run(Check);
PortraitEquipmentUiCases.Run(Check);
PortraitNativeImageInteractionCases.Run(Check);
#else
Check(false, "child windows need bounded owner-relative placement");
#endif

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
