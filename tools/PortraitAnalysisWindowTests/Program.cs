using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using FireGaze;
using FireGaze.PortraitPreview;
using FireGaze.UI;

var passed = 0; var failed = 0;
void Check(bool pass, string message) { Console.WriteLine((pass ? "PASS " : "FAIL ") + message); if (pass) passed++; else failed++; }
object? Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
void Invoke(object instance, string name, params object[] values) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, values);
async Task Drain(PortraitPreviewWindow window)
{
    var request = (PortraitAnalysisRequest)Field(window, "analysis")!;
    var until = DateTime.UtcNow.AddSeconds(5);
    while (request.IsBusy) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(1); }
}
PortraitCaptureSample Sample(PortraitImageScope scope, string key = "state-1") => new(new(11, 22), new(101, 102, 103, 4, 22), key,
    scope, new(7, Vector2.Zero, new(1920, 1080)), scope == PortraitImageScope.VisibleAdventurerPlate
        ? new(201, 2, 203, 204, 11, 22, 5, new(10, 20, 320, 180), "plate-a") : null);
static Vector4 Color(uint value) => new((value & 255) / 255f, ((value >> 8) & 255) / 255f,
    ((value >> 16) & 255) / 255f, (value >> 24) / 255f);
static double Contrast(Vector4 left, Vector4 right)
{
    static double Luminance(Vector4 color)
    {
        static double Channel(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        return .2126 * Channel(color.X) + .7152 * Channel(color.Y) + .0722 * Channel(color.Z);
    }
    var a = Luminance(left); var b = Luminance(right);
    return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
}

{
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait), LiveTexture = new(20, 32, 5) };
    using var window = new PortraitPreviewWindow(model);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    using var paletteScope = FireGazePalette.Enter(FireGazeColorDomain.Portrait);
    ImGui.FilledRectangles.Clear();
    Invoke(window, "DrawAnalysisWindow");
    var rectangles = ImGui.FilledRectangles.ToArray();
    var backgrounds = rectangles.Select((rectangle, index) => (rectangle, index))
        .Where(entry => entry.rectangle.Maximum.X - entry.rectangle.Minimum.X > 120).ToArray();
    Check(backgrounds.Length == 4, "actual analysis draw submits luma and three RGB histogram surfaces");
    Check(backgrounds.Length == 4 && backgrounds.All(entry =>
    {
        var color = Color(entry.rectangle.Color);
        return color.X > .9f && color.Y > .9f && color.Z > .9f && color.W == 1
            && MathF.Max(color.X, MathF.Max(color.Y, color.Z)) < .99f;
    }), "actual portrait histograms submit the softened near-white surface used by portrait floating tools");
    for (var channel = 0; channel < 3 && backgrounds.Length == 4; channel++)
    {
        var background = backgrounds[channel + 1];
        var end = channel + 2 < backgrounds.Length ? backgrounds[channel + 2].index : rectangles.Length;
        var bars = rectangles.Skip(background.index + 1).Take(end - background.index - 1)
            .Where(rectangle => rectangle.Maximum.Y > rectangle.Minimum.Y).ToArray();
        var label = new[] { "red", "green", "blue" }[channel];
        Check(bars.Length > 0 && bars.All(bar => Contrast(Color(bar.Color), Color(background.rectangle.Color)) >= 3),
            $"actual {label} histogram data remains visible at 3:1 against its submitted light surface");
        Check(bars.Length > 0 && bars.All(bar =>
        {
            var color = Color(bar.Color);
            return channel switch { 0 => color.X > color.Y && color.X > color.Z,
                1 => color.Y > color.X && color.Y > color.Z, _ => color.Z > color.X && color.Z > color.Y };
        }), $"actual {label} histogram retains its RGB channel identity");
    }
}

// Returning a cached portrait is the original bug: this must perform no readback in plate scope.
{
    var model = new Model { LiveCaptureScope = PortraitImageScope.VisibleAdventurerPlate };
    using var window = new PortraitPreviewWindow(model, new() { Photo = new(10, 16, 1) });
    var calls = Plugin.TextureReadback.Calls;
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    Check(Plugin.TextureReadback.Calls == calls, "a missing visible plate never falls back to a history portrait");
    Check(((PortraitAnalysisRequest)Field(window, "analysis")!).CurrentResult is null, "plate unavailability never publishes portrait histograms");
}
{
    var source = new Texture(10, 16, 2);
    var model = new Model { LiveCaptureScope = PortraitImageScope.VisibleAdventurerPlate, Sample = Sample(PortraitImageScope.Portrait), LiveTexture = source };
    using var window = new PortraitPreviewWindow(model, new() { Photo = new(10, 16, 1) });
    var calls = Plugin.TextureReadback.Calls;
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    Check(Plugin.TextureReadback.Calls == calls, "a late live portrait is rejected after the requested scope becomes plate");
    Check(source.Shares.Count == 1 && source.Shares[0].Disposes == 1, "rejecting the wrong live scope releases its acquired texture");
}
{
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait), LiveTexture = new(20, 32, 5) };
    using var window = new PortraitPreviewWindow(model);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    var old = (Texture)Field(window, "analysisPreview")!;
    ImGui.Click = "可见铭牌";
    Invoke(window, "DrawAnalysisWindow");
    var request = (PortraitAnalysisRequest)Field(window, "analysis")!;
    Check(request.CurrentResult is null && request.CurrentKey is null, "selecting a new scope removes the previous result in the same frame");
    Check(old.Disposes == 1 && Field(window, "analysisPreview") is null, "selecting a new scope releases the previous preview in the same frame");
}
{
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait), LiveTexture = new(20, 32, 5) };
    using var window = new PortraitPreviewWindow(model);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    var request = (PortraitAnalysisRequest)Field(window, "analysis")!;
    var firstKey = request.CurrentKey;
    model.Sample = model.Sample! with { Editor = model.Sample.Editor with { Generation = 9 } };
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    Check(request.CurrentKey != firstKey, "same state text from a new editor allocation has a distinct analysis source key");
    var secondKey = request.CurrentKey;
    model.LiveCaptureScope = PortraitImageScope.VisibleAdventurerPlate;
    model.Sample = Sample(PortraitImageScope.VisibleAdventurerPlate);
    model.LiveTexture = new(32, 18, 6);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    Check(request.CurrentKey != secondKey, "same state text from another image scope has a distinct analysis source key");
    var thirdKey = request.CurrentKey;
    model.Sample = model.Sample with { Plate = model.Sample.Plate! with { VisualKey = "plate-b" } };
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    Check(request.CurrentKey != thirdKey, "changing the visible plate content invalidates a same-state analysis cache hit");
}
{
    var model = new Model { LiveCaptureScope = PortraitImageScope.VisibleAdventurerPlate,
        Sample = Sample(PortraitImageScope.VisibleAdventurerPlate), LiveTexture = new(32, 18, 6) };
    using var window = new PortraitPreviewWindow(model, new() { Photo = new(10, 16, 1) });
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    // The popup's first frame may synchronize its history cursor before the second click.
    Invoke(window, "DrawAnalysisWindow");
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    ImGui.Click = "设为参考";
    Invoke(window, "DrawAnalysisWindow");
    var reference = Field(window, "comparisonReference")!;
    var texture = (Texture?)Field(reference, "current");
    Check(texture?.Handle == 6, "comparison reference comes from the actual analyzed plate rather than the history portrait");
    Check(Math.Abs(ImGui.LastDrawImageSize.X / ImGui.LastDrawImageSize.Y - 32f / 18) < .00001f,
        "attention preview preserves the full plate aspect ratio");
}
var spaceImage = typeof(PortraitPreviewWindow).GetMethod("DrawLightingSpaceImage", BindingFlags.Instance | BindingFlags.NonPublic);
if (spaceImage is null) Check(false, "space window can display a retained portrait image");
else
{
    var texture = new Texture(800, 600, 99);
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait), LiveTexture = texture };
    using var window = new PortraitPreviewWindow(model);
    ImGui.LastImage = 0;
    spaceImage.Invoke(window, [new Vector2(500, 300)]);
    Check(ImGui.LastImage == 99 && ImGui.LastDrawImageSize == new Vector2(400, 300),
        "space result pane draws the real portrait with letterboxing");
    Check(texture.Shares.Count == 1 && texture.Shares[0].Disposes == 1 && texture.Disposes == 0,
        "space result pane releases its per-frame lease while preserving the service owner");
    model.Sample = Sample(PortraitImageScope.VisibleAdventurerPlate);
    ImGui.LastImage = 0;
    spaceImage.Invoke(window, [new Vector2(500, 300)]);
    Check(ImGui.LastImage == 0 && texture.Shares[^1].Disposes == 1,
        "space result pane rejects and releases a late plate image");
    model.LiveTexture = new Texture(0, 10, 100);
    model.Sample = Sample(PortraitImageScope.Portrait);
    spaceImage.Invoke(window, [new Vector2(500, 300)]);
    Check(ImGui.LastImage == 0 && model.LiveTexture.Shares[^1].Disposes == 1,
        "space result pane does not draw an invalid texture and still releases it");
    model.Sample = null;
    spaceImage.Invoke(window, [new Vector2(500, 300)]);
    Check(ImGui.LastImage == 0, "space result pane tolerates a closing or refreshing source");
}
Console.WriteLine($"PortraitAnalysisWindowTests: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
