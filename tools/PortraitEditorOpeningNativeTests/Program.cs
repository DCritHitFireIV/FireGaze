using FireGaze.PortraitPreview;
using NativeEditor = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentBannerEditor;

var passed = 0;
var failed = 0;
void Check(bool value, string name)
{
    Console.WriteLine((value ? "PASS " : "FAIL ") + name);
    if (value) passed++; else failed++;
}
foreach (var focus in new[] { 0, 1 })
{
    using var feature = new PortraitPreviewFeature(); feature.SetFocus(focus);
    Check(feature.Sample() is { Agent: > 0, State: 0, OperationsIdle: true }
        && feature.BoundaryReads == 2 && NativeEditor.GetterCalls == 1,
        "real native sampler accepts known stable focus without requiring foreground: " + focus);
    feature.SetOpened();
    Check(feature.Sample() is { State: > 0, View: > 0, StateOwner: > 0, ViewOwner: > 0, ReadyIdentity: not null },
        "real native sampler preserves ready root and backlink reads with known focus: " + focus);
}
foreach (var focus in new[] { 0, 1 })
{
    using var feature = new PortraitPreviewFeature(); feature.SetFocus(focus);
    var receipt = new PortraitEditorOpeningController(feature.Sample, () => 0);
    var nonce = Guid.NewGuid().ToString("D"); var calls = 0;
    Check(receipt.Open("gearset", nonce, () => { calls++; feature.SetOpened(); })
        && receipt.Read() is { Status: "ReturnedPending", Nonce: var observed } && observed == nonce && calls == 1,
        "real native sampler allows one synchronous receipt-bound opening with known focus: " + focus);
    receipt.Update();
    Check(receipt.Read() is { Status: "Ready", ReadyIdentity: not null, MayMutate: false, MayClose: false }
        && !receipt.Open("gearset", nonce, () => calls++) && calls == 1,
        "native sampler keeps readiness attribution, nonce replay and permission limits with known focus: " + focus);
}
foreach (var focus in new int?[] { null, 2, -1 })
{
    using var feature = new PortraitPreviewFeature(); feature.SetFocus(focus);
    Check(feature.Sample() is null && NativeEditor.GetterCalls == 0 && feature.BoundaryReads == 1,
        "unknown or malformed focus rejects before any native getter: " + focus);
}
foreach (var mutation in new Func<PortraitSafetyBoundary, PortraitSafetyBoundary>[] {
    value => value with { FocusByte = 1 }, value => value with { FocusByte = null }, value => value with { FocusByte = 2 },
    value => value with { Frame = 43 }, value => value with { FrameworkAddress = 11 },
    value => value with { SourceAddress = 101 }, value => value with { SourceGeneration = 10 },
    value => value with { ConditionAddress = 201 }, value => value with { ConditionMaxEntries = 111 },
    value => value with { Conditions = PortraitPreviewFeature.Flags(1, 5) },
    value => value with { Player = value.Player! with { TerritoryID = 179 } },
    value => value with { EditorAgent = value.EditorAgent + 1 }, value => value with { EditorState = 1 }, value => value with { EditorView = 1 } })
{
    using var feature = new PortraitPreviewFeature(); feature.SetFocus(0); feature.After = mutation(feature.After);
    Check(feature.Sample() is null && feature.BoundaryReads == 2,
        "real native sampler refuses a changed focus, frame, source, conditions or editor boundary");
}
foreach (var offThread in new[] { false, true })
{
    using var feature = new PortraitPreviewFeature(); feature.SetFocus(0);
    if (offThread) FireGaze.Plugin.Framework.IsInFrameworkUpdateThread = false; else feature.SetDisposed();
    Check(feature.Sample() is null && feature.BoundaryReads == 0 && NativeEditor.GetterCalls == 0,
        "disposed or non-Framework sampler performs no boundary or native reads");
}
{
    using var feature = new PortraitPreviewFeature(); feature.SetFocus(1); feature.After = feature.After with { FocusByte = 0 };
    Check(feature.Sample() is null && feature.BoundaryReads == 2,
        "foreground-to-background change inside one native sampling boundary is also rejected");
}
foreach (var busy in new[] { "sourceCensus", "nativeLive", "nativeLivePhysical", "lightingFit", "lightDrag", "photosBusy",
    "photosPhysical", "livePhysical", "fitPhysical", "windowCensus", "plateClose", "plateMirror" })
{
    using var feature = new PortraitPreviewFeature(); feature.SetFocus(0); feature.SetBusy(busy);
    var sample = feature.Sample(); var calls = 0;
    var receipt = new PortraitEditorOpeningController(() => sample, () => 0);
    Check(sample is { OperationsIdle: false } && !receipt.Open("gearset", Guid.NewGuid().ToString("D"), () => calls++) && calls == 0,
        "background acceptance preserves each physical idle gate through the real sampler: " + busy);
}
{
    using var feature = new PortraitPreviewFeature(); feature.SetFocus(0);
    NativeEditor.DuringGetter = () => feature.Character = new(77, 99);
    Check(feature.Sample() is null, "character identity replacement during a native getter remains rejected");
}
{
    using var feature = new PortraitPreviewFeature(); feature.SetFocus(0);
    feature.DuringBoundary = () => { if (feature.BoundaryReads == 1) feature.ChangeGearset(); };
    Check(feature.Sample() is null, "current gearset replacement between native reads remains rejected");
}
foreach (var preview in new[] { false, true })
{
    using var feature = new PortraitPreviewFeature(); feature.SetFocus(0);
    if (preview) feature.SetPreviewActive(); else feature.SetSaveGuardUnavailable();
    var sample = feature.Sample(); var calls = 0;
    var receipt = new PortraitEditorOpeningController(() => sample, () => 0);
    Check(!receipt.Open("gearset", Guid.NewGuid().ToString("D"), () => calls++) && calls == 0,
        "background sampling retains preview and native save guard gates");
}
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
