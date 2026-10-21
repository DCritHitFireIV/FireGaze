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
    using var window = new PortraitPreviewWindow(new Model());
    ImGui.Click = "灯光拟合";
    Invoke(window, "DrawAnalysisWindow");
    Check(window.LightingFitOwner == "FireGazePortraitAnalysis",
        "analysis curve button passes the actual analysis window as the opener");
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

{
    var source = new Texture(20, 32, 5);
    var model = new Model { SourceCensusBlocksOperations = true,
        Sample = Sample(PortraitImageScope.Portrait), LiveTexture = source };
    using var window = new PortraitPreviewWindow(model);
    var calls = Plugin.TextureReadback.Calls;
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    Check(source.Shares.Count == 0 && Plugin.TextureReadback.Calls == calls,
        "source census physical lifetime blocks analysis before texture sharing or readback");
    Check(((PortraitAnalysisRequest)Field(window, "analysis")!).CurrentResult is null,
        "blocked source census analysis does not publish a result");
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
    var texture = (Texture?)Field(window, "comparisonOwned");
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
{
    var source = new Texture(2, 1, 200);
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait), LiveTexture = source };
    var window = new PortraitPreviewWindow(model);
    var provider = Plugin.TextureReadback;
    provider.Hold = true; provider.Pending = null;
    Invoke(window, "StartCurrentAnalysis");
    var until = DateTime.UtcNow.AddSeconds(5);
    while (provider.Pending is null) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(1); }
    var retained = source.Shares[0].Shares[0];
    window.Dispose();
    var physical = window.GetType().GetMethod("DrainPhysicalAsync")?.Invoke(window, null) as Task ?? Task.CompletedTask;
    await Task.Delay(20);
    Check(!physical.IsCompleted && retained.Disposes == 0,
        "closing analysis holds host physical retirement and its source until actual readback completion");
    Check(!provider.LastCancellable, "actual analysis readback cannot logically cancel while provider still uses its source");
    provider.Pending.TrySetResult(new(new(28, 2, 1, 8), [100,100,100,255,100,100,100,255]));
    await Drain(window); await physical.WaitAsync(TimeSpan.FromSeconds(5));
    Check(retained.Disposes == 1, "analysis physical completion releases its transferred source exactly once");
    provider.Hold = false; provider.Pending = null;
}
{
    var source = new Texture(2, 1, 201);
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait), LiveTexture = source };
    var window = new PortraitPreviewWindow(model);
    Task? prefixDrain = null; var held = false;
    source.OnShare = () => { window.Dispose(); prefixDrain = window.DrainPhysicalAsync(); held = !prefixDrain.IsCompleted; };
    var calls = Plugin.TextureReadback.Calls;
    Invoke(window, "StartCurrentAnalysis");
    await prefixDrain!.WaitAsync(TimeSpan.FromSeconds(5));
    Check(held && source.Shares.Single().Disposes == 1 && Plugin.TextureReadback.Calls == calls,
        "analysis owns the live-acquisition synchronous prefix before reentrant close and releases late output without readback");
}
{
    var source = new Texture(2, 1, 202);
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait), LiveTexture = source };
    var window = new PortraitPreviewWindow(model);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    var preview = (Texture)Field(window, "analysisPreview")!;
    preview.OnDispose = () => throw new IOException("unconfirmed preview release");
    window.Dispose();
    Check(!window.DrainPhysicalAsync().IsCompleted && preview.Disposes == 1,
        "analysis preview release failure quarantines its provider resource rather than retiring host scope");
}
{
    var source = new Texture(2, 1, 203);
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait), LiveTexture = source };
    var window = new PortraitPreviewWindow(model);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    Invoke(window, "DrawAnalysisWindow");
    ImGui.Click = "设为参考"; Invoke(window, "DrawAnalysisWindow");
    var reference = Field(window, "comparisonReference")!;
    var resource = (Texture)Field(window, "comparisonOwned")!;
    resource.OnDispose = () => throw new IOException("unconfirmed comparison release");
    var escaped = false;
    try { window.Dispose(); } catch (IOException) { escaped = true; }
    Check(!escaped && !window.DrainPhysicalAsync().IsCompleted && resource.Disposes == 1,
        "analysis comparison release failure stays quarantined and cannot bypass host physical retirement");
}
{
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait, "A"), LiveTexture = new(20, 32, 501) };
    using var window = new PortraitPreviewWindow(model);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    ImGui.Click = "设为参考"; Invoke(window, "DrawAnalysisWindow");
    var referenceA = (Texture)Field(window, "comparisonOwned")!;
    model.Sample = model.Sample! with { StateKey = "B", Viewport = new(8, new(20, 30), new(1280, 720)) };
    model.LiveTexture = new(32, 18, 502);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    Check(ReferenceEquals(Field(window, "comparisonOwned"), referenceA) && referenceA.Disposes == 0,
        "I05 same-session parameter edit and resized B preserve the independent A reference");
    ImGui.DrawnImages.Clear(); ImGui.Texts.Clear(); Invoke(window, "DrawAnalysisWindow");
    Check(ImGui.DrawnImages.Any(image => image.Handle == 501) && ImGui.DrawnImages.Any(image => image.Handle == 502),
        "I05 actual comparison draws retained A beside newly analyzed B");
    Check(ImGui.DrawnImages.Any(image => image.Handle == 501) && ImGui.DrawnImages.Any(image => image.Handle == 502)
        && ImGui.DrawnImages.Where(image => image.Handle == 501).All(image => Math.Abs(image.Size.X / image.Size.Y - 20f / 32) < .00001f)
        && ImGui.DrawnImages.Where(image => image.Handle == 502).All(image => Math.Abs(image.Size.X / image.Size.Y - 32f / 18) < .00001f),
        "I05 different A and B proportions remain individually letterboxed");
    Check(ImGui.Texts.Any(text => text.Contains("参考 · 实时肖像") && text.Contains("读取于"))
        && ImGui.Texts.Any(text => text.Contains("本次分析 · 实时肖像") && text.Contains("读取于")),
        "I05 each side exposes user-facing provenance and honest read time");
}
{
    var model = new Model { LiveCaptureScope = PortraitImageScope.VisibleAdventurerPlate,
        Sample = Sample(PortraitImageScope.VisibleAdventurerPlate), LiveTexture = new(32, 18, 601) };
    using var window = new PortraitPreviewWindow(model);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    ImGui.Click = "设为参考"; Invoke(window, "DrawAnalysisWindow");
    var reference = (Texture)Field(window, "comparisonOwned")!;
    var provenance = Field(window, "comparisonProvenance");
    model.Sample = model.Sample! with { StateKey = "B", Viewport = new(9, new(70, 80), new(800, 600)),
        Plate = model.Sample.Plate! with { Addon = 301, Generation = 6, Rect = new(40, 60, 400, 200), VisualKey = "plate-b" } };
    model.LiveTexture = new(40, 20, 602);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    Invoke(window, "DrawAnalysisWindow");
    Check(ReferenceEquals(Field(window, "comparisonOwned"), reference) && reference.Disposes == 0,
        "I05 plate sample, surface, viewport and size changes preserve same-subject static A");
    Check(ReferenceEquals(Field(window, "comparisonProvenance"), provenance)
        && !ReferenceEquals(Field(window, "analysisProvenance"), provenance),
        "I05 reference provenance stays fixed while B owns independent metadata");
}
var changes = new (string Name, Func<PortraitAnalysisSession, PortraitAnalysisSession?> Change)[]
{
    ("account", s => s with { Owner = s.Owner with { AccountID = 99 } }),
    ("character owner", s => s with { Owner = s.Owner with { ContentID = 99 }, Entry = s.Entry with { ContentID = 99 } }),
    ("editor", s => s with { Editor = s.Editor with { Editor = 999 } }),
    ("state allocation", s => s with { Editor = s.Editor with { State = 999 } }),
    ("model allocation", s => s with { Editor = s.Editor with { Model = 999 } }),
    ("allocation generation", s => s with { Editor = s.Editor with { Generation = 99 } }),
    ("editor character", s => s with { Editor = s.Editor with { Character = 99 } }),
    ("entry point", s => s with { Entry = s.Entry with { EntryPoint = PortraitHistoryEntryPoint.AdventurerPlate } }),
    ("gearset", s => s with { Entry = s.Entry with { SourceID = 9 } }),
    ("compatibility", s => s with { Entry = s.Entry with { CompatToken = "other" } }),
    ("missing credential", _ => null),
    ("invalid generation", s => s with { Editor = s.Editor with { Generation = 0 } }),
};
foreach (var change in changes)
{
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait), LiveTexture = new(20, 32, 701) };
    using var window = new PortraitPreviewWindow(model);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    ImGui.Click = "设为参考"; Invoke(window, "DrawAnalysisWindow");
    var reference = (Texture)Field(window, "comparisonOwned")!;
    var preview = (Texture)Field(window, "analysisPreview")!;
    model.CurrentAnalysisSession = change.Change(model.CurrentAnalysisSession!);
    var shares = model.LiveTexture.Shares.Count;
    ImGui.DrawnImages.Clear(); Invoke(window, "DrawAnalysisWindow");
    Check(reference.Disposes == 1 && preview.Disposes == 1 && Field(window, "comparisonOwned") is null
        && ((PortraitAnalysisRequest)Field(window, "analysis")!).CurrentResult is null
        && !ImGui.DrawnImages.Any(image => image.Handle == 701),
        $"I05 {change.Name} change clears both sides before draw without another analysis click");
    Check(model.LiveTexture.Shares.Count == shares,
        $"I05 {change.Name} draw validation uses cached managed credentials without source acquisition");
}
{
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait), LiveTexture = new(20, 32, 702) };
    using var window = new PortraitPreviewWindow(model);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    ImGui.Click = "设为参考"; Invoke(window, "DrawAnalysisWindow");
    var reference = (Texture)Field(window, "comparisonOwned")!;
    model.LiveCaptureScope = PortraitImageScope.VisibleAdventurerPlate;
    Invoke(window, "DrawAnalysisWindow");
    Check(reference.Disposes == 1 && Field(window, "comparisonOwned") is null
        && ((PortraitAnalysisRequest)Field(window, "analysis")!).CurrentResult is null,
        "I05 image scope change clears both sides before draw");
}
foreach (var mismatch in new[] { "missing credential", "live owner", "live editor", "plate owner" })
{
    var scope = mismatch == "plate owner" ? PortraitImageScope.VisibleAdventurerPlate : PortraitImageScope.Portrait;
    var model = new Model { Sample = Sample(scope), LiveCaptureScope = scope, LiveTexture = new(20, 32, 703) };
    if (mismatch == "missing credential") model.CurrentAnalysisSession = null;
    if (mismatch == "live owner") model.Sample = model.Sample! with { Owner = new(99, 22) };
    if (mismatch == "live editor") model.Sample = model.Sample! with { Editor = model.Sample!.Editor with { Generation = 99 } };
    if (mismatch == "plate owner") model.Sample = model.Sample! with { Plate = model.Sample!.Plate! with { AccountID = 99 } };
    using var window = new PortraitPreviewWindow(model);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    ImGui.Click = "设为参考"; Invoke(window, "DrawAnalysisWindow"); ImGui.Click = null;
    Check(((PortraitAnalysisRequest)Field(window, "analysis")!).CurrentResult is not null
        && Field(window, "comparisonOwned") is null,
        $"I05 {mismatch} keeps ordinary image analysis available while refusing comparison admission");
}
{
    var atA = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    var atB = atA.AddHours(2);
    var model = new Model { History = [new(1, atA, "A", true)] };
    var photos = new PortraitHistoryPhotos { Photo = new(20, 32, 801) };
    using var window = new PortraitPreviewWindow(model, photos);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    ImGui.Click = "设为参考"; Invoke(window, "DrawAnalysisWindow");
    var reference = (Texture)Field(window, "comparisonOwned")!;
    model.History = [new(2, atB, "B", true)]; photos.Photo = new(32, 18, 802);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    ImGui.Texts.Clear(); Invoke(window, "DrawAnalysisWindow");
    Check(ReferenceEquals(Field(window, "comparisonOwned"), reference) && reference.Disposes == 0,
        "I05 selecting another history record preserves same-session reference A");
    Check(Field(window, "comparisonProvenance") is PortraitAnalysisProvenance { Source: "历史记录", IsRecordTime: true } left && left.At == atA
        && Field(window, "analysisProvenance") is PortraitAnalysisProvenance { Source: "历史记录", IsRecordTime: true } right && right.At == atB
        && ImGui.Texts.Any(text => text.Contains("参考 · 历史记录 · 记录于"))
        && ImGui.Texts.Any(text => text.Contains("本次分析 · 历史记录 · 记录于"))
        && !ImGui.Texts.Any(text => text.Contains("当前照片")),
        "I05 historical A and B expose their distinct record times without pretending to be live");
}
{
    var feature = new PortraitPreviewFeature();
    Invoke(feature, "PublishAnalysisSession");
    var original = feature.CurrentAnalysisSession!;
    feature.historyOwner = new(99, 88); feature.observedIdentity = original.Editor with { Generation = 9 };
    Check(ReferenceEquals(feature.CurrentAnalysisSession, original) && original.Owner == new PortraitCaptureOwner(11, 22),
        "I05 UI getter returns one immutable published observation instead of mixing changing fields");
    feature.observedKey = original.Entry with { ContentID = 88 };
    Invoke(feature, "PublishAnalysisSession");
    Check(feature.CurrentAnalysisSession is { Owner.AccountID: 99, Owner.ContentID: 88, Editor.Generation: 9 },
        "I05 Framework publishes new owner editor and history entry together");
    feature.session.IsActive = false; Invoke(feature, "PublishAnalysisSession");
    Check(feature.CurrentAnalysisSession is null, "I05 inactive editor session revokes its comparison credential");
    feature.session.IsActive = true; feature.observedKey = original.Entry; Invoke(feature, "PublishAnalysisSession");
    Check(feature.CurrentAnalysisSession is null, "I05 mismatched cached owner and history entry never publish a credential");
    feature.observedKey = original.Entry with { ContentID = 88 }; feature.disposed = true; Invoke(feature, "PublishAnalysisSession");
    Check(feature.CurrentAnalysisSession is null, "I05 disposed feature cannot republish comparison credentials");
}
{
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait), LiveTexture = new(20, 32, 704) };
    var window = new PortraitPreviewWindow(model);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    ImGui.Click = "设为参考"; Invoke(window, "DrawAnalysisWindow");
    var reference = (Texture)Field(window, "comparisonOwned")!;
    var preview = (Texture)Field(window, "analysisPreview")!;
    window.Dispose(); window.Dispose();
    await window.DrainPhysicalAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Check(reference.Disposes == 1 && preview.Disposes == 1 && model.LiveTexture.Disposes == 0,
        "I05 repeated close releases reference and analyzed preview exactly once while leaving provider owner intact");
}
{
    var model = new Model { Sample = Sample(PortraitImageScope.Portrait), LiveTexture = new(20, 32, 705) };
    using var window = new PortraitPreviewWindow(model);
    Invoke(window, "StartCurrentAnalysis"); await Drain(window);
    var preview = (Texture)Field(window, "analysisPreview")!;
    preview.OnShare = () => { ImGui.CloseOnBegin = true; Invoke(window, "DrawAnalysisWindow"); };
    ImGui.Click = "设为参考"; Invoke(window, "DrawAnalysisWindow");
    Check(Field(window, "comparisonOwned") is null && Field(window, "comparisonProvenance") is null
        && preview.Shares.Single().Disposes == 1 && preview.Disposes == 1,
        "I05 closing actual analysis during pin sharing rejects and releases the late reference without republishing A");
}
Console.WriteLine($"PortraitAnalysisWindowTests: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
