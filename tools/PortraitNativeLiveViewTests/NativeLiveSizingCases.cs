using System.Numerics;
using FireGaze.PortraitPreview;

internal static class NativeLiveSizingCases
{
    internal static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("manual portrait shrink and grow stay centered with native aspect", () =>
        {
            var maximum = PortraitNativeLiveLayout.Fit(new(400, 600), 400, 500);
            var fraction = Resize(1, new(-100, -125), maximum.Size, 80);
            check(Math.Abs(fraction - .5f) < .0001f, "dragging the centered bottom-right corner left/up shrinks the image");
            var smaller = Fit(new(400, 600), 400, 500, fraction);
            check(smaller.Size == new Vector2(200, 250) && smaller.Offset == new Vector2(100, 175),
                "half-sized image remains centered and preserves 4:5 aspect");
            var larger = Resize(fraction, new(100, 125), maximum.Size, 80);
            check(Math.Abs(larger - 1) < .0001f, "reverse corner drag restores the maximum fitted size");
        });
        test("portrait resize clamps to content and keeps the drag handle reachable", () =>
        {
            check(Resize(.5f, new(10000, 10000), new(400, 500), 80) == 1,
                "oversized drag never grows beyond available image region");
            check(Math.Abs(Resize(1, new(-10000, -10000), new(400, 500), 80) - .2f) < .0001f,
                "shrinking leaves an 80px short edge rather than an unreachable zero-sized image");
            check(Resize(1, new(-10000, -10000), new(40, 50), 80) == 1,
                "very small cells use their full valid fit rather than overflowing a minimum size");
        });
        test("manual portrait ratio adapts when window size or DPI changes", () =>
        {
            foreach (var (area, expected) in new[]
            {
                (new Vector2(400, 600), new Vector2(200, 250)),
                (new Vector2(800, 1200), new Vector2(400, 500)),
                (new Vector2(700, 250), new Vector2(100, 125)),
            })
            {
                var image = Fit(area, 400, 500, .5f);
                check(image.Size == expected && image.Offset.X >= 0 && image.Offset.Y >= 0
                    && image.Offset.X + image.Size.X <= area.X && image.Offset.Y + image.Size.Y <= area.Y,
                    "saved relative size follows resized content without cropping, stretching or crossing bounds");
            }
        });
    }

    private static PortraitNativeLiveImageRect Fit(Vector2 area, int width, int height, float fraction)
        => PortraitNativeLiveLayout.Fit(area, width, height, fraction);
    private static float Resize(float fraction, Vector2 delta, Vector2 maximum, float edge)
        => PortraitNativeLiveLayout.ResizeFraction(fraction, delta, maximum, edge);
}
