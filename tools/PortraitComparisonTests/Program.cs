using System.Numerics;
#if HAS_COMPARISON
using FireGaze.PortraitPreview;
#endif

var passed = 0;
void Check(bool value, string name) { if (!value) throw new InvalidOperationException("FAIL " + name); passed++; }
#if !HAS_COMPARISON
Check(false, "comparison keeps its own reference without changing live portrait");
#else
var first = new Resource();
using var reference = new PortraitComparisonReference<Resource>();
reference.Set("portrait:1", first);
Check(ReferenceEquals(reference.For("portrait:1"), first), "reference is visible only within its acquisition scope");
Check(reference.For("plate:1") is null && reference.For("portrait:2") is null, "a different scope or character cannot reuse an old reference");
reference.Set("portrait:1", first);
Check(first.Disposed == 0, "setting the exact retained lease again does not dispose the displayed resource");
var second = new Resource();
reference.Set("portrait:1", second);
Check(first.Disposed == 1 && second.Disposed == 0, "replacement releases the old reference while retaining the new one");
reference.Clear(); reference.Clear();
Check(second.Disposed == 1 && reference.For("portrait:1") is null, "clear releases once and removes the visible reference");
var third = new Resource(); reference.Set("portrait:1", third); reference.Dispose(); reference.Dispose();
var late = new Resource(); reference.Set("portrait:1", late);
Check(third.Disposed == 1 && late.Disposed == 1 && reference.For("portrait:1") is null, "dispose releases current and late owned references without publishing");

Check(PortraitComparisonLayout.TryFit(new(400, 400), new(160, 256), out var rect)
    && rect.Offset == new Vector2(75, 0) && rect.Size == new Vector2(250, 400), "portrait fits without stretching and centers on the spare axis");
Check(PortraitComparisonLayout.TryFit(new(400, 200), new(400, 200), out rect)
    && rect.Offset == Vector2.Zero && rect.Size == new Vector2(400, 200), "matching aspect uses the complete canvas");
Check(PortraitComparisonLayout.TryWipe(new(200, 320), .25f, out var wipe)
    && wipe.Cut == 50 && wipe.UvCut == .25f, "wipe preserves image UV rather than rescaling the reference portion");
Check(PortraitComparisonLayout.TryWipe(new(200, 320), -1, out wipe) && wipe.Cut == 0,
    "wipe clamp keeps a pointer outside the canvas safe");
Check(PortraitComparisonLayout.TryWipe(new(200, 320), 2, out wipe) && wipe.Cut == 200,
    "wipe clamp preserves the complete reference at the far edge");
foreach (var size in new[] { Vector2.Zero, new Vector2(-1, 2), new Vector2(float.NaN, 3), new Vector2(1, float.PositiveInfinity) })
    Check(!PortraitComparisonLayout.TryFit(size, new(160, 256), out _) && !PortraitComparisonLayout.TryWipe(size, .5f, out _), "invalid canvas cannot yield nonfinite draw coordinates");
Check(!PortraitComparisonLayout.TryFit(new(100, 100), Vector2.Zero, out _)
    && !PortraitComparisonLayout.TryWipe(new(100, 100), float.NaN, out _), "invalid source or cut is rejected before drawing");
#endif
Console.WriteLine($"PortraitComparisonTests: {passed} passed, 0 failed");
#if HAS_COMPARISON
sealed class Resource : IDisposable { public int Disposed { get; private set; } public void Dispose() => Disposed++; }
#endif
