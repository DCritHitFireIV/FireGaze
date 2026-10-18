using System.Numerics;
using FireGaze.PortraitPreview;

var passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    passed++;
    Console.WriteLine("PASS " + name);
}

Vector2 Preferred(Vector2 desired, Vector2 work, float scale)
{
    return PortraitPopupLayout.PreferredSize(desired, work, scale);
}

foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
foreach (var work in new[] { new Vector2(3840, 2160), new Vector2(1920, 1080), new Vector2(1280, 720), new Vector2(800, 600) })
{
    var size = Preferred(new Vector2(570, 620) * scale, work, scale);
    Check(size.X <= work.X && size.Y <= work.Y,
        $"dye popup stays within the {work.X}x{work.Y} work area at font scale {scale}");
    Check(size.X > 0 && size.Y > 0 && size.X <= 570 * scale && size.Y <= 620 * scale,
        "viewport limits never enlarge the requested color palette");
}

Check(Preferred(new(570, 620), new(1920, 1080), 1) == new Vector2(570, 620),
    "a palette that already fits retains its existing default size");
var twoX = Preferred(new Vector2(570, 620) * 2, new(1920, 1080), 2);
Check(twoX.Y < 1080 && twoX.X == 1140,
    "two-times font uses the available height without shrinking a fitting palette width");
var shrunk = PortraitPopupLayout.MaximumSize(new(800, 600), 2);
Check(shrunk.X < 800 && shrunk.Y < 600 && shrunk.X > 0 && shrunk.Y > 0,
    "live constraints follow a work-area reduction while leaving a reachable outer margin");
Check(Preferred(new(540, 0), new(800, 600), 1).Y == 0,
    "source popup keeps native auto height while its separate constraints bound it");
Check(Preferred(new(0, 570), new(800, 600), 1).X == 0,
    "an automatic dimension is not converted into a one-pixel fixed dimension");
var history = Preferred(new Vector2(460, 570) * 2, new(1920, 1080), 2);
Check(history.X == 920 && history.Y < 1080,
    "independent history first-open size also fits a short work area");
foreach (var work in new[] { Vector2.Zero, new Vector2(32, 16), new Vector2(float.NaN, float.PositiveInfinity) })
{
    var maximum = PortraitPopupLayout.MaximumSize(work, 2);
    Check(float.IsFinite(maximum.X) && float.IsFinite(maximum.Y) && maximum.X > 0 && maximum.Y > 0,
        "zero or temporarily unavailable viewport values never reach native sizing as NaN or infinity");
}

foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
foreach (var work in new[] { new Vector2(1920, 1080), new Vector2(1366, 768), new Vector2(800, 600) })
{
    var (preferred, minimum, maximum) = MainWindowBounds(work, scale);
    var physicalMinimum = minimum * scale;
    var physicalMaximum = maximum * scale;
    Check(physicalMinimum.X < work.X && physicalMinimum.Y < work.Y,
        $"main editor minimum fits {work.X}x{work.Y} after the host applies {scale}x global scale");
    Check(physicalMaximum.X < work.X && physicalMaximum.Y < work.Y &&
        minimum.X <= maximum.X && minimum.Y <= maximum.Y,
        "host-scaled maximum leaves an outer margin and never falls below the minimum");
    Check(preferred.X <= maximum.X && preferred.Y <= maximum.Y &&
        preferred.X >= minimum.X && preferred.Y >= minimum.Y,
        "main editor preferred size stays between the logical bounds without applying scale twice");
}
var normalBounds = MainWindowBounds(new(1920, 1080), 1);
Check(normalBounds.Preferred == new Vector2(920, 760) && normalBounds.Minimum == new Vector2(700, 540),
    "a fitting work area preserves the familiar main editor defaults");
var wideBounds = MainWindowBounds(new(3840, 2160), 2);
Check(wideBounds.Preferred == new Vector2(920, 760) && wideBounds.Minimum == new Vector2(700, 540),
    "two-times scale on a large display retains logical defaults for host scaling");
Console.WriteLine($"PortraitPopupLayoutTests: {passed} passed, 0 failed");

static (Vector2 Preferred, Vector2 Minimum, Vector2 Maximum) MainWindowBounds(Vector2 work, float scale)
{
    var bounds = PortraitWindowLayout.ForViewport(work, scale);
    return (bounds.Preferred, bounds.Minimum, bounds.Maximum);
}
