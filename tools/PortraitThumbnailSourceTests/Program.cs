#if !HAS_SOURCE
Console.Error.WriteLine("FAIL portrait thumbnails have no framework-only retained-source capture boundary");
return 1;
#else
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.PortraitPreview;
using FFXIVClientStructs.FFXIV.Component.GUI;

var checks = 0;
void Check(bool value, string reason) { if (!value) throw new Exception(reason); checks++; }
var identity = new PortraitIdentity(1, 2, 3, 4, 5);

var scanMethod = typeof(PortraitThumbnailSource).GetMethod("FindPortrait", BindingFlags.Static | BindingFlags.NonPublic);
Check(scanMethod is not null, "portrait scanning needs an owner-validated boundary around native type lookups");
var scan = (Func<nint, Func<bool>, Func<nint, ComponentType>, nint>)scanMethod!.CreateDelegate(
    typeof(Func<nint, Func<bool>, Func<nint, ComponentType>, nint>));
unsafe
{
    var addon = (AtkUnitBase*)NativeMemory.AllocZeroed((nuint)sizeof(AtkUnitBase));
    var nodes = (AtkResNode**)NativeMemory.AllocZeroed((nuint)(2 * sizeof(nint)));
    var first = (AtkComponentNode*)NativeMemory.AllocZeroed((nuint)sizeof(AtkComponentNode));
    var second = (AtkComponentNode*)NativeMemory.AllocZeroed((nuint)sizeof(AtkComponentNode));
    var firstComponent = (AtkComponentBase*)NativeMemory.AllocZeroed((nuint)sizeof(AtkComponentBase));
    var secondComponent = (AtkComponentBase*)NativeMemory.AllocZeroed((nuint)sizeof(AtkComponentBase));
    try
    {
        addon->UldManager.NodeList = nodes; addon->UldManager.NodeListCount = 2;
        first->Type = (NodeType)1000; second->Type = (NodeType)1000;
        first->Component = firstComponent; second->Component = secondComponent;
        firstComponent->OwnerNode = first; secondComponent->OwnerNode = second;
        nodes[0] = (AtkResNode*)first; nodes[1] = (AtkResNode*)second;
        var valid = true; var lookups = 0;
        var invalidated = scan((nint)addon, () => valid, address =>
        {
            lookups++; valid = false;
            // Reentrant callbacks may replace or free the array immediately.
            addon->UldManager.NodeList = null;
            return ComponentType.Base;
        });
        Check(invalidated == 0 && lookups == 1, "owner replacement during a nonmatching type lookup stops before the next stale node");
        addon->UldManager.NodeList = nodes;
        valid = true; lookups = 0;
        var single = scan((nint)addon, () => valid, address => { lookups++; return lookups == 1 ? ComponentType.Portrait : ComponentType.Base; });
        Check(single == (nint)firstComponent && lookups == 2, "unique portrait resolved from live owner");
        var ambiguous = scan((nint)addon, () => valid, _ => ComponentType.Portrait);
        Check(ambiguous == 0, "multiple portrait components fail closed");
        valid = false; lookups = 0;
        Check(scan((nint)addon, () => valid, _ => { lookups++; return ComponentType.Portrait; }) == 0 && lookups == 0,
            "invalid owner is rejected before scanning any native nodes");
    }
    finally
    {
        NativeMemory.Free(secondComponent); NativeMemory.Free(firstComponent); NativeMemory.Free(second);
        NativeMemory.Free(first); NativeMemory.Free(nodes); NativeMemory.Free(addon);
    }
}

var fullMethod = typeof(PortraitThumbnailSource).GetMethod("TryCreateCharacterCopyArgs", BindingFlags.Static | BindingFlags.NonPublic);
Check(fullMethod is not null, "character render target must not be cropped by its logical ULD display rectangle");
var fullArguments = new object?[] { 576u, 960u, 576u, 960u, null };
Check((bool)fullMethod!.Invoke(null, fullArguments)!, "actual character render target dimensions accepted");
var full = (TextureModificationArgs)fullArguments[4]!;
Check(full.Uv0 == Vector2.Zero && full.Uv1 == Vector2.One, "576x960 character target is captured in full although its ULD node is only 256x420");
fullArguments = [1024u, 1024u, 576u, 960u, null];
Check((bool)fullMethod.Invoke(null, fullArguments)!, "padded character target accepted");
full = (TextureModificationArgs)fullArguments[4]!;
Check(full.Uv1 == new Vector2(576f / 1024, 960f / 1024), "allocated render target padding is excluded without trimming the actual portrait");
foreach (var dimensions in new[] { new uint[] {576,960,0,960}, new uint[] {576,960,577,960}, new uint[] {576,960,576,961}, new uint[] {32768,960,576,960} })
{
    fullArguments = [dimensions[0],dimensions[1],dimensions[2],dimensions[3],null];
    Check(!(bool)fullMethod.Invoke(null, fullArguments)!, "invalid actual/allocated character target dimensions fail closed");
}

Check(!PortraitThumbnailSource.TryCreateCopyArgs(0, 256, 0, 0, 1, 1, out _), "zero allocation rejected");
Check(!PortraitThumbnailSource.TryCreateCopyArgs(256, 0, 0, 0, 1, 1, out _), "zero height rejected");
Check(!PortraitThumbnailSource.TryCreateCopyArgs(32768, 256, 0, 0, 1, 1, out _), "unreasonable GPU allocation rejected");
Check(!PortraitThumbnailSource.TryCreateCopyArgs(256, 256, 0, 0, 0, 1, out _), "empty image crop rejected");
Check(!PortraitThumbnailSource.TryCreateCopyArgs(256, 256, 200, 0, 100, 256, out _), "crop cannot read outside allocation");
Check(!PortraitThumbnailSource.TryCreateCopyArgs(256, 256, 0, 200, 256, 100, out _), "vertical crop cannot read outside allocation");
Check(PortraitThumbnailSource.TryCreateCopyArgs(512, 512, 64, 32, 256, 400, out var crop), "valid atlas crop accepted");
Check(crop.Uv0 == new Vector2(.125f, .0625f) && crop.Uv1 == new Vector2(.625f, .84375f), "UV honors actual part rectangle within allocated texture");
Check(crop.NewWidth == 160 && crop.NewHeight == 256, "fixed small thumbnail output");
Check(crop.DxgiFormat == 28, "character copy output uses renderable RGBA8");

var frameUV0 = typeof(PortraitThumbnailLayers).GetProperty("FrameUV0");
var frameUV1 = typeof(PortraitThumbnailLayers).GetProperty("FrameUV1");
var accentUV0 = typeof(PortraitThumbnailLayers).GetProperty("AccentUV0");
var accentUV1 = typeof(PortraitThumbnailLayers).GetProperty("AccentUV1");
Check(frameUV0 is not null && frameUV1 is not null && accentUV0 is not null && accentUV1 is not null,
    "independent normalized layers expose full UV rectangles for exactly one final composition");
using (var legacy = new PortraitThumbnailLayers(new FakeTexture(), new FakeTexture(), new FakeTexture()))
{
    Check((Vector2)frameUV0!.GetValue(legacy)! == Vector2.Zero && (Vector2)frameUV1!.GetValue(legacy)! == Vector2.One,
        "legacy three-layer constructor preserves full frame UV defaults");
    Check((Vector2)accentUV0!.GetValue(legacy)! == Vector2.Zero && (Vector2)accentUV1!.GetValue(legacy)! == Vector2.One,
        "legacy three-layer constructor preserves full accent UV defaults");
}

foreach (var mode in new[] { "thread", "identity", "missing", "replaced" })
{
    var api = new FakeApi { Framework = mode != "thread", Current = mode != "identity", Missing = mode == "missing", ReplacedOnAcquire = mode == "replaced" };
    var source = new PortraitThumbnailSource(api);
    var result = await source.CaptureAsync(identity);
    Check(result is null, mode + " fails closed");
    Check(api.Copies == 0, mode + " never copies invalid source");
    Check(api.Acquires == (mode is "thread" or "identity" ? 0 : 1), mode + " does not inspect native layers outside valid framework owner");
    Check(api.Inputs.All(x => x.Disposes == 1), mode + " acquired references released once");
}

foreach (var layers in new[] { 1, 2, 3 })
{
    var api = new FakeApi { LayerCount = layers };
    var source = new PortraitThumbnailSource(api);
    using var result = await source.CaptureAsync(identity);
    Check(result is not null && result.Character is not null, "character copied");
    Check((result!.Frame is not null) == (layers >= 2) && (result.Accent is not null) == (layers >= 3), "hidden overlays remain absent");
    Check(api.Copies == layers && api.Inputs.Count == layers, "every visible layer is independently copied before raw readback; compressed atlases never escape the source boundary");
    Check(api.Inputs.All(x => x.Disposes == 1), "all original references are released after their real copy tasks complete");
    Check(ReferenceEquals(result.Frame, layers >= 2 ? api.Outputs[1] : null) && ReferenceEquals(result.Accent, layers >= 3 ? api.Outputs[2] : null), "result owns independent frame and accent outputs rather than the original atlases");
    Check(api.CopyInputs.Select(value => value.Texture).SequenceEqual(api.Inputs), "copies run in character, frame, accent order using the retained source references");
    Check(api.CopyInputs.All(value => value.CopyArgs.NewWidth == 160 && value.CopyArgs.NewHeight == 256
        && value.CopyArgs.DxgiFormat == 28 && !value.CopyArgs.MakeOpaque), "all copied layers request fixed RGBA8 size without changing alpha");
    Check(api.Outputs.All(x => x.Disposes == 0), "owned output remains alive for caller");
    if (layers >= 2)
        Check((Vector2)frameUV0!.GetValue(result)! == Vector2.Zero
            && (Vector2)frameUV1!.GetValue(result)! == Vector2.One
            && api.CopyInputs[1].CopyArgs.Uv0 == new Vector2(.125f, .0625f)
            && api.CopyInputs[1].CopyArgs.Uv1 == new Vector2(.625f, .84375f), "frame crop is applied exactly once by the copy service and not again at composition");
    if (layers >= 3)
        Check((Vector2)accentUV0!.GetValue(result)! == Vector2.Zero
            && (Vector2)accentUV1!.GetValue(result)! == Vector2.One
            && api.CopyInputs[2].CopyArgs.Uv0 == new Vector2(.25f, .125f)
            && api.CopyInputs[2].CopyArgs.Uv1 == new Vector2(.75f, .875f), "accent crop is independently normalized exactly once");
    result.Dispose(); result.Dispose();
    Check(api.Outputs.All(x => x.Disposes == 1) && api.Inputs.All(x => x.Disposes == 1), "layers disposal is idempotent and owns all independent copied layers");
}

{
    var api = new FakeApi { OmitFrame = true };
    using var result = await new PortraitThumbnailSource(api).CaptureAsync(identity);
    Check(result is not null && result.Frame is null && ReferenceEquals(result.Accent, api.Outputs[1]),
        "accent-only capture normalizes the accent independently when frame is hidden");
    Check(api.Copies == 2 && api.Inputs.All(value => value.Disposes == 1), "accent-only capture copies both visible layers and releases source atlases");
    Check((Vector2)accentUV0!.GetValue(result)! == Vector2.Zero
        && (Vector2)accentUV1!.GetValue(result)! == Vector2.One
        && api.CopyInputs[1].CopyArgs.Uv0 == new Vector2(.25f, .125f), "accent-only crop is applied before full-output composition");
    result!.Dispose();
    Check(api.Inputs.All(x => x.Disposes == 1) && api.Outputs.All(x => x.Disposes == 1), "accent-only disposal releases every reference once");
}
{
    var api = new FakeApi { DefaultOverlayUV1 = true };
    using var result = await new PortraitThumbnailSource(api).CaptureAsync(identity);
    Check((Vector2)frameUV1!.GetValue(result)! == Vector2.One && (Vector2)accentUV1!.GetValue(result)! == Vector2.One,
        "zero UV1 supplied by a default argument follows the official full-texture convention");
}

{
    var api = new FakeApi { DelayCopies = true };
    var source = new PortraitThumbnailSource(api);
    var pending = source.CaptureAsync(identity);
    Check(!pending.IsCompleted && api.Inputs.All(x => x.Disposes == 0), "sources retained throughout asynchronous copy");
    api.Framework = false; api.Current = false; api.RejectNativeReads = true;
    api.CompleteCopies();
    using var result = await pending;
    Check(result is not null && api.Copies == 3, "all detached layer copies complete without native or framework ownership dereference after await");
    Check(api.Inputs.All(x => x.Disposes == 1), "async completion releases all source atlases after normalization");
    result!.Dispose();
    Check(api.Inputs.All(x => x.Disposes == 1), "late async result disposal leaves all original references released once");
}

foreach (var layer in new[] { 1, 2, 3 })
{
    var api = new FakeApi { PauseCopy = layer };
    var pending = new PortraitThumbnailSource(api).CaptureAsync(identity);
    Check(!pending.IsCompleted && api.Copies == layer && api.Outputs.Count == layer - 1,
        "layer " + layer + " copy is strictly sequential and awaits the actual provider task");
    Check(api.Inputs.All(value => value.Disposes == 0) && api.Outputs.All(value => value.Disposes == 0),
        "layer " + layer + " pending task keeps every source and prior output alive");
    api.CompleteCopies();
    using var result = await pending;
    Check(result is not null && api.Inputs.All(value => value.Disposes == 1), "layer " + layer + " completion releases retained inputs");
}

foreach (var failure in new[] { 1, 2, 3 })
{
    var api = new FakeApi { FailCopy = failure, PauseCopy = failure };
    var source = new PortraitThumbnailSource(api);
    var pending = source.CaptureAsync(identity);
    Check(!pending.IsCompleted && api.Copies == failure
        && api.Inputs.All(value => value.Disposes == 0) && api.Outputs.All(value => value.Disposes == 0),
        "pending failing layer " + failure + " retains its sources and prior outputs until actual completion");
    api.CompleteCopies();
    bool failed = false;
    try { using var result = await pending; } catch (InvalidOperationException) { failed = true; }
    Check(failed, "copy failure reported");
    Check(api.Inputs.All(x => x.Disposes == 1), "copy failure releases all retained input layers");
    Check(api.Outputs.All(x => x.Disposes == 1), "copy failure releases previously copied output layers");
}

foreach (var layer in new[] { 1, 2, 3 })
{
    var api = new FakeApi { SynchronousFailCopy = layer };
    var failed = false;
    try { using var result = await new PortraitThumbnailSource(api).CaptureAsync(identity); }
    catch (InvalidOperationException) { failed = true; }
    Check(failed && api.Copies == layer, "synchronous copy prefix failure stops before subsequent layer copies");
    Check(api.Inputs.All(value => value.Disposes == 1) && api.Outputs.All(value => value.Disposes == 1),
        "synchronous layer " + layer + " failure releases every input and prior independent output once");
}

{
    var api = new FakeApi();
    var source = new PortraitThumbnailSource(api);
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    bool canceled = false;
    try { using var result = await source.CaptureAsync(identity, cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
    Check(canceled && api.Acquires == 0 && api.Copies == 0, "already canceled request never reads native source");
}

foreach (var layer in new[] { 1, 2, 3 })
{
    var api = new FakeApi { CancelCopy = layer };
    var source = new PortraitThumbnailSource(api);
    bool canceled = false;
    try { using var result = await source.CaptureAsync(identity); } catch (OperationCanceledException) { canceled = true; }
    Check(canceled, "mid-copy cancellation propagated");
    Check(api.Inputs.All(x => x.Disposes == 1), "cancellation releases retained input layers");
    Check(api.Outputs.All(x => x.Disposes == 1), "cancellation releases completed output layers");
}

// Cancellation after acquisition and after a producer returns must release every owned resource.
foreach (var timing in new[] { "acquire", "copy" })
{
    using var cancellation = new CancellationTokenSource();
    var api = new FakeApi();
    if (timing == "acquire") api.AfterAcquire = cancellation.Cancel;
    else api.AfterCopy = cancellation.Cancel;
    var source = new PortraitThumbnailSource(api);
    var canceled = false;
    try { using var result = await source.CaptureAsync(identity, cancellation.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Check(canceled, timing + " cancellation propagated");
    Check(api.Inputs.All(x => x.Disposes == 1) && api.Outputs.All(x => x.Disposes == 1),
        timing + " cancellation does not leak or double-dispose an overlay");
    Check(api.Copies == (timing == "acquire" ? 0 : 1), timing + " cancellation never starts overlay copies");
}
foreach (var layer in new[] { 1, 2, 3 })
{
    using var cancellation = new CancellationTokenSource();
    var api = new FakeApi { PauseCopy = layer, IgnoreCancellation = true };
    var pending = new PortraitThumbnailSource(api).CaptureAsync(identity, cancellation.Token);
    cancellation.Cancel();
    Check(api.Inputs.All(x => x.Disposes == 0), "canceled pending producer retains its resources until it really completes");
    api.CompleteCopies();
    var canceled = false;
    try { using var result = await pending; } catch (OperationCanceledException) { canceled = true; }
    Check(canceled && api.Copies == layer, "late layer " + layer + " completion observes cancellation before starting another copy or transferring ownership");
    Check(api.Inputs.All(x => x.Disposes == 1) && api.Outputs.All(x => x.Disposes == 1),
        "ignored producer cancellation cleans every source, prior output and late copy");
}

foreach (var layer in new[] { 0, 1, 2 })
{
    var api = new FakeApi();
    api.AfterAcquire = () => api.Inputs[layer].OnDispose = () => throw new InvalidOperationException("unconfirmed release");
    var physical = new PortraitCaptureRetirement();
    var held = physical.Enter();
    try { using var result = await new PortraitThumbnailSource(api).CaptureAsync(identity); }
    catch (Exception error) { physical.ObserveFailure(error); }
    held.Dispose();
    Check(!physical.Seal().IsCompleted && physical.CleanupFailures > 0,
        "failed source-input release transfers unconfirmed ownership to the host physical quarantine");
    Check(api.Inputs.All(value => value.Disposes == 1) && api.Outputs.All(value => value.Disposes == 1),
        "source-input cleanup failure still releases all independent layers and unpublished output exactly once");
}

foreach (var layer in new[] { 0, 1, 2 })
{
    var api = new FakeApi { FailCopy = 3 };
    if (layer == 2) api.FailCopy = 0;
    api.AfterCopy = () => { if (api.Outputs.Count > layer) api.Outputs[layer].OnDispose = () => throw new InvalidOperationException("unconfirmed copied-layer release"); };
    var physical = new PortraitCaptureRetirement();
    var held = physical.Enter();
    try { using var result = await new PortraitThumbnailSource(api).CaptureAsync(identity); }
    catch (Exception error) { physical.ObserveFailure(error); }
    held.Dispose();
    Check(!physical.Seal().IsCompleted && physical.CleanupFailures > 0,
        "unconfirmed copied layer " + layer + " release remains physically quarantined");
    Check(api.Inputs.All(value => value.Disposes == 1) && api.Outputs.All(value => value.Disposes == 1),
        "copied-layer cleanup failure never prevents other independent resources releasing once");
}

#if HAS_PHOTO_TRACE
await PhotoTraceCases.Run(identity, Check);
await PhotoTraceSnapshotCases.Run(Check);
#else
Check(false, "production source photo phase trace is missing");
#endif
Console.WriteLine($"{checks}/{checks} portrait thumbnail source checks passed; no game or GPU calls.");
return 0;

sealed class FakeTexture : IDalamudTextureWrap
{
    public int Disposes;
    public Action? OnDispose;
    public ImTextureID Handle => new((nint)123);
    public int Width => 160;
    public int Height => 256;
    public void Dispose() { Disposes++; OnDispose?.Invoke(); }
}

sealed class FakeApi : IPortraitThumbnailSourceApi
{
    public bool Framework = true, Current = true, Missing, ReplacedOnAcquire, RejectNativeReads, DelayCopies;
    public int LayerCount = 3, Acquires, Copies, FailCopy, CancelCopy, PauseCopy, SynchronousFailCopy;
    public bool IgnoreCancellation, OmitFrame, DefaultOverlayUV1;
    public Action? AfterAcquire, BeforeCopy, AfterCopy;
    public List<FakeTexture> Inputs = [], Outputs = [];
    public List<PortraitThumbnailInput> CopyInputs = [];
    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsFrameworkThread { get { if (RejectNativeReads) throw new Exception("native read after await"); return Framework; } }
    public bool IsCurrent(PortraitIdentity identity) { if (RejectNativeReads) throw new Exception("owner read after await"); return Current; }
    public bool TryAcquire(PortraitIdentity identity, out PortraitThumbnailInputs? inputs)
    {
        Acquires++; inputs = null;
        if (Missing) return false;
        PortraitThumbnailInput New(int layer)
        {
            var texture = new FakeTexture(); Inputs.Add(texture);
            return new(texture, new() { NewWidth = 160, NewHeight = 256, DxgiFormat = 28, MakeOpaque = false,
                Uv0 = layer == 2 ? new(.125f, .0625f) : new(.25f, .125f),
                Uv1 = DefaultOverlayUV1 ? Vector2.Zero : layer == 2 ? new(.625f, .84375f) : new(.75f, .875f) });
        }
        inputs = new(New(1), LayerCount >= 2 && !OmitFrame ? New(2) : null, LayerCount >= 3 ? New(3) : null);
        if (ReplacedOnAcquire) Current = false;
        AfterAcquire?.Invoke();
        return true;
    }
    public Task<IDalamudTextureWrap> CopyAsync(PortraitThumbnailInput input, CancellationToken token)
    {
        BeforeCopy?.Invoke();
        var layer = ++Copies;
        CopyInputs.Add(input);
        if (layer == SynchronousFailCopy) throw new InvalidOperationException("synchronous copy failure");
        return CompleteCopyAsync(token, layer);
    }
    private async Task<IDalamudTextureWrap> CompleteCopyAsync(CancellationToken token, int layer)
    {
        if (DelayCopies || PauseCopy == layer) await gate.Task;
        if (layer == FailCopy) throw new InvalidOperationException("copy failure");
        if (layer == CancelCopy) throw new OperationCanceledException();
        if (!IgnoreCancellation) token.ThrowIfCancellationRequested();
        var output = new FakeTexture(); Outputs.Add(output); AfterCopy?.Invoke(); return output;
    }
    public void CompleteCopies() => gate.SetResult();
}
#endif
