using System.Diagnostics;
using System.Globalization;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private sealed record DevelopmentCameraRequest(Guid Nonce, long PreparedAt, long SessionRevision,
        OwnedCharacterKey Owner, PortraitLightingSpaceBinding Binding);

    private sealed record DevelopmentCameraReceipt
    {
        public string Status { get; init; } = "Idle";
        public string Stage { get; init; } = "idle";
        public string? Nonce { get; init; }
        public string? Reason { get; init; }
        public long? SessionRevision { get; init; }
        public uint? Generation { get; init; }
        public ulong? ContentID { get; init; }
        public string? BeforeStateKey { get; init; }
        public string? AfterStateKey { get; init; }
        public float? BeforeYaw { get; init; }
        public float? AfterYaw { get; init; }
        public float? DeltaDegrees { get; init; }
        public bool NativeWriteAttempted { get; init; }
        public bool NativeWriteAccepted { get; init; }
        public bool PostVerified { get; init; }
    }

    private DevelopmentCameraRequest? developmentCameraRequest;
    private DevelopmentCameraReceipt developmentCameraReceipt = new();
    // Never evict a used nonce and make it replayable. Refuse further development requests
    // at this bounded per-instance limit; a new plugin instance starts a separate authority.
    private readonly HashSet<Guid> developmentCameraNonces = [];

    private bool TryDevelopmentCameraCommand(string[] parts)
    {
        if (parts.Length == 0 || parts[0] != "dev-native-camera") return false;
        // Keeping census ownership intact is essential: an active diagnostic is refused,
        // not implicitly stopped to make the development mutation appear admissible.
        Mutate(() => ExecuteDevelopmentCameraCommand(parts), preserveSourceCensus: true);
        return true;
    }

    private void ExecuteDevelopmentCameraCommand(string[] parts)
    {
        var rawNonce = parts.Length > 2 ? parts[2] : null;
        var validNonce = Guid.TryParseExact(rawNonce, "D", out var nonce) && nonce != Guid.Empty;
        developmentCameraReceipt = new() { Nonce = validNonce ? nonce.ToString("D") : null, Stage = "parse" };
        if (!validNonce)
        {
            developmentCameraRequest = null;
            RejectDevelopmentCamera("InvalidNonce", "parse");
            return;
        }

        if (parts is ["dev-native-camera", "prepare", _])
        {
            developmentCameraRequest = null;
            if (developmentCameraNonces.Count >= 1024 || !developmentCameraNonces.Add(nonce))
            { RejectDevelopmentCamera("NonceUnavailable", "parse"); return; }
            if (DevelopmentCameraGateFailure() is { } blocked)
            { RejectDevelopmentCamera(blocked, "gate"); return; }
            var revision = session.Revision;
            if (!TryReadDevelopmentCameraSource(out var owner, out var identity, out var key, out var before)
                || !PortraitLightingSpaceBinding.TryCreate(identity, key, before, out var binding)
                || session.Revision != revision)
            { RejectDevelopmentCamera("SourceUnavailable", "binding"); return; }
            developmentCameraRequest = new(nonce, Stopwatch.GetTimestamp(), revision, owner, binding!);
            developmentCameraReceipt = developmentCameraReceipt with
            {
                Status = "Prepared", Stage = "ready", SessionRevision = revision,
                Generation = identity.Generation, ContentID = owner.ContentID,
                BeforeStateKey = binding!.StateKey, BeforeYaw = before.Composition.CameraYaw,
            };
            return;
        }

        var request = developmentCameraRequest;
        // Every attempted execution consumes the pending authority, including a malformed
        // delta or a newly blocked game state. Retrying requires a fresh bind and nonce.
        developmentCameraRequest = null;
        if (parts is not ["dev-native-camera", "yaw", _, var rawDegrees])
        { RejectDevelopmentCamera("InvalidCommand", "parse"); return; }
        if (request is null || request.Nonce != nonce)
        { RejectDevelopmentCamera("NotPrepared", "binding"); return; }
        developmentCameraReceipt = developmentCameraReceipt with
        {
            SessionRevision = request.SessionRevision, Generation = request.Binding.Identity.Generation,
            ContentID = request.Owner.ContentID, BeforeStateKey = request.Binding.StateKey,
            BeforeYaw = request.Binding.Composition.CameraYaw,
        };
        var age = Stopwatch.GetElapsedTime(request.PreparedAt);
        if (age < TimeSpan.Zero || age >= TimeSpan.FromSeconds(5))
        { RejectDevelopmentCamera("Expired", "binding"); return; }
        if (!float.TryParse(rawDegrees, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees)
            || !float.IsFinite(degrees) || MathF.Abs(degrees) is < 1f or > 5f)
        { RejectDevelopmentCamera("InvalidDelta", "parse"); return; }
        developmentCameraReceipt = developmentCameraReceipt with { DeltaDegrees = degrees };
        if (DevelopmentCameraGateFailure() is { } failure)
        { RejectDevelopmentCamera(failure, "gate"); return; }
        if (session.Revision != request.SessionRevision
            || !TryReadDevelopmentCameraSource(out var currentOwner, out var currentIdentity, out var currentKey, out var current)
            || currentOwner != request.Owner || !request.Binding.Matches(currentIdentity, currentKey, current))
        { RejectDevelopmentCamera("SourceChanged", "binding"); return; }

        var composition = current.Composition;
        var yaw = MathF.IEEERemainder(composition.CameraYaw + degrees * (MathF.PI / 180f), MathF.Tau);
        var horizontalDistance = MathF.Cos(composition.CameraPitch) * composition.CameraDistance;
        var desired = composition with
        {
            CameraYaw = yaw,
            CameraPosition = new(
                composition.CameraTarget.X + MathF.Sin(yaw) * horizontalDistance,
                composition.CameraTarget.Y - MathF.Sin(composition.CameraPitch) * composition.CameraDistance,
                composition.CameraTarget.Z + MathF.Cos(yaw) * horizontalDistance,
                composition.CameraPosition.W),
        };
        // Do not use the artistic reference camera's From(), which clamps distance/pitch.
        if (!PortraitLightingSpace.TryCreateNativeCandidate(composition, desired, out desired))
        { RejectDevelopmentCamera("CameraOutsideNativeDomain", "binding"); return; }
        developmentCameraReceipt = developmentCameraReceipt with { Stage = "write", NativeWriteAttempted = true };
        try
        {
            if (!target.SetLightingSpace(currentIdentity, current, desired))
            {
                developmentCameraReceipt = developmentCameraReceipt with
                { Status = "NativeWriteRejected", Reason = "NativeRejected" };
                return;
            }
            developmentCameraReceipt = developmentCameraReceipt with { Stage = "postverify", NativeWriteAccepted = true };
            if (session.Revision != request.SessionRevision || !session.BlockSave(currentIdentity)
                || !target.CanBlockSave || !TryReadDevelopmentCameraSource(out var afterOwner, out var afterIdentity, out var afterKey, out var after)
                || afterOwner != request.Owner || afterIdentity != currentIdentity || afterKey != currentKey
                || !PortraitLightingSpaceBinding.MatchesSnapshot(current with { Composition = desired }, after)
                || !PortraitLightingSpaceBinding.TryCreate(afterIdentity, afterKey, after, out var afterBinding))
            {
                developmentCameraReceipt = developmentCameraReceipt with { Status = "PostVerifyFailed", Reason = "SourceChangedAfterWrite" };
                return;
            }
            developmentCameraReceipt = developmentCameraReceipt with
            {
                Status = "Applied", Stage = "complete", AfterYaw = after.Composition.CameraYaw,
                AfterStateKey = afterBinding!.StateKey, PostVerified = true,
            };
            // Deliberately leave observedEditor, historyObservedEditor, session.Current and
            // history untouched. The ordinary Framework sampler must discover this edit.
        }
        catch (Exception exception)
        {
            developmentCameraReceipt = developmentCameraReceipt with
            { Status = "PostVerifyFailed", Reason = "NativeException:" + exception.GetType().Name };
        }
    }

    private string? DevelopmentCameraGateFailure()
    {
        // This is always dev-only, including OWNED_SLOT builds.
        if (!pluginInterface.IsDev) return "DevelopmentSlotRequired";
        if (!Plugin.Framework.IsInFrameworkUpdateThread) return "FrameworkRequired";
        if (disposed) return "Disposed";
        if (SourceCensusBlocksOperations) return "DiagnosticActive";
        if (!session.IsActive) return "PreviewInactive";
        if (!target.CanBlockSave) return "SaveProtectionUnavailable";
        if (LightingFitMutationBlocked || LightingFitHistorySuppressed || lightDrag.IsActive) return "LightingOperationActive";
        if (pointerHeld || sampledPointer || sampledKeyboard) return "InputActive";
        for (var key = 8; key < 255; key++)
            if (Plugin.Keys.IsVirtualKeyValid(key) && Plugin.Keys[key]) return "InputActive";
        if (productPhotoPending || photos.IsBusy || !photos.IsPhysicallyIdle
            || liveImageService?.IsBusy == true || liveImageService?.IsPhysicallyIdle == false
            || lightingFitCapture?.IsBusy == true || lightingFitCapture?.IsPhysicallyIdle == false)
            return "CaptureNotDrained";
        return null;
    }

    private bool TryReadDevelopmentCameraSource(out OwnedCharacterKey owner, out PortraitIdentity identity,
        out PortraitHistoryKey key, out PortraitEditorSnapshot snapshot)
    {
        owner = default; identity = default; key = default; snapshot = null!;
        if (!Plugin.Framework.IsInFrameworkUpdateThread || CurrentCharacter() is not { } currentOwner
            || !target.TryResolve(out var currentIdentity, out _, out _) || currentIdentity.Generation == 0
            || !session.BlockSave(currentIdentity) || !target.IsCurrent(currentIdentity)
            || target.CaptureEditorState(currentIdentity) is not { IsValid: true } current
            || GetHistoryKey(currentIdentity, current) is not { } currentKey
            || currentKey.ContentID != currentOwner.ContentID || CurrentCharacter() != currentOwner
            || !target.IsCurrent(currentIdentity) || !session.BlockSave(currentIdentity)) return false;
        owner = currentOwner; identity = currentIdentity; key = currentKey; snapshot = current.Copy();
        return true;
    }

    private void RejectDevelopmentCamera(string reason, string stage)
        => developmentCameraReceipt = developmentCameraReceipt with { Status = "Rejected", Reason = reason, Stage = stage };

    // Explicit property names keep the diagnostic contract stable in both serializers.
    // Reading the receipt is detached and never reads native state or advances a request.
    private object ReadDevelopmentCameraReceipt() => new
    {
        schema = "firegaze-dev-native-camera-v1", status = developmentCameraReceipt.Status,
        stage = developmentCameraReceipt.Stage, nonce = developmentCameraReceipt.Nonce, reason = developmentCameraReceipt.Reason,
        devNativeMutation = true, uiCallbackSimulated = false, directHistoryCommit = false,
        sessionRevision = developmentCameraReceipt.SessionRevision, generation = developmentCameraReceipt.Generation,
        contentID = developmentCameraReceipt.ContentID, beforeStateKey = developmentCameraReceipt.BeforeStateKey,
        afterStateKey = developmentCameraReceipt.AfterStateKey, beforeYaw = developmentCameraReceipt.BeforeYaw,
        afterYaw = developmentCameraReceipt.AfterYaw, deltaDegrees = developmentCameraReceipt.DeltaDegrees,
        nativeWriteAttempted = developmentCameraReceipt.NativeWriteAttempted,
        nativeWriteAccepted = developmentCameraReceipt.NativeWriteAccepted, postVerified = developmentCameraReceipt.PostVerified,
    };
}
