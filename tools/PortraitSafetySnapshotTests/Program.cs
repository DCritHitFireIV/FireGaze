using System.Text.Json;
using System.Text.Json.Nodes;
#if SAFETY_SNAPSHOT
using FireGaze.PortraitPreview;
#endif

#if !SAFETY_SNAPSHOT
Console.Error.WriteLine("FAIL ReadSafetySnapshot is missing; no fresh one-Framework safety snapshot exists");
return 1;
#else
var passed = 0;
var failed = 0;
void Check(bool value, string name)
{
    Console.WriteLine((value ? "PASS " : "FAIL ") + name);
    if (value) passed++; else failed++;
}
JsonElement Read(FakeSource source)
{
    using var document = JsonDocument.Parse(new PortraitSafetySnapshotCollector(source).ReadJSON(17));
    return document.RootElement.Clone();
}
bool Partial(FakeSource source, string reason) => Read(source).GetProperty("unavailableReasons").EnumerateArray()
    .Any(value => value.GetString() == reason);
string EditedState(Action<JsonObject> edit)
{
    var state = JsonNode.Parse(FakeSource.State(1))!.AsObject();
    edit(state);
    return state.ToJsonString();
}

var cold = new FakeSource();
_ = new PortraitSafetySnapshotCollector(cold);
Check(cold.Calls.Count == 0, "collector construction executes no source, native, clock or scheduling operation");
var good = new FakeSource();
var normal = Read(good);
Check(normal.GetProperty("status").GetString() == "Complete", "fresh stable complete evidence is available");
var censusMissing = Read(new FakeSource { Version = "1.5.0.42" });
Check(censusMissing.GetProperty("status").GetString() == "Partial"
    && censusMissing.GetProperty("unavailableReasons").EnumerateArray().Any(value => value.GetString() == "source-census-unavailable"),
    "new runtime cannot omit source census physical ownership evidence");
string CensusManaged(bool active, Action<JsonObject>? edit = null)
{
    var managed = JsonNode.Parse(FakeSource.Managed(false))!.AsObject();
    var host = new { state = active ? "Observing" : "Retired", hooks = active ? 4 : 0, inFlight = 0,
        reservationHeld = active, failed = false };
    managed["sourceCensus"] = JsonSerializer.SerializeToNode(new { known = true, state = active ? "Armed" : "Off", attempt = 1,
        blocksOperations = active, physicalDrainCompleted = !active, source = host, counters = host, resources = host,
        productionAdmission = false });
    edit?.Invoke(managed["sourceCensus"]!.AsObject()); return managed.ToJsonString();
}
foreach (var activeCensus in new[] { false, true })
{
    var value = Read(new FakeSource { Version = "1.5.0.42", ManagedBefore = CensusManaged(activeCensus) });
    Check(value.GetProperty("status").GetString() == "Complete"
        && value.GetProperty("managed").GetProperty("sourceCensus").GetProperty("blocksOperations").GetBoolean() == activeCensus,
        "complete census evidence preserves physical busy/idle for the external guard: " + activeCensus);
}
foreach (var mutation in new Action<JsonObject>[] {
    value => value["known"] = false,
    value => value["state"] = "Unknown",
    value => value["blocksOperations"] = true,
    value => value["source"] = null,
    value => value["source"]!["inFlight"] = -1,
    value => value["source"]!["hooks"] = 1,
    value => value["counters"]!["reservationHeld"] = true,
    value => value["resources"]!["failed"] = true,
    value => value["productionAdmission"] = true })
    Check(Partial(new FakeSource { Version = "1.5.0.42", ManagedBefore = CensusManaged(false, mutation) }, "source-census-unavailable"),
        "new runtime refuses missing, malformed or falsely drained native ownership evidence");
Check(Partial(new FakeSource { Version = "1.5.0.42", ManagedBefore = CensusManaged(true), ManagedAfter = CensusManaged(false) }, "managed-changed"),
    "physical census retirement transition during the two managed reads is not a stable snapshot");
string OwnedManaged(string state = "Off", long attempt = 1, long censusAttempt = 2, int hooks = 0, int inFlight = 0,
    bool reservation = false, Action<JsonObject>? edit = null)
{
    var managed = JsonNode.Parse(CensusManaged(false))!.AsObject();
    managed["ownedPassive"] = JsonSerializer.SerializeToNode(new { known = true, state, attempt, censusAttempt,
        blocksOperations = state != "Off", physicalDrainCompleted = state == "Off", hooks, inFlight,
        reservationHeld = reservation, failed = state == "Faulted", productionAdmission = false });
    edit?.Invoke(managed["ownedPassive"]!.AsObject()); return managed.ToJsonString();
}
Check(Partial(new FakeSource { Version = "1.5.0.42", OwnedCapability = JsonValue.Create(true), ManagedBefore = CensusManaged(false) },
    "owned-passive-unavailable"), "runtime capability requires independent owned passive physical evidence");
Check(Partial(new FakeSource { Version = "1.5.0.42", OwnedCapability = JsonValue.Create("true"), ManagedBefore = OwnedManaged() },
    "owned-passive-unavailable"), "malformed owned passive capability cannot silently disable evidence requirements");
foreach (var evidence in new[] {
    OwnedManaged(attempt: 0, censusAttempt: 0), OwnedManaged(),
    OwnedManaged("WaitingForCensusDrain"), OwnedManaged("Installing"),
    OwnedManaged("Installing", hooks: 17, reservation: true),
    OwnedManaged("WaitingForSourceReturn", hooks: 39, reservation: true),
    OwnedManaged("Retiring"), OwnedManaged("Retiring", hooks: 39, inFlight: 1, reservation: true),
    OwnedManaged("Faulted") })
{
    var value = Read(new FakeSource { Version = "1.5.0.42", OwnedCapability = JsonValue.Create(true), ManagedBefore = evidence });
    Check(value.GetProperty("status").GetString() == "Complete"
        && value.GetProperty("managed").GetProperty("ownedPassive").GetRawText()
            == JsonDocument.Parse(evidence).RootElement.GetProperty("ownedPassive").GetRawText(),
        "complete owned passive evidence preserves legitimate idle, partial-install, physical-tail or failed state");
}
foreach (var mutation in new Action<JsonObject>[] {
    value => value.Remove("known"), value => value["known"] = false,
    value => value["state"] = "Unknown", value => value["attempt"] = -1,
    value => value["attempt"] = 0, value => value["censusAttempt"] = 0,
    value => value["censusAttempt"] = -1, value => value["hooks"] = -1,
    value => value["hooks"] = 40, value => value["hooks"] = 1,
    value => value["inFlight"] = 1, value => value["inFlight"] = -1,
    value => value["reservationHeld"] = true, value => value["failed"] = true,
    value => value["blocksOperations"] = true, value => value["physicalDrainCompleted"] = false,
    value => value["productionAdmission"] = true, value => value.Remove("productionAdmission"),
    value => value["hooks"] = 4294967296L, value => value["inFlight"] = 4294967296L,
    value => value["attempt"] = 1.25, value => value["reservationHeld"] = "false" })
    Check(Partial(new FakeSource { Version = "1.5.0.42", OwnedCapability = JsonValue.Create(true), ManagedBefore = OwnedManaged(edit: mutation) },
        "owned-passive-unavailable"), "owned passive unknown, malformed or falsely drained evidence fails closed");
foreach (var evidence in new[] {
    OwnedManaged("WaitingForCensusDrain", hooks: 1, reservation: true),
    OwnedManaged("WaitingForCensusDrain", inFlight: 1),
    OwnedManaged("WaitingForSourceReturn", hooks: 38, reservation: true),
    OwnedManaged("WaitingForSourceReturn", hooks: 39),
    OwnedManaged("Installing", hooks: 1), OwnedManaged("Retiring", hooks: 1), OwnedManaged("Faulted", hooks: 1),
    OwnedManaged("Retiring", inFlight: 1),
    OwnedManaged("Retiring", edit: value => value["physicalDrainCompleted"] = true),
    OwnedManaged("Faulted", edit: value => value["failed"] = false),
    OwnedManaged("Installing", attempt: 0, censusAttempt: 0) })
    Check(Partial(new FakeSource { Version = "1.5.0.42", OwnedCapability = JsonValue.Create(true), ManagedBefore = evidence },
        "owned-passive-unavailable"), "owned passive state, count, reservation and attempt invariants must agree");
foreach (var field in new[] { "state", "attempt", "censusAttempt", "hooks", "inFlight", "blocksOperations",
    "physicalDrainCompleted", "reservationHeld", "failed" })
{
    Check(Partial(new FakeSource { OwnedCapability = JsonValue.Create(true),
        ManagedBefore = OwnedManaged(edit: value => value.Remove(field)) }, "owned-passive-unavailable"),
        "owned passive field cannot be omitted: " + field);
    Check(Partial(new FakeSource { OwnedCapability = JsonValue.Create(true),
        ManagedBefore = OwnedManaged(edit: value => value[field] = new JsonObject()) }, "owned-passive-unavailable"),
        "owned passive malformed field is rejected with explicit evidence reason: " + field);
}
Check(Partial(new FakeSource { ManagedBefore = OwnedManaged(edit: value => value["known"] = false) }, "owned-passive-unavailable"),
    "owned passive evidence is validated whenever present even in a historical runtime");
Check(Partial(new FakeSource { OwnedCapability = JsonValue.Create(false), ManagedBefore = OwnedManaged(edit: value => value["known"] = false) },
    "owned-passive-unavailable"), "false capability cannot waive malformed owned passive evidence that is present");
Check(Partial(new FakeSource { Version = "1.5.0.42", OwnedCapability = JsonValue.Create(true), ManagedBefore = OwnedManaged(),
    ManagedAfter = CensusManaged(false) }, "owned-passive-unavailable"), "confirmation read independently requires owned physical evidence");
Check(Partial(new FakeSource { Version = "1.5.0.42", OwnedCapability = JsonValue.Create(true),
    ManagedBefore = OwnedManaged("Retiring", hooks: 39, inFlight: 1, reservation: true), ManagedAfter = OwnedManaged() }, "managed-changed"),
    "owned physical retirement between complete managed readings cannot become a stable snapshot");
var receiptFlags = Enumerable.Range(0, 112).Select(index => index == 1 ? 1 : 0).ToArray();
var receiptSample = new PortraitEditorOpeningSample(true, 1, 1, 10, 0, 0, 0, 0, false, -1, -1, 3,
    new(100, 9, 77, 88, 178, 18, 1, 2, 3, 1000, false, false, false, 0), receiptFlags, false, true, true, null);
var receiptController = new PortraitEditorOpeningController(() => receiptSample, () => 0);
var receiptManaged = JsonNode.Parse(CensusManaged(false))!.AsObject();
receiptManaged["editorOpening"] = JsonSerializer.SerializeToNode(receiptController.Read(),
    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
var stableReceiptManaged = receiptManaged.ToJsonString();
foreach (var changedReceipt in new[] {
    receiptController.Read() with { Epoch = 2, Status = "Invalidated", Reason = "PreFinalize" },
    receiptController.Read() with { Before = receiptSample },
    receiptController.Read() with { Returned = receiptSample with { State = 20, View = 30 } },
    receiptController.Read() with { ReadyIdentity = new(10,20,30,7,40) } })
{
    receiptManaged["editorOpening"] = JsonSerializer.SerializeToNode(changedReceipt,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    Check(Partial(new FakeSource { Version = "1.5.0.42", ManagedBefore = stableReceiptManaged,
        ManagedAfter = receiptManaged.ToJsonString() }, "managed-changed"),
        "complete managed double sampling detects opening lifecycle, sample and allocation changes");
}
Check(good.Calls.SequenceEqual(["clock", "runtime", "boundary", "editor", "managed", "editor", "managed", "boundary", "clock"]),
    "two complete confirmations occur in one already-owned Framework callback without mutation work");
Check(normal.GetProperty("sequence").GetInt64() == 17 && normal.GetProperty("runtime").GetProperty("mvid").GetString() == "mvid-current",
    "snapshot includes the caller sequence and exact current runtime identity");
Check(normal.GetProperty("editor").GetProperty("editorState").GetProperty("Composition").GetProperty("CameraYaw").GetSingle() == 1,
    "full fresh editor JSON is returned as detached JSON, not a state hash");
Check(normal.GetProperty("editor").GetProperty("editorState").GetProperty("Customize").ValueKind == JsonValueKind.String
    && normal.GetProperty("editor").GetProperty("editorState").GetProperty("Customize").GetBytesFromBase64().Length == 26
    && normal.GetProperty("status").GetString() == "Complete",
    "the real PortraitEditorSnapshot.ToJSON byte-array base64 contract is accepted");
var outsideFramework = new FakeSource { Framework = false };
Check(Partial(outsideFramework, "not-framework") && outsideFramework.Calls.Count == 0,
    "outside Framework fails without native sampling");
var disposed = new FakeSource { Disposed = true };
Check(Partial(disposed, "disposed") && disposed.Calls.Count == 0, "disposed source performs no reads");
Check(Partial(new FakeSource { BoundaryOverride = x => x with { FocusByte = null } }, "focus-unknown"), "unknown focus never becomes true");
var unfocused = Read(new FakeSource { BoundaryOverride = x => x with { FocusByte = 0 } });
Check(unfocused.GetProperty("before").GetProperty("focused").GetBoolean() == false, "real focus false remains false evidence");
Check(Partial(new FakeSource { BoundaryOverride = x => x with { FocusByte = 2 } }, "focus-unknown"), "non-boolean focus is unknown");
Check(Partial(new FakeSource { BoundaryOverride = x => x with { FrameworkAddress = 0 } }, "framework-root-unavailable"), "missing native Framework root is not a cached success");
Check(Partial(new FakeSource { AfterOverride = x => x with { FrameworkAddress = 999 } }, "boundary-changed"), "Framework root replacement is detected");
Check(Partial(new FakeSource { AfterOverride = x => x with { Frame = 43 } }, "boundary-changed"), "a Framework frame change invalidates the atomic observation");
Check(Partial(new FakeSource { AfterOverride = x => x with { SourceAddress = 333 } }, "boundary-changed"), "source pointer replacement is detected");
Check(Partial(new FakeSource { AfterOverride = x => x with { SourceGeneration = 92 } }, "boundary-changed"), "same-address player generation replacement is detected");
Check(Partial(new FakeSource { BoundaryOverride = x => x with { SourceGeneration = 0 } }, "source-unavailable"), "zero player generation is unknown");
Check(Partial(new FakeSource { AfterOverride = x => x with { Player = x.Player! with { TerritoryID = 88 } } }, "boundary-changed"), "territory change is detected");
Check(Partial(new FakeSource { AfterOverride = x => x with { Player = x.Player! with { X = 8 } } }, "boundary-changed"), "position change is detected");
Check(Partial(new FakeSource { AfterOverride = x => x with { ConditionAddress = 555 } }, "boundary-changed"), "Condition root replacement is detected");
Check(Partial(new FakeSource { BoundaryOverride = x => x with { ConditionMaxEntries = 111 } }, "conditions-unavailable"), "condition array must have the exact reviewed length");
Check(Partial(new FakeSource { BoundaryOverride = x => x with { Conditions = [2, .. x.Conditions![1..]] } }, "conditions-unavailable"), "non-boolean condition bytes fail closed");
Check(Partial(new FakeSource { AfterOverride = x => x with { Conditions = [1, .. x.Conditions![1..]] } }, "boundary-changed"), "passive condition changes are detected without masking them");
Check(Partial(new FakeSource { AfterOverride = x => x with { EditorView = 777 } }, "boundary-changed"), "editor root-chain change is detected");
Check(Partial(new FakeSource { EditorOverride = x => x with { Availability = "Unknown", EditorStateJSON = null, Identity = null } }, "editor-unavailable"),
    "hidden, loading or failed editor resolution is not forged into closed");
Check(Partial(new FakeSource { ConfirmationOverride = x => x with { Identity = x.Identity! with { Generation = 10 } } }, "editor-changed"),
    "same-address editor actor generation change is detected");
Check(Partial(new FakeSource { ConfirmationOverride = x => x with { EditorStateJSON = FakeSource.State(2) } }, "editor-changed"),
    "external full state edits are detected even when light angles would match");
Check(Partial(new FakeSource { EditorOverride = x => x with { EditorStateJSON = "{}" } }, "editor-unavailable"),
    "state omitted by a reader cannot be presented as an exact editor snapshot");
Check(Partial(new FakeSource { EditorOverride = x => x with { EditorStateJSON = EditedState(state => state["Customize"] = "invalid!") } }, "editor-unavailable"),
    "invalid base64 customization is rejected through the actual codec contract");
Check(Partial(new FakeSource { EditorOverride = x => x with { EditorStateJSON = EditedState(state => state["Customize"] = Convert.ToBase64String(new byte[25])) } }, "editor-unavailable"),
    "well-formed base64 with an incorrect customization length is rejected");
Check(Partial(new FakeSource { EditorOverride = x => x with { EditorStateJSON = EditedState(state => state["Look"]!["Pieces"]!.AsArray().RemoveAt(12)) } }, "editor-unavailable"),
    "incomplete equipment arrays cannot be accepted as a full native state");
Check(Partial(new FakeSource { EditorOverride = x => x with { EditorStateJSON = EditedState(state => state["Look"]!["Pieces"]![12]!["ItemID"] = 70000) } }, "editor-unavailable"),
    "actual facewear identifier range is enforced");
Check(Partial(new FakeSource { EditorOverride = x => x with { EditorStateJSON = EditedState(state => state["Composition"]!["AnimationProgress"] = -1f) } }, "editor-unavailable"),
    "actual snapshot IsValid rejects invalid animation ranges");
Check(Partial(new FakeSource { EditorOverride = x => x with { EditorStateJSON = EditedState(state => state["Composition"]!.AsObject().Remove("CameraYaw")) } }, "editor-unavailable"),
    "omitted composition values cannot silently become valid codec defaults");
Check(Partial(new FakeSource { EditorOverride = x => x with { EditorStateJSON = EditedState(state => state["Customize"] = new JsonArray(Enumerable.Range(0, 26).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray())) } }, "editor-unavailable"),
    "handwritten integer arrays cannot replace the real byte-array serialization contract");
Check(Partial(new FakeSource { EditorOverride = x => x with { Identity = x.Identity! with { Character = 100 } } }, "editor-unavailable"),
    "portrait identity may not alias the source player");
Check(Partial(new FakeSource { ManagedAfter = "{\"active\":true,\"light\":{\"token\":9},\"worldTrials\":{\"known\":true,\"active\":false}}" }, "managed-changed"),
    "light gesture/token or another managed guard change is detected");
Check(Partial(new FakeSource { ManagedBefore = "{\"worldTrials\":{\"known\":false}}", ManagedAfter = "{\"worldTrials\":{\"known\":false}}" }, "world-trials-unknown"),
    "missing sibling runtime is unknown, never no trials");
var busy = Read(new FakeSource { ManagedBefore = FakeSource.Managed(true), ManagedAfter = FakeSource.Managed(true) });
Check(busy.GetProperty("managed").GetProperty("worldTrials").GetProperty("active").GetBoolean(),
    "current world trial evidence is preserved for the external guard");
Check(Partial(new FakeSource { ThrowAt = "editor" }, "sampling-failed:InvalidOperationException"), "read exceptions do not substitute previous editor values");
var clone = new FakeSource();
var json = Read(clone).GetRawText();
clone.Flags[1] = 0;
Check(JsonDocument.Parse(json).RootElement.GetProperty("before").GetProperty("conditions")[1].GetInt32() == 1,
    "returned condition evidence does not share a mutable source array");
var closed = Read(new FakeSource { Closed = true });
Check(closed.GetProperty("status").GetString() == "Complete" && closed.GetProperty("editor").GetProperty("availability").GetString() == "Closed"
    && closed.GetProperty("editor").GetProperty("editorState").ValueKind == JsonValueKind.Null,
    "positive fresh null-state evidence can identify closed without an old snapshot");

var now = TimeSpan.Zero;
var isolation = new PortraitCaptureIsolation(() => now);
Check(isolation.TryConfigure("history", false, false), "bounded history lease can be established while idle");
now = TimeSpan.FromSeconds(10);
var firstLease = isolation.Peek();
now = TimeSpan.FromSeconds(61);
var expiredLease = isolation.Peek();
Check(firstLease.EffectiveMode == "history" && expiredLease.EffectiveMode == "off" && expiredLease.ConfiguredMode == "history",
    "Peek reports effective expiry without mutating configured mode");
now = TimeSpan.FromSeconds(10);
Check(isolation.Peek().EffectiveMode == "history" && isolation.Peek().ExpiresAt == firstLease.ExpiresAt,
    "reading an expired lease neither clears nor refreshes its stored deadline");

#if SAFETY_SNAPSHOT_NATIVE
Check(PortraitSafetySnapshotNative.TryGetLayout(out var layout) && layout.FocusOffset == 0xA28
    && layout.FrameOffset == 0x16D0 && layout.EditorStateOffset == 0x28
    && layout.StateViewOffset == 0x3C8 && layout.MovingOffset == 0x5B08,
    "actual SDK named-field layout matches the reviewed focus, frame and owner chain");
var allocation = System.Runtime.InteropServices.Marshal.AllocHGlobal(128);
try
{
    System.Runtime.InteropServices.Marshal.WriteInt32(allocation, 0x12345678);
    Check(PortraitSafetySnapshotNative.TryRead<int>(allocation, out var copied) && copied == 0x12345678,
        "fallible current-process copy reads only caller-owned test memory");
    Check(PortraitSafetySnapshotNative.TryReadBytes(allocation, 4, out var bytes)
        && bytes.SequenceEqual(new byte[] { 0x78, 0x56, 0x34, 0x12 }),
        "byte copies preserve raw values without bool coercion");
    Check(!PortraitSafetySnapshotNative.TryReadBytes(allocation, 113, out _),
        "condition copy rejects unbounded lengths");
    Check(!PortraitSafetySnapshotNative.TryRead<int>(0, out _) && !PortraitSafetySnapshotNative.TryRead<int>(1, out _),
        "unreadable or absent native addresses fail without unchecked dereference");
}
finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(allocation); }
#else
Check(false, "fallible native focus, generation and condition copying is missing");
#endif

#if SAFETY_SNAPSHOT_FEATURE
var featureClock = TimeSpan.Zero;
var feature = new PortraitPreviewFeature(() => featureClock, () => new(true, true, 51));
Check(FireGaze.Plugin.Framework.Dispatches == 0 && feature.EditorReads == 0 && feature.Captures == 0,
    "feature collector initialization does not resolve native state or schedule a callback");
var actualSource = feature.Source();
using var hostRuntime = JsonDocument.Parse(actualSource.ReadRuntimeJSON());
Check(hostRuntime.RootElement.TryGetProperty("ownedNativePassiveSupported", out var actualOwnedCapability)
    && actualOwnedCapability.ValueKind == JsonValueKind.True,
    "actual feature runtime advertises mandatory owned native passive evidence support");
Check(hostRuntime.RootElement.GetProperty("location").GetString()
    == new SafetyPluginInterface().AssemblyLocation.FullName
    && hostRuntime.RootElement.GetProperty("location").GetString() != typeof(FireGaze.Plugin).Assembly.Location,
    "actual feature reports the host watched DLL path instead of a shadow-loaded assembly location");
FireGaze.Plugin.Framework.IsInFrameworkUpdateThread = true;
var freshEditor = actualSource.ReadEditor();
Check(freshEditor.Availability == "Ready" && feature.EditorReads == 1 && feature.Captures == 1
    && freshEditor.EditorStateJSON!.Contains("fresh-target-fixture", StringComparison.Ordinal),
    "actual feature adapter resolves and captures a fresh full state through its audited target");
feature.SetMutationCounters(52);
Check(feature.ConfigureLease(), "feature fixture establishes the original lease once");
featureClock = TimeSpan.FromSeconds(61);
using var managedRead = JsonDocument.Parse(actualSource.ReadManagedJSON());
var currentManaged = managedRead.RootElement;
Check(currentManaged.TryGetProperty("ownedPassive", out var ownedUnknown)
    && !ownedUnknown.GetProperty("known").GetBoolean(),
    "actual adapter reports an absent owned passive controller as unknown rather than manufacturing idle");
Check(currentManaged.TryGetProperty("editorOpening", out var openingReceipt)
    && openingReceipt.GetProperty("status").GetString() == "NotExecuted"
    && !openingReceipt.GetProperty("mayMutate").GetBoolean() && !openingReceipt.GetProperty("mayClose").GetBoolean(),
    "actual formal adapter includes a detached opening receipt without promoting ownership to action permission");
feature.Opening.Invalidate("Feature lifecycle test");
using var revokedOpening = JsonDocument.Parse(actualSource.ReadManagedJSON());
Check(revokedOpening.RootElement.GetProperty("editorOpening").GetProperty("status").GetString() == "Invalidated"
    && revokedOpening.RootElement.GetProperty("editorOpening").GetProperty("epoch").GetInt64() > openingReceipt.GetProperty("epoch").GetInt64(),
    "actual formal adapter reflects explicit receipt invalidation without progressing it during sampling");
Check(currentManaged.TryGetProperty("sourceCensus", out var censusUnknown)
    && !censusUnknown.GetProperty("known").GetBoolean(),
    "uninitialized actual controller is unknown rather than manufactured idle");
feature.CensusSafety = new(true, "Retiring", 3, true, false, new("Retiring", 4, 1, true, false),
    new("Retired", 0, 0, false, false), new("Retired", 0, 0, false, false));
using var physicalManaged = JsonDocument.Parse(actualSource.ReadManagedJSON());
Check(physicalManaged.RootElement.GetProperty("sourceCensus").GetProperty("source").GetProperty("inFlight").GetInt32() == 1
    && physicalManaged.RootElement.GetProperty("sourceCensus").GetProperty("blocksOperations").GetBoolean()
    && !physicalManaged.RootElement.GetProperty("sourceCensus").GetProperty("physicalDrainCompleted").GetBoolean(),
    "actual formal adapter serializes physical tail instead of treating logical stop as idle");
feature.OwnedSafety = new(true, "Retiring", 4, 3, true, false, 39, 1, true, false);
using var ownedPhysicalManaged = JsonDocument.Parse(actualSource.ReadManagedJSON());
var ownedPhysical = ownedPhysicalManaged.RootElement.GetProperty("ownedPassive");
Check(ownedPhysical.GetProperty("attempt").GetInt64() == 4 && ownedPhysical.GetProperty("censusAttempt").GetInt64() == 3
    && ownedPhysical.GetProperty("hooks").GetInt32() == 39 && ownedPhysical.GetProperty("inFlight").GetInt32() == 1
    && ownedPhysical.GetProperty("reservationHeld").GetBoolean() && ownedPhysical.GetProperty("blocksOperations").GetBoolean()
    && !ownedPhysical.GetProperty("physicalDrainCompleted").GetBoolean() && !ownedPhysical.GetProperty("productionAdmission").GetBoolean(),
    "actual formal adapter independently preserves owned attempt, complete hook batch, reservation and physical tail");
Check(currentManaged.GetProperty("captureIsolation").GetString() == "off"
    && feature.Lease.ConfiguredMode == "history" && feature.Lease.ExpiresAt == TimeSpan.FromSeconds(60),
    "actual adapter uses Peek without expiring or refreshing the stored lease");
Check(currentManaged.GetProperty("historyPhotos").GetProperty("busy").GetBoolean()
    && !currentManaged.GetProperty("historyPhotos").GetProperty("capturing").GetBoolean(),
    "actual adapter retains maintenance busy separately from capture activity");
Check(currentManaged.GetProperty("light").GetProperty("token").GetInt64() == 52
    && currentManaged.GetProperty("light").GetProperty("lastToken").GetInt64() == 52
    && currentManaged.GetProperty("light").GetProperty("pointerToken").GetInt64() == 52
    && currentManaged.GetProperty("worldTrials").GetProperty("revision").GetInt64() == 51,
    "current light ownership and sibling runtime evidence are copied without reserving tokens");
var secondManaged = actualSource.ReadManagedJSON();
Check(secondManaged == actualSource.ReadManagedJSON(), "repeat managed sampling does not advance leases, controls or tasks");
FireGaze.Plugin.Framework.IsInFrameworkUpdateThread = false;
using var dispatched = JsonDocument.Parse(feature.ReadSafetySnapshot());
Check(FireGaze.Plugin.Framework.Dispatches == 1 && feature.EditorReads == 3 && feature.Captures == 3
    && dispatched.RootElement.GetProperty("status").GetString() == "Partial",
    "public adapter dispatches one callback and cannot manufacture complete evidence without game roots");
FireGaze.Plugin.Framework.IsInFrameworkUpdateThread = true;
_ = feature.ReadSafetySnapshot();
Check(FireGaze.Plugin.Framework.Dispatches == 1, "an existing Framework caller is never redispatched");
feature.SetDisposed();
var callsBeforeDisposal = feature.EditorReads;
using var disposedFeature = JsonDocument.Parse(feature.ReadSafetySnapshot());
Check(disposedFeature.RootElement.GetProperty("status").GetString() == "Disposed"
    && feature.EditorReads == callsBeforeDisposal && FireGaze.Plugin.Framework.Dispatches == 1,
    "disposed public adapter performs no scheduling or editor reads");
var missingSibling = new PortraitPreviewFeature();
using var missingSiblingJSON = JsonDocument.Parse(missingSibling.Source().ReadManagedJSON());
Check(!missingSiblingJSON.RootElement.GetProperty("worldTrials").GetProperty("known").GetBoolean(),
    "actual adapter reports missing sibling runtime as unknown rather than no active trials");
#else
Check(false, "actual one-Framework feature adapter is missing");
#endif

SafetyTrialRuntimeRegression.Run(Check);

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class FakeSource : IPortraitSafetySnapshotSource
{
    public bool Framework = true;
    public bool Disposed;
    public bool Closed;
    public string? ThrowAt;
    public readonly List<string> Calls = [];
    public readonly int[] Flags = Enumerable.Range(0, 112).Select(x => x == 1 ? 1 : 0).ToArray();
    public Func<PortraitSafetyBoundary, PortraitSafetyBoundary>? BoundaryOverride;
    public Func<PortraitSafetyBoundary, PortraitSafetyBoundary>? AfterOverride;
    public Func<PortraitSafetyEditorSample, PortraitSafetyEditorSample>? EditorOverride;
    public Func<PortraitSafetyEditorSample, PortraitSafetyEditorSample>? ConfirmationOverride;
    public string ManagedBefore = Managed(false);
    public string? ManagedAfter;
    public string Version = "1.5.0.37";
    public JsonNode? OwnedCapability;
    private int boundaries, editors, managed;
    public bool IsFrameworkThread => Framework;
    public bool IsDisposed => Disposed;
    void Call(string name) { Calls.Add(name); if (ThrowAt == name) throw new InvalidOperationException(); }
    public long Timestamp() { Call("clock"); return Calls.Count; }
    public string ReadRuntimeJSON()
    {
        Call("runtime");
        var runtime = JsonSerializer.SerializeToNode(new { version = Version, mvid = "mvid-current", instanceID = "instance-current" })!.AsObject();
        if (OwnedCapability is not null) runtime["ownedNativePassiveSupported"] = OwnedCapability.DeepClone();
        return runtime.ToJsonString();
    }
    public PortraitSafetyBoundary ReadBoundary()
    {
        Call("boundary");
        var value = new PortraitSafetyBoundary(10, 42, 1, 100, 91, 200, 112, Flags,
            new(777, 423, 1, 2, 3, 4, 1000, false, false, false, 0), 300, Closed ? 0 : 400, Closed ? 0 : 500);
        value = BoundaryOverride?.Invoke(value) ?? value;
        return boundaries++ == 0 ? value : AfterOverride?.Invoke(value) ?? value;
    }
    public PortraitSafetyEditorSample ReadEditor()
    {
        Call("editor");
        var value = Closed ? new PortraitSafetyEditorSample("Closed", null, null, null, null, null)
            : new PortraitSafetyEditorSample("Ready", new(300, 400, 500, 9, 600), "Gearset", State(1), "{\"ContentID\":1,\"SourceID\":0}", 99);
        value = EditorOverride?.Invoke(value) ?? value;
        return editors++ == 0 ? value : ConfirmationOverride?.Invoke(value) ?? value;
    }
    public string ReadManagedJSON() { Call("managed"); return managed++ == 0 ? ManagedBefore : ManagedAfter ?? ManagedBefore; }
    public static string Managed(bool active) => JsonSerializer.Serialize(new
    {
        active = true, canBlockSave = true, syncPlate = false, plateMirrorActive = false, plateMirrorClosePending = false,
        historyWindowOpen = false, historyPhotosView = false, captureIsolation = "off",
        historyPhotos = new { capturing = false, busy = false }, liveCapture = new { busy = false },
        lightingFit = new { historySuppressed = false, mutationBlocked = false, nativeWritePending = false, physicalCapturePending = false },
        light = new { showLight = false, pointerToken = 0L, serial = 0L, token = 0L, lastToken = 0L },
        worldTrials = new { known = true, active },
    });
    public static string State(int marker) => new PortraitEditorSnapshot
    {
        GameBuild = "reviewed", EntryPoint = 1,
        Customize = Enumerable.Range(0, 26).Select(value => (byte)value).ToArray(),
        Composition = new() { CameraYaw = marker, AnimationProgress = marker / 10f },
    }.ToJSON();
}
#endif
