using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private readonly Guid safetySnapshotInstance = Guid.NewGuid();
    private readonly Func<PortraitSafetyTrialState>? readWorldTrials;
    private long safetySnapshotSerial;

    /// <summary>
    /// Detached current evidence from one Framework callback. This does not authorize mutations,
    /// configure isolation, advance a task, or replace the caller's runtime and baseline guard.
    /// </summary>
    public string ReadSafetySnapshot()
    {
        if (disposed) return PortraitSafetySnapshotCollector.Unavailable("Disposed", "disposed");
        try
        {
            return OnFramework(() => new PortraitSafetySnapshotCollector(new SafetySnapshotSource(this))
                .ReadJSON(Interlocked.Increment(ref safetySnapshotSerial)));
        }
        catch (Exception exception)
        {
            return PortraitSafetySnapshotCollector.Unavailable("Unavailable", "dispatch-failed:" + exception.GetType().Name);
        }
    }

    private sealed class SafetySnapshotSource(PortraitPreviewFeature feature) : IPortraitSafetySnapshotSource
    {
        public bool IsFrameworkThread => Plugin.Framework.IsInFrameworkUpdateThread;
        public bool IsDisposed => feature.disposed;
        public long Timestamp() => Stopwatch.GetTimestamp();

        public string ReadRuntimeJSON()
        {
            var assembly = typeof(Plugin).Assembly;
            var sdk = typeof(NativeFramework).Assembly;
            return JsonSerializer.Serialize(new
            {
                version = assembly.GetName().Version?.ToString(), mvid = assembly.ManifestModule.ModuleVersionId,
                location = feature.pluginInterface.AssemblyLocation.FullName, instanceID = feature.safetySnapshotInstance,
                processID = Environment.ProcessId, isDev = feature.pluginInterface.IsDev,
                sdkVersion = sdk.GetName().Version?.ToString(), sdkMvid = sdk.ManifestModule.ModuleVersionId,
                gameBuild = feature.target.SnapshotGameBuild,
                focusSource = "Framework.CursorInputs.IsGameWindowFocused",
            });
        }

        public PortraitSafetyBoundary ReadBoundary()
        {
            if (!PortraitSafetySnapshotNative.TryGetLayout(out var layout)
                || !PortraitSafetySnapshotNative.TryRead((nint)NativeFramework.StaticAddressPointers.ppInstance, out nint framework)
                || framework <= 0)
                return new(0, null, null, 0, 0, 0, 0, null, null, 0, -1, -1);

            uint? frame = PortraitSafetySnapshotNative.TryRead<uint>(framework + layout.FrameOffset, out var rawFrame) ? rawFrame : null;
            int? focus = PortraitSafetySnapshotNative.TryRead<byte>(framework + layout.FocusOffset, out var rawFocus) ? rawFocus : null;
            var conditionAddress = Plugin.Conditions.Address;
            var conditionCount = Plugin.Conditions.MaxEntries;
            int[]? conditions = conditionCount == 112
                && PortraitSafetySnapshotNative.TryReadBytes(conditionAddress, conditionCount, out var rawConditions)
                ? rawConditions.Select(value => (int)value).ToArray() : null;

            var player = Plugin.Objects.LocalPlayer;
            var address = player?.Address ?? 0;
            var generation = PortraitSafetySnapshotNative.TryRead<uint>(address == 0 ? 0 : address + 0x74, out var rawGeneration)
                ? rawGeneration : 0;
            PortraitSafetyPlayer? source = null;
            if (player is not null && address > 0 && generation > 0)
            {
                var map = (nint)AgentMap.Instance();
                bool? moving = PortraitSafetySnapshotNative.TryRead<byte>(map <= 0 ? 0 : map + layout.MovingOffset, out var rawMoving)
                    && rawMoving is 0 or 1 ? rawMoving == 1 : null;
                bool? combat = conditions is not null && conditions[(int)ConditionFlag.InCombat] is 0 or 1
                    ? conditions[(int)ConditionFlag.InCombat] == 1 : null;
                var position = player.Position;
                source = new(player.GameObjectId, Plugin.ClientState.TerritoryType, position.X, position.Y, position.Z,
                    player.ClassJob.RowId, player.CurrentHp, combat, moving, player.IsCasting, player.CastActionId);
                if (Plugin.Objects.LocalPlayer?.Address != address
                    || !PortraitSafetySnapshotNative.TryRead<uint>(address + 0x74, out var confirmedGeneration)
                    || confirmedGeneration != generation) generation = 0;
            }
            var editor = ReadEditorRoots(layout);
            return new((long)framework, frame, focus, (long)address, generation, (long)conditionAddress,
                conditionCount, conditions, source, editor.Editor, editor.State, editor.View);
        }

        public PortraitSafetyEditorSample ReadEditor()
        {
            // Use the audited native getter path freshly. observedEditor and observedIdentity are
            // history/UI caches; neither can establish the current full editor state or ownership.
            if (feature.target.TryResolve(out var identity, out _, out var openType))
            {
                var captured = feature.target.CaptureEditorState(identity);
                if (captured is not { IsValid: true } || !feature.target.IsCurrent(identity)
                    || feature.GetHistoryKey(identity, captured) is not { } key || !feature.target.IsCurrent(identity))
                    return UnknownEditor();
                return new("Ready", new((long)identity.Editor, (long)identity.State, (long)identity.Model,
                    identity.Generation, identity.Character), openType.ToString(), captured.ToJSON(),
                    JsonSerializer.Serialize(key), feature.history.CurrentRecordID(key));
            }

            // Resolution failure also includes hidden/loading/replaced native owners. Only fresh
            // positive null-state evidence, with no live local session, can be called Closed.
            if (!feature.session.IsActive && PortraitSafetySnapshotNative.TryGetLayout(out var layout))
            {
                var roots = ReadEditorRoots(layout);
                if (roots.Editor > 0 && roots.State == 0 && roots.View == 0)
                    return new("Closed", null, null, null, null, null);
            }
            return UnknownEditor();
        }

        public string ReadManagedJSON()
        {
            var lease = feature.captureIsolation.Peek();
            var photoState = JsonSerializer.SerializeToNode(feature.photos.Diagnostics())!.AsObject();
            photoState["busy"] = feature.photos.IsBusy;
            var light = feature.window.ReadSafetyLightState();
            var trials = feature.readWorldTrials?.Invoke() ?? new(false, false, 0);
            return JsonSerializer.Serialize(new
            {
                active = feature.session.IsActive, canBlockSave = feature.target.CanBlockSave,
                nativeError = feature.target.LastError,
                blockedSaves = feature.target.BlockedSaves, blockedSaveEvents = feature.target.BlockedSaveEvents,
                syncPlate = feature.syncPlate, plateMirrorActive = feature.target.IsPlateMirrorActive,
                plateMirrorClosePending = feature.plateMirrorClosePending,
                historyCount = feature.history.Count, historyBytes = feature.history.Bytes,
                historyWindowOpen = feature.window.HistoryWindowOpen, historyPhotosView = feature.window.HistoryPhotosView,
                historyPhotos = photoState, liveCapture = feature.liveImageService?.Diagnostics,
                lightingFit = new
                {
                    busy = feature.LightingFitMutationBlocked, historySuppressed = feature.lightingFitHistorySuppressed,
                    mutationBlocked = feature.LightingFitMutationBlocked,
                    nativeWritePending = feature.lightingFitAdapter?.HasPendingCleanup ?? false,
                    physicalCapturePending = feature.lightingFitCapture?.IsBusy ?? false,
                    sessionActive = feature.lightingFitSession?.IsActive ?? false,
                    sessionPendingCleanup = feature.lightingFitSession?.HasPendingCleanup ?? false,
                    sessionPendingCapture = feature.lightingFitSession?.HasPendingCapture ?? false,
                    adapterPendingCapture = feature.lightingFitAdapter?.HasPendingCapture ?? false,
                },
                light = new
                {
                    showLight = light.ShowLight, pointerToken = light.PointerToken, serial = light.Serial,
                    token = feature.lightDrag.CurrentToken, lastToken = feature.lightDrag.LastToken,
                    inputToken = feature.lightInputToken,
                },
                worldTrials = new { known = trials.Known, active = trials.Active, revision = trials.Revision },
                captureIsolation = lease.EffectiveMode,
                captureLease = new
                {
                    configuredMode = lease.ConfiguredMode, expiresAtTicks = lease.ExpiresAt?.Ticks,
                    clockTicks = lease.Clock.Ticks, remainingTicks = lease.Remaining?.Ticks,
                },
                pointerHeld = feature.pointerHeld, sampledPointer = feature.sampledPointer, sampledKeyboard = feature.sampledKeyboard,
            });
        }

        private static PortraitSafetyEditorSample UnknownEditor() => new("Unknown", null, null, null, null, null);

        private static (long Editor, long State, long View) ReadEditorRoots(PortraitSafetySnapshotLayout layout)
        {
            var editor = (nint)AgentBannerEditor.Instance();
            if (editor <= 0) return (0, -1, -1);
            if (!PortraitSafetySnapshotNative.TryRead<nint>(editor + layout.EditorStateOffset, out var state))
                return ((long)editor, -1, -1);
            if (state == 0) return ((long)editor, 0, 0);
            return PortraitSafetySnapshotNative.TryRead<nint>(state + layout.StateViewOffset, out var view)
                ? ((long)editor, (long)state, (long)view) : ((long)editor, (long)state, -1);
        }
    }
}
