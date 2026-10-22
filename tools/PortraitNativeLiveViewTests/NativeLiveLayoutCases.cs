using System.Numerics;
using FireGaze.PortraitPreview;

internal static class NativeLiveLayoutCases
{
    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("portrait image fits and stays centered at wide narrow and scaled sizes", () =>
        {
            foreach (var (area, expectedSize, expectedOffset) in new[]
            {
                (new Vector2(400, 600), new Vector2(400, 500), new Vector2(0, 50)),
                (new Vector2(700, 250), new Vector2(200, 250), new Vector2(250, 0)),
                (new Vector2(800, 1200), new Vector2(800, 1000), new Vector2(0, 100)),
            })
            {
                var fitted = PortraitNativeLiveLayout.Fit(area, 400, 500);
                check(fitted.Size == expectedSize && fitted.Offset == expectedOffset,
                    "native image preserves its 4:5 aspect without cropping or stretching");
            }
            foreach (var area in new[] { Vector2.Zero, new Vector2(-1, 200), new Vector2(float.NaN, 300) })
                check(PortraitNativeLiveLayout.Fit(area, 400, 500).Size == Vector2.Zero,
                    "invalid content bounds cannot submit a malformed image");
            check(PortraitNativeLiveLayout.Fit(new(400, 600), 0, 500).Size == Vector2.Zero,
                "missing native dimensions wait for a valid image");
        });
    }
}
