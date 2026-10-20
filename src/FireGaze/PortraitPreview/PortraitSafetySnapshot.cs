using System.Text.Json;
using System.Text.Json.Nodes;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitSafetyPlayer(ulong GameObjectID, uint TerritoryID, float X, float Y, float Z,
    uint JobID, uint CurrentHP, bool? InCombat, bool? IsMoving, bool? IsCasting, uint? CastActionID);

internal sealed record PortraitSafetyBoundary(long FrameworkAddress, uint? Frame, int? FocusByte,
    long SourceAddress, uint SourceGeneration, long ConditionAddress, int ConditionMaxEntries,
    int[]? Conditions, PortraitSafetyPlayer? Player, long EditorAgent, long EditorState, long EditorView)
{
    public bool? Focused => FocusByte is 0 ? false : FocusByte is 1 ? true : null;
    public int[]? ActiveFlags => Conditions?.Select((value, index) => (value, index))
        .Where(value => value.value == 1).Select(value => value.index).ToArray();
    public PortraitSafetyBoundary Detached() => this with { Conditions = Conditions?.ToArray() };
}

internal sealed record PortraitSafetyEditorIdentity(long Editor, long State, long Model, uint Generation, ulong Character);
internal sealed record PortraitSafetyEditorSample(string Availability, PortraitSafetyEditorIdentity? Identity,
    string? OpenType, string? EditorStateJSON, string? HistoryKeyJSON, long? CurrentHistoryID);
internal readonly record struct PortraitSafetyTrialState(bool Known, bool Active, long Revision);

/// <summary>All methods execute inside one existing Framework callback. No action/mutation route is exposed.</summary>
internal interface IPortraitSafetySnapshotSource
{
    bool IsFrameworkThread { get; }
    bool IsDisposed { get; }
    long Timestamp();
    string ReadRuntimeJSON();
    PortraitSafetyBoundary ReadBoundary();
    PortraitSafetyEditorSample ReadEditor();
    string ReadManagedJSON();
}

/// <summary>
/// Produces detached evidence, not permission to mutate. Complete means all required evidence was
/// read consistently; a complete observation may still report unfocused, moving or busy and must
/// be rejected by the caller's exact runtime/baseline/ownership/deadline guard.
/// </summary>
internal sealed class PortraitSafetySnapshotCollector(IPortraitSafetySnapshotSource source)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] CompositionFields = JsonSerializer.SerializeToElement(new PortraitComposition())
        .EnumerateObject().Select(property => property.Name).ToArray();

    public string ReadJSON(long sequence)
    {
        if (source.IsDisposed) return Unavailable("Disposed", "disposed", sequence);
        if (!source.IsFrameworkThread) return Unavailable("Unavailable", "not-framework", sequence);
        try
        {
            var started = source.Timestamp();
            var runtime = Parse(source.ReadRuntimeJSON());
            var before = source.ReadBoundary().Detached();
            var editor = Detach(source.ReadEditor());
            var managed = Parse(source.ReadManagedJSON());
            var editorConfirmation = Detach(source.ReadEditor());
            var managedConfirmation = Parse(source.ReadManagedJSON());
            var after = source.ReadBoundary().Detached();
            var ended = source.Timestamp();
            var errors = new HashSet<string>(StringComparer.Ordinal);
            ValidateBoundary(before, errors);
            ValidateBoundary(after, errors);
            if (JsonSerializer.Serialize(before, Options) != JsonSerializer.Serialize(after, Options)) errors.Add("boundary-changed");
            ValidateEditor(editor, before, errors);
            ValidateEditor(editorConfirmation, after, errors);
            if (JsonSerializer.Serialize(editor, Options) != JsonSerializer.Serialize(editorConfirmation, Options)) errors.Add("editor-changed");
            ValidateManaged(managed, errors);
            ValidateManaged(managedConfirmation, errors);
            ValidateSourceCensus(managed, runtime, errors);
            ValidateSourceCensus(managedConfirmation, runtime, errors);
            ValidateOwnedPassive(managed, runtime, errors);
            ValidateOwnedPassive(managedConfirmation, runtime, errors);
            if (StableManaged(managed) != StableManaged(managedConfirmation)) errors.Add("managed-changed");
            if (!HasString(runtime, "version") || !HasString(runtime, "mvid") || !HasString(runtime, "instanceID")) errors.Add("runtime-unavailable");
            if (ended < started || sequence <= 0) errors.Add("timing-unavailable");
            if (source.IsDisposed || !source.IsFrameworkThread) errors.Add("source-became-unavailable");
            return JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status = errors.Count == 0 ? "Complete" : "Partial", sequence,
                startedTimestamp = started, completedTimestamp = ended,
                timestampFrequency = System.Diagnostics.Stopwatch.Frequency,
                unavailableReasons = errors.Order(StringComparer.Ordinal).ToArray(), runtime, before, after,
                editor, editorConfirmation, managed, managedConfirmation,
            }, Options);
        }
        catch (Exception exception)
        {
            // No old state is retained, and no cleanup/cancel/read retry is issued here.
            return Unavailable("Partial", "sampling-failed:" + exception.GetType().Name, sequence);
        }
    }

    internal static string Unavailable(string status, string reason, long sequence = 0)
        => JsonSerializer.Serialize(new { schemaVersion = 1, status, sequence, unavailableReasons = new[] { reason } }, Options);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Object evidence required");
        return document.RootElement.Clone();
    }

    private static DetachedEditor Detach(PortraitSafetyEditorSample value)
        => new(value.Availability, value.Identity, value.OpenType,
            value.EditorStateJSON is null ? null : Parse(value.EditorStateJSON),
            value.HistoryKeyJSON is null ? null : Parse(value.HistoryKeyJSON), value.CurrentHistoryID);

    private static void ValidateBoundary(PortraitSafetyBoundary value, ISet<string> errors)
    {
        if (value.FrameworkAddress <= 0 || value.Frame is null) errors.Add("framework-root-unavailable");
        if (value.Focused is null) errors.Add("focus-unknown");
        if (value.SourceAddress <= 0 || value.SourceGeneration == 0 || value.Player is not { } player
            || player.GameObjectID == 0 || !float.IsFinite(player.X) || !float.IsFinite(player.Y) || !float.IsFinite(player.Z)
            || player.InCombat is null || player.IsMoving is null || player.IsCasting is null || player.CastActionID is null)
            errors.Add("source-unavailable");
        if (value.ConditionAddress <= 0 || value.ConditionMaxEntries != 112 || value.Conditions is not { Length: 112 }
            || value.Conditions.Any(flag => flag is not (0 or 1))) errors.Add("conditions-unavailable");
        if (value.EditorAgent <= 0 || value.EditorState < 0 || value.EditorView < 0) errors.Add("editor-root-unavailable");
    }

    private static void ValidateEditor(DetachedEditor value, PortraitSafetyBoundary boundary, ISet<string> errors)
    {
        if (value.Availability == "Closed" && boundary.EditorAgent > 0 && boundary.EditorState == 0
            && boundary.EditorView == 0 && value.Identity is null && value.EditorState is null) return;
        if (value.Availability != "Ready" || value.Identity is not { } identity
            || identity.Editor <= 0 || identity.State <= 0 || identity.Model <= 0 || identity.Character == 0 || identity.Generation == 0
            || identity.Character == (ulong)boundary.SourceAddress
            || new[] { (ulong)identity.Editor, (ulong)identity.State, (ulong)identity.Model, identity.Character }.Distinct().Count() != 4
            || boundary.EditorAgent != identity.Editor || boundary.EditorState != identity.State || boundary.EditorView != identity.Model
            || value.EditorState is not { } state || !HasStateEnvelope(state) || value.HistoryKey is null)
            errors.Add("editor-unavailable");
    }

    private static bool HasStateEnvelope(JsonElement value)
    {
        // This is the actual snapshot codec, including finite composition values, 13 equipment
        // pieces and facewear range. byte[] Customize is base64 in System.Text.Json, not an array.
        // Explicit presence checks prevent omitted fields from silently becoming valid defaults.
        return value.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var schema) && schema == 1
            && HasString(value, "GameBuild") && value.TryGetProperty("EntryPoint", out var entry) && entry.ValueKind == JsonValueKind.Number && entry.TryGetByte(out _)
            && HasBoolean(value, "HideVieraEars")
            && value.TryGetProperty("Look", out var look) && look.ValueKind == JsonValueKind.Object
            && HasBoolean(look, "HeadVisible") && HasBoolean(look, "WeaponVisible") && HasBoolean(look, "Visor")
            && look.TryGetProperty("Pieces", out var pieces) && pieces.ValueKind == JsonValueKind.Array && pieces.GetArrayLength() == 13
            && pieces.EnumerateArray().All(piece => HasInteger(piece, "ItemID") && HasInteger(piece, "Stain0") && HasInteger(piece, "Stain1"))
            && value.TryGetProperty("Customize", out var customize) && customize.ValueKind == JsonValueKind.String
            && customize.TryGetBytesFromBase64(out var bytes) && bytes.Length == 26
            && value.TryGetProperty("Composition", out var composition) && composition.ValueKind == JsonValueKind.Object
            && CompositionFields.All(name => composition.TryGetProperty(name, out _))
            && PortraitEditorSnapshot.FromJSON(value.GetRawText()) is { IsValid: true };
    }

    private static void ValidateManaged(JsonElement value, ISet<string> errors)
    {
        if (!value.TryGetProperty("worldTrials", out var trials) || trials.ValueKind != JsonValueKind.Object
            || !trials.TryGetProperty("known", out var known) || known.ValueKind != JsonValueKind.True
            || !HasBoolean(trials, "active")) errors.Add("world-trials-unknown");
        if (!HasBoolean(value, "active") || !HasBoolean(value, "canBlockSave") || !HasBoolean(value, "syncPlate")
            || !HasBoolean(value, "plateMirrorActive") || !HasBoolean(value, "plateMirrorClosePending")
            || !HasBoolean(value, "historyWindowOpen") || !HasBoolean(value, "historyPhotosView")
            || !value.TryGetProperty("historyPhotos", out var photos) || !HasBoolean(photos, "capturing") || !HasBoolean(photos, "busy")
            || !value.TryGetProperty("liveCapture", out var live) || !HasBoolean(live, "busy")
            || !value.TryGetProperty("lightingFit", out var fit) || !HasBoolean(fit, "historySuppressed")
            || !HasBoolean(fit, "mutationBlocked") || !HasBoolean(fit, "nativeWritePending") || !HasBoolean(fit, "physicalCapturePending")
            || !value.TryGetProperty("light", out var light) || !HasBoolean(light, "showLight")
            || !HasInteger(light, "pointerToken") || !HasInteger(light, "serial") || !HasInteger(light, "token") || !HasInteger(light, "lastToken")
            || !HasString(value, "captureIsolation")) errors.Add("managed-unavailable");
    }

    private static void ValidateSourceCensus(JsonElement managed, JsonElement runtime, ISet<string> errors)
    {
        var required = runtime.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
            && Version.TryParse(version.GetString(), out var parsed) && parsed >= new Version(1, 5, 0, 42);
        if (!managed.TryGetProperty("sourceCensus", out var census))
        { if (required) errors.Add("source-census-unavailable"); return; }
        if (!ValidSourceCensus(census)) errors.Add("source-census-unavailable");
    }

    private static bool ValidSourceCensus(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("known", out var known) || known.ValueKind != JsonValueKind.True
            || !value.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String
            || state.GetString() is not ("Off" or "ObservingThread" or "Armed" or "Retiring" or "Faulted")
            || !value.TryGetProperty("attempt", out var attempt) || !attempt.TryGetInt64(out var number) || number < 0
            || !HasBoolean(value, "blocksOperations") || !HasBoolean(value, "physicalDrainCompleted")
            || value.GetProperty("blocksOperations").GetBoolean() == value.GetProperty("physicalDrainCompleted").GetBoolean()
            || !value.TryGetProperty("productionAdmission", out var admission) || admission.ValueKind != JsonValueKind.False) return false;
        var drained = value.GetProperty("physicalDrainCompleted").GetBoolean();
        if (number == 0)
            return state.GetString() == "Off" && drained && new[] { "source", "counters", "resources" }
                .All(name => value.TryGetProperty(name, out var host) && host.ValueKind == JsonValueKind.Null);
        if (drained && state.GetString() != "Off") return false;
        foreach (var name in new[] { "source", "counters", "resources" })
        {
            if (!value.TryGetProperty(name, out var host) || host.ValueKind != JsonValueKind.Object
                || !host.TryGetProperty("state", out var hostState) || hostState.ValueKind != JsonValueKind.String
                || hostState.GetString() is not ("Off" or "Observing" or "Installed" or "Retiring" or "Retired" or "Faulted")
                || !HasInteger(host, "hooks") || !HasInteger(host, "inFlight")
                || !HasBoolean(host, "reservationHeld") || !HasBoolean(host, "failed")) return false;
            if (drained && (hostState.GetString() != "Retired" || host.GetProperty("hooks").GetInt64() != 0
                || host.GetProperty("inFlight").GetInt64() != 0 || host.GetProperty("reservationHeld").GetBoolean()
                || host.GetProperty("failed").GetBoolean())) return false;
        }
        return true;
    }

    private static void ValidateOwnedPassive(JsonElement managed, JsonElement runtime, ISet<string> errors)
    {
        var required = false;
        if (runtime.TryGetProperty("ownedNativePassiveSupported", out var capability))
        {
            if (capability.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                errors.Add("owned-passive-unavailable");
            else required = capability.GetBoolean();
        }
        if (!managed.TryGetProperty("ownedPassive", out var passive))
        { if (required) errors.Add("owned-passive-unavailable"); return; }
        if (!ValidOwnedPassive(passive)) errors.Add("owned-passive-unavailable");
    }

    private static bool ValidOwnedPassive(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("known", out var known) || known.ValueKind != JsonValueKind.True
            || !value.TryGetProperty("productionAdmission", out var admission) || admission.ValueKind != JsonValueKind.False
            || !value.TryGetProperty("state", out var stateValue) || stateValue.ValueKind != JsonValueKind.String
            || stateValue.GetString() is not ("Off" or "WaitingForCensusDrain" or "Installing" or "WaitingForSourceReturn" or "Retiring" or "Faulted")
            || !NonnegativeInt64(value, "attempt", out var attempt) || !NonnegativeInt64(value, "censusAttempt", out var censusAttempt)
            || !NonnegativeInt32(value, "hooks", out var hooks) || hooks > 39
            || !NonnegativeInt32(value, "inFlight", out var inFlight)
            || !HasBoolean(value, "blocksOperations") || !HasBoolean(value, "physicalDrainCompleted")
            || !HasBoolean(value, "reservationHeld") || !HasBoolean(value, "failed")) return false;
        var state = stateValue.GetString();
        var drained = value.GetProperty("physicalDrainCompleted").GetBoolean();
        var blocks = value.GetProperty("blocksOperations").GetBoolean();
        var reservation = value.GetProperty("reservationHeld").GetBoolean();
        var failed = value.GetProperty("failed").GetBoolean();
        if (blocks == drained || drained != (state == "Off") || failed != (state == "Faulted")
            || (attempt == 0 ? state != "Off" || censusAttempt != 0 : censusAttempt <= 0)
            || hooks == 0 && inFlight != 0 || hooks > 0 && !reservation) return false;
        return state switch
        {
            "Off" or "WaitingForCensusDrain" => hooks == 0 && inFlight == 0 && !reservation,
            "WaitingForSourceReturn" => hooks == 39 && reservation,
            _ => true,
        };
    }

    private static bool NonnegativeInt64(JsonElement value, string name, out long number)
    {
        number = 0;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out number) && number >= 0;
    }

    private static bool NonnegativeInt32(JsonElement value, string name, out int number)
    {
        number = 0;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out number) && number >= 0;
    }

    private static string StableManaged(JsonElement value)
    {
        var copy = JsonNode.Parse(value.GetRawText())!.AsObject();
        // Remaining time is observed, never refreshed. Only its naturally advancing clock is
        // excluded from equality; configured mode, effective mode and deadline remain compared.
        if (copy["captureLease"] is JsonObject lease)
        {
            lease.Remove("clockTicks");
            lease.Remove("remainingTicks");
        }
        return copy.ToJsonString();
    }

    private static bool HasString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(property.GetString());
    private static bool HasBoolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool HasInteger(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number) && number >= 0;
    private sealed record DetachedEditor(string Availability, PortraitSafetyEditorIdentity? Identity, string? OpenType,
        JsonElement? EditorState, JsonElement? HistoryKey, long? CurrentHistoryID);
}
