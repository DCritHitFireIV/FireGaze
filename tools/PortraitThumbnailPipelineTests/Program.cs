#if !HAS_CAPTURE_GATE
Console.Error.WriteLine("FAIL: history photos have no stable, owner-bound capture gate");
return 1;
#else
using FireGaze.PortraitPreview;
var checks = 0;
void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); checks++; }
var now = DateTimeOffset.Parse("2026-10-18T13:50:00Z");
var gate = new PortraitThumbnailCaptureGate(() => now, TimeSpan.FromMilliseconds(200));
Check(!gate.Observe("portrait-A", 1, "state-A", true), "a fresh record waits for its rendered model to settle");
now = now.AddMilliseconds(199);
Check(!gate.Observe("portrait-A", 1, "state-A", true), "the stable delay cannot be bypassed by repeated frames");
now = now.AddMilliseconds(1);
Check(gate.Observe("portrait-A", 1, "state-A", true), "one settled state becomes eligible for capture");
var ticket = gate.Begin();
Check(ticket is not null && !gate.Observe("portrait-A", 1, "state-A", true), "only one copy can be in flight for a record");
Check(gate.CanAccept(ticket!), "an unchanged capture ticket can publish its pixels");
gate.Complete(ticket!, true);
now = now.AddSeconds(1);
Check(!gate.Observe("portrait-A", 1, "state-A", true), "a saved thumbnail is not regenerated every frame");
Check(!gate.Observe("portrait-A", 2, "state-B", true), "a newly committed edit starts a fresh settling period");
now = now.AddMilliseconds(200);
Check(gate.Observe("portrait-A", 2, "state-B", true), "the next completed edit can be captured");
var stale = gate.Begin()!;
Check(!gate.Observe("portrait-A", 2, "state-C", false) && !gate.CanAccept(stale), "changing the active editor before copy completion invalidates the ticket");
gate.Complete(stale, true);
Check(!gate.Observe("portrait-A", 3, "state-C", true), "a stale completion cannot claim a later record was captured");
now = now.AddMilliseconds(200);
Check(gate.Observe("portrait-A", 3, "state-C", true), "later valid records survive stale callbacks");
var departed = gate.Begin()!;
gate.Reset();
Check(!gate.CanAccept(departed), "closing the editor rejects its asynchronous completion");
gate.Complete(departed, true);
Check(!gate.Observe("portrait-B", 3, "state-C", true), "an owner change cannot reuse an old settling window even for the same ID and state");
now = now.AddMilliseconds(200);
Check(gate.Observe("portrait-B", 3, "state-C", true), "a new owner can settle independently");
var failed = gate.Begin()!;
gate.Complete(failed, false);
Check(!gate.Observe("portrait-B", 3, "state-C", true), "failed capture retries are throttled");
now = now.AddMilliseconds(200);
Check(gate.Observe("portrait-B", 3, "state-C", true), "a transient copy failure permits a later retry");
var resumed = gate.Begin()!;
Check(!gate.Observe("portrait-B", 3, "state-C", false), "holding input makes a committed record ineligible");
Check(!gate.CanAccept(resumed), "input activity invalidates a pending capture even if the state has not changed yet");
gate.Complete(resumed, false);
now = now.AddMilliseconds(500);
Check(!gate.Observe("portrait-B", 3, "state-C", true), "the first ready sample after input release starts a new settling window");
now = now.AddMilliseconds(200);
Check(gate.Observe("portrait-B", 3, "state-C", true), "releasing input eventually permits capture");
#if HAS_IMAGE_GUARD
var png = new byte[33];
new byte[] {137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82,0,0,0,160,0,0,1,0,8,6}.CopyTo(png,0);
Check(PortraitThumbnailImage.IsSafeSize(png), "our small RGBA portrait passes the decoder size gate");
png[20]=1;
Check(!PortraitThumbnailImage.IsSafeSize(png), "a compressed image claiming huge height is refused before GPU decoding");
png[20]=0;png[16]=1;
Check(!PortraitThumbnailImage.IsSafeSize(png), "a compressed image claiming huge width is refused before GPU decoding");
png[16]=0;png[0]=0;
Check(!PortraitThumbnailImage.IsSafeSize(png) && !PortraitThumbnailImage.IsSafeSize([]), "unknown and truncated containers are refused");

var visibilityMethod = typeof(PortraitThumbnailImage).GetMethod("HasVisiblePixels",
    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
Check(visibilityMethod is not null, "raw thumbnail pixels have a visibility guard before an empty render can be cached");
var visible = visibilityMethod!.CreateDelegate<PixelVisibilityGuard>();
const int width = 160, height = 256, tightPitch = width * 4;
var pixels = new byte[tightPitch * height];
foreach (var format in new[] { 28, 29, 87, 91 })
{
    Check(!visible(pixels, width, height, tightPitch, format), $"an entirely transparent {format} render is not a usable photo");
    pixels[3] = 255;
    Check(visible(pixels, width, height, tightPitch, format), $"opaque black is visible in {format} and is not mistaken for an empty render");
    pixels[3] = 0;
    pixels[^1] = 1;
    Check(visible(pixels, width, height, tightPitch, format), $"a nonzero alpha at the final pixel of {format} is sufficient");
    pixels[^1] = 0;
}
pixels[0] = pixels[1] = pixels[2] = 255;
Check(!visible(pixels, width, height, tightPitch, 28), "RGB data with zero alpha is still invisible");
pixels[3] = 255;
Check(!visible(pixels.AsSpan(0, pixels.Length - 1), width, height, tightPitch, 28), "a truncated final scanline is rejected even when an earlier pixel is visible");
Check(!visible([], width, height, tightPitch, 28), "an empty raw buffer is rejected");
foreach (var dimensions in new[] { (0, height), (-1, height), (width - 1, height), (width + 1, height),
    (width, 0), (width, -1), (width, height - 1), (width, height + 1), (int.MaxValue, int.MaxValue) })
    Check(!visible(pixels, dimensions.Item1, dimensions.Item2, tightPitch, 28), "only the exact thumbnail dimensions are accepted");
foreach (var pitch in new[] { 0, -1, tightPitch - 1, int.MaxValue })
    Check(!visible(pixels, width, height, pitch, 28), "invalid or overflowing row pitches cannot read outside the buffer");
foreach (var format in new[] { 0, 27, 30, 86, 88, 90, 92, int.MaxValue })
    Check(!visible(pixels, width, height, tightPitch, format), "unsupported pixel layouts are not guessed from their byte count");
const int paddedPitch = tightPitch + 16;
var padded = new byte[paddedPitch * height];
for (var row = 0; row < height; row++)
    padded.AsSpan(row * paddedPitch + tightPitch, paddedPitch - tightPitch).Fill(255);
Check(!visible(padded, width, height, paddedPitch, 87), "nonzero row padding is not part of the image");
padded[(height - 1) * paddedPitch + tightPitch - 1] = 255;
Check(visible(padded, width, height, paddedPitch, 87), "the final visible pixel remains detectable with row padding");
Check(!visible(padded.AsSpan(0, padded.Length - 1), width, height, paddedPitch, 87), "the declared padded image must fit in the supplied buffer");
var extra = new byte[pixels.Length + 4];
extra[^1] = 255;
Check(!visible(extra, width, height, tightPitch, 28), "bytes after the declared image do not create false visibility");
#else
Check(false, "compressed history pictures have no decoder size gate");
#endif
Console.WriteLine($"PortraitThumbnailPipelineTests: {checks} passed, 0 failed");
return 0;
#endif

delegate bool PixelVisibilityGuard(ReadOnlySpan<byte> pixels, int width, int height, int pitch, int dxgiFormat);
