using System.Numerics;
using FireGaze.PortraitPreview;

internal static class NativeLiveSizingCases
{
    internal static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("portrait zoom exceeds automatic fit while preserving native aspect", () =>
        {
            var enlarged = PortraitNativeLiveLayout.Fit(new(400, 600), 400, 500, 2);
            check(enlarged.Size == new Vector2(800, 1000) && enlarged.Offset == new Vector2(-200, -200),
                "200 percent zoom makes the portrait larger than its clipped view instead of clamping to fit");
        });
        test("manual portrait shrink and grow stay centered with native aspect", () =>
        {
            var maximum = PortraitNativeLiveLayout.Fit(new(400, 600), 400, 500);
            var fraction = Resize(1, new(-100, -125), maximum.Size);
            check(Math.Abs(fraction - .5f) < .0001f, "dragging the centered bottom-right corner left/up shrinks the image");
            var smaller = Fit(new(400, 600), 400, 500, fraction);
            check(smaller.Size == new Vector2(200, 250) && smaller.Offset == new Vector2(100, 175),
                "half-sized image remains centered and preserves 4:5 aspect");
            var larger = Resize(fraction, new(100, 125), maximum.Size);
            check(Math.Abs(larger - 1) < .0001f, "reverse corner drag restores the maximum fitted size");
        });
        test("portrait drag zoom clamps to a useful quarter to triple scale", () =>
        {
            check(Resize(.5f, new(10000, 10000), new(400, 500)) == 3,
                "long corner drag can enlarge to triple fitted scale");
            check(Resize(1, new(-10000, -10000), new(400, 500)) == .25f,
                "shrinking stops at a quarter of fitted size");
            check(Resize(1, new(-10000, -10000), new(40, 50)) == .25f,
                "a small cell keeps the same player-controlled zoom range");
        });
        test("wheel zoom anchors the pointed image location and reaches both limits", () =>
        {
            var state = PortraitNativeLiveLayout.ZoomAt(new(400, 500), 400, 500, 1, Vector2.Zero, new(300, 350), 1);
            var rect = PortraitNativeLiveLayout.Fit(new(400, 500), 400, 500, state.Fraction, state.Pan);
            check(Math.Abs(state.Fraction - 1.15f) < .0001f
                && Vector2.Distance(rect.Offset, new(-45, -52.5f)) < .001f,
                "zoom keeps the same image feature under the pointer instead of jumping to the center");
            check(PortraitNativeLiveLayout.ZoomAt(new(400, 500), 400, 500, 1, Vector2.Zero, new(200, 250), 100).Fraction == 3
                && PortraitNativeLiveLayout.ZoomAt(new(400, 500), 400, 500, 1, Vector2.Zero, new(200, 250), -100).Fraction == .25f,
                "fast wheel movement never exceeds the supported zoom range");
        });
        test("pan stays within image edges and exposes every enlarged corner", () =>
        {
            var area = new Vector2(400, 500); var size = new Vector2(800, 1000);
            var pan = PortraitNativeLiveLayout.PanBy(area, size, Vector2.Zero, new(100, -100));
            check(Vector2.Distance(pan, new(.5f, -.4f)) < .0001f,
                "dragging translates the picture by the requested number of pixels");
            foreach (var (delta, offset) in new[]
            {
                (new Vector2(10000), Vector2.Zero),
                (new Vector2(-10000), new Vector2(-400, -500)),
            })
            {
                pan = PortraitNativeLiveLayout.PanBy(area, size, Vector2.Zero, delta);
                var rect = PortraitNativeLiveLayout.Fit(area, 400, 500, 2, pan);
                check(rect.Offset == offset, "panning stops at the enlarged image edge without exposing empty space");
            }
            check(PortraitNativeLiveLayout.PanBy(area, new(200, 250), new(1), new(50)) == Vector2.Zero,
                "a smaller-than-cell picture remains centered instead of wandering outside its cell");
        });
        test("invalid image input cannot introduce nonfinite zoom or pan", () =>
        {
            check(Resize(float.NaN, new(float.PositiveInfinity), new(400, 500)) == 1,
                "invalid drag input retains a valid default zoom");
            var state = PortraitNativeLiveLayout.ZoomAt(Vector2.Zero, 0, 0, float.NaN, new(float.NaN), new(float.NaN), float.NaN);
            check(state.Fraction == 1 && state.Pan == Vector2.Zero,
                "missing bounds and dimensions never create an invalid image transform");
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
        test("enlarged photo keeps player zoom and view position across proportional viewport changes", () =>
        {
            var first = PortraitNativeLiveLayout.Fit(new(400, 500), 400, 500, 2, new(.5f, -.4f));
            var scaled = PortraitNativeLiveLayout.Fit(new(800, 1000), 400, 500, 2, new(.5f, -.4f));
            check(first.Size == new Vector2(800, 1000) && first.Offset == new Vector2(-100, -350)
                && scaled.Size == new Vector2(1600, 2000) && scaled.Offset == new Vector2(-200, -700),
                "window or DPI changes scale the existing transform instead of resetting zoom or pan");
        });
    }

    private static PortraitNativeLiveImageRect Fit(Vector2 area, int width, int height, float fraction)
        => PortraitNativeLiveLayout.Fit(area, width, height, fraction);
    private static float Resize(float fraction, Vector2 delta, Vector2 maximum)
        => PortraitNativeLiveLayout.ResizeFraction(fraction, delta, maximum);
}
