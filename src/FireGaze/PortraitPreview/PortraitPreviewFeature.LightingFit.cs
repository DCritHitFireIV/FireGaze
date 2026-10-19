using System.Security.Cryptography;
using System.Text;

namespace FireGaze.PortraitPreview;

/// <summary>Framework-owned, six-channel light fitting with a retained native/readback cleanup barrier.</summary>
internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitLightingFitCaptureBridge? lightingFitCapture;
    private PortraitLightingFitFeatureBackend? lightingFitBackend;
    private PortraitLightingFitAdapter? lightingFitAdapter;
    private PortraitLightingFitSession? lightingFitSession;
    private PortraitLightingFitBinding? lightingFitBinding;
    private PortraitLightingFitCaptureFrame? lightingFitFrame;
    private PortraitCaptureViewport? lightingFitViewport;
    private PortraitLightingFitView? lightingFitView;
    private bool lightingFitHistorySuppressed;
    private DateTime nextLightingFitViewRead;

    public PortraitLightingFitView? LightingFit => lightingFitView;
    internal object LightingFitDiagnostics
    {
        get
        {
            var controls = Plugin.Framework.IsInFrameworkUpdateThread && target.TryResolve(out var identity, out _, out _)
                ? target.ReadLightControls(identity) : null;
            return new
            {
                view = lightingFitView,
                rawControls = controls,
                ranges = controls?.Select(control => new PortraitNativeLightRange(control.Minimum, control.Maximum, 1)).ToArray(),
                historySuppressed = lightingFitHistorySuppressed,
                mutationBlocked = LightingFitMutationBlocked,
                captureFrame = lightingFitFrame is { } frame ? new { frame.Width, frame.Height, frame.Sample.StateKey, frame.Surface.GenerationKey } : null,
                nativeWritePending = lightingFitAdapter?.HasPendingCleanup ?? false,
                physicalCapturePending = lightingFitCapture?.IsBusy ?? false,
                fitFailure = lightingFitSession?.FitFailure.ToString(),
                proposal = lightingFitSession?.Proposal,
                verification = lightingFitSession?.Verification,
            };
        }
    }
    private bool LightingFitHistorySuppressed => lightingFitHistorySuppressed;
    private bool LightingFitMutationBlocked => lightingFitHistorySuppressed
        || lightingFitSession is { IsActive: true } or { HasPendingCleanup: true } or { HasPendingCapture: true }
        || lightingFitAdapter is { HasPendingCleanup: true } or { HasPendingCapture: true }
        || lightingFitCapture is { IsBusy: true };

    private void InitializeLightingFit()
    {
        if (liveCaptureAPI is null) return;
        lightingFitCapture = new(liveCaptureAPI, new PortraitLightingFitTextureReadback(Plugin.TextureReadback));
        lightingFitBackend = new(new LightingFitHost(this));
        lightingFitAdapter = new(lightingFitBackend);
        lightingFitSession = new(lightingFitAdapter);
        PublishLightingFitView(false);
    }

    public void StartLightingFit(PortraitColorToneTarget colorTarget, PortraitSampleRegion region)
        => StartLightingFit(colorTarget, region, null);

    public void StartLightingFit(PortraitColorToneTarget colorTarget, PortraitSampleRegion region, string? expectedFixedStateKey) => Mutate(() =>
    {
        if (lightingFitSession is null || lightingFitBackend is null || !session.IsActive || !target.CanBlockSave)
        { Error("请先打开肖像预览并暂停动作。"); return; }
        if (LightingFitMutationBlocked) { Error("灯光取样尚未结束，请稍候。"); return; }
        lightingFitViewport = requestedCaptureViewport;
        if (!TryReadLightingFitBinding(out var binding) || !binding.Snapshot.Composition.Paused)
        { Error("请暂停动作，等待肖像图像加载完成后重试。"); return; }
        if (expectedFixedStateKey is not null && !string.Equals(expectedFixedStateKey, binding.State.FixedStateKey, StringComparison.Ordinal))
        { Error("肖像已变化，请等待新图像显示后重新框选。"); return; }
        if (!region.TryMask(binding.State.Width, binding.State.Height, out var mask))
        { Error("请框选稍大一些的角色受光区域。"); return; }
        // Finish any existing user gesture before sample suppression. This captures real changes
        // preceding the transaction; individual probe states never enter the ordinary observer.
        CancelLightDrag();
        HandleHistoryBatchResult(historyBatch.Flush());
        RecordDirectChange("调整肖像");
        if (!lightingFitSession.TryStart(colorTarget, mask))
        { Error("当前肖像暂时无法取样，请等待图像稳定后重试。"); return; }
        PublishLightingFitView(true);
    });

    public void CancelLightingFit() => Mutate(() =>
    {
        _ = CancelLightingFitForEdit();
        PublishLightingFitView(false);
    });

    /// <summary>Cancel and synchronously compensate owned RGB. A physical capture must still drain.</summary>
    private bool CancelLightingFitForEdit()
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread) return false;
        if (!LightingFitMutationBlocked) return true;
        lightingFitSession?.Cancel();
        lightingFitAdapter?.CancelCapturePublication();
        lightingFitCapture?.Close();
        PollLightingFit();
        return !LightingFitMutationBlocked;
    }

    private void PollLightingFit()
    {
        if (!Plugin.Framework.IsInFrameworkUpdateThread || lightingFitSession is null) return;
        var wasBlocked = LightingFitMutationBlocked;
        lightingFitCapture?.Update();
        lightingFitAdapter?.Poll();
        lightingFitSession.Poll();
        if (wasBlocked || LightingFitMutationBlocked) PublishLightingFitView(lightingFitBinding is not null);
        else if (DateTime.UtcNow >= nextLightingFitViewRead)
        {
            nextLightingFitViewRead = DateTime.UtcNow.AddMilliseconds(200);
            PublishLightingFitView(TryReadLightingFitBinding(out _));
        }
    }

    /// <summary>Draw owns composition only; all editor reads and writes remain on Framework.</summary>
    private void DrawLightingFit() => lightingFitCapture?.Draw();

    /// <summary>Never waits for Present/GPU work; the bridge releases independently retained late textures.</summary>
    private void DisposeLightingFit()
    {
        _ = CancelLightingFitForEdit();
        lightingFitCapture?.Dispose();
        lightingFitAdapter?.Poll();
        lightingFitSession?.Cancel();
        lightingFitSession?.Poll();
        lightingFitView = null;
    }

    private bool TryReadLightingFitBinding(out PortraitLightingFitBinding binding)
    {
        binding = null!;
        if (!Plugin.Framework.IsInFrameworkUpdateThread || lightingFitCapture is null
            || !target.TryResolve(out var identity, out _, out _)
            || target.CaptureEditorState(identity) is not { } snapshot || GetHistoryKey(identity, snapshot) is not { } key
            || target.ReadLightControls(identity) is not { Length: 6 } controls) return false;
        int[] colors = [snapshot.Composition.DirectionalRed, snapshot.Composition.DirectionalGreen, snapshot.Composition.DirectionalBlue,
            snapshot.Composition.AmbientRed, snapshot.Composition.AmbientGreen, snapshot.Composition.AmbientBlue];
        // Steps is slider metadata rather than a color quantum. These six native fields are bytes.
        var ranges = controls.Select(control => new PortraitNativeLightRange(control.Minimum, control.Maximum, 1)).ToArray();
        if (Enumerable.Range(0, 6).Any(axis => !ranges[axis].Contains(colors[axis])
            || !LightingFitMutationBlocked && controls[axis].Value != colors[axis])) return false;

        string surface; int width; int height;
        var viewport = lightingFitViewport ?? requestedCaptureViewport;
        if (snapshot.Composition.Paused && viewport is { } area && lightingFitCapture.TryReadFrame(area, out var frame)
            && frame is not null && frame.Sample.Editor == identity && frame.Sample.Owner.ContentID == key.ContentID
            && frame.Sample.StateKey.EndsWith(":" + SnapshotDigest(snapshot), StringComparison.Ordinal))
        {
            lightingFitFrame = frame;
            surface = frame.Surface.GenerationKey;
            width = frame.Width; height = frame.Height;
        }
        else
        {
            // Playback makes a stable sampling image unavailable. A previous same-owner descriptor
            // can still support conditional RGB rollback; Capture/Commit/Start separately require a
            // fresh paused frame, so it can never become a new probe or silently accept a changed pose.
            if (snapshot.Composition.Paused || lightingFitBinding is not { } previous
                || previous.Identity != identity || previous.HistoryKey != key || !LightingFitMutationBlocked) return false;
            surface = previous.SurfaceGenerationKey;
            width = previous.State.Width; height = previous.State.Height;
        }
        if (!PortraitLightingFitBinding.TryCreate(identity, key, snapshot, surface, width, height, ranges, out var current)
            || current is null) return false;
        lightingFitBinding = binding = current;
        return true;
    }

    private static string SnapshotDigest(PortraitEditorSnapshot snapshot)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.ToJSON())));

    private void PublishLightingFitView(bool readable)
    {
        var fit = lightingFitSession;
        if (fit is null) { lightingFitView = null; return; }
        var current = lightingFitBinding;
        var blocked = LightingFitMutationBlocked;
        var ready = readable && current is not null && current.Snapshot.Composition.Paused
            && session.IsActive && target.CanBlockSave && !blocked;
        var message = fit.HasPendingCleanup || lightingFitAdapter?.HasPendingCleanup == true ? "正在还原临时灯光…"
            : fit.IsActive ? "正在分析受光与色彩，请保持肖像不变…"
            : blocked ? "正在结束取样，请稍候…"
            : fit.Outcome switch
            {
                PortraitLightingFitSessionOutcome.Accepted => "已应用灯光，可在编辑记录中撤回。",
                PortraitLightingFitSessionOutcome.Cancelled => "已取消，原有灯光已保留。",
                PortraitLightingFitSessionOutcome.ExternalChange => "肖像已变化，取样已停止。",
                PortraitLightingFitSessionOutcome.CalibrationRejected or PortraitLightingFitSessionOutcome.CandidateRejected
                    => "当前灯光无法更接近目标色彩，原有灯光已保留。",
                PortraitLightingFitSessionOutcome.None => ready ? "框选角色的受光区域后应用。" : "请打开肖像预览并暂停动作。",
                _ => "本次取样未完成，原有灯光已保留。请稍后重试。",
            };
        lightingFitView = new(blocked, ready, message, fit.Phase, fit.Outcome, current?.State.Width ?? 0,
            current?.State.Height ?? 0, current?.SurfaceGenerationKey ?? "", current?.State.EditorIdentity ?? "",
            current?.State.FixedStateKey ?? "", fit.HasPendingCleanup || lightingFitAdapter?.HasPendingCleanup == true,
            fit.HasPendingCapture || lightingFitAdapter?.HasPendingCapture == true || lightingFitCapture?.IsBusy == true,
            lightingFitFrame?.Sample.StateKey ?? "");
    }

    private bool CommitLightingFit(PortraitLightingFitBinding expected, IReadOnlyList<int> baseline)
    {
        if (!lightingFitHistorySuppressed || !session.IsActive || !target.CanBlockSave
            || !TryReadLightingFitBinding(out var current)
            || current.State.EditorIdentity != expected.State.EditorIdentity || current.State.FixedStateKey != expected.State.FixedStateKey
            || current.State.Width != expected.State.Width || current.State.Height != expected.State.Height
            || !current.State.Ranges.SequenceEqual(expected.State.Ranges) || !current.State.Parameters.SequenceEqual(expected.State.Parameters)) return false;
        var captured = current.Snapshot;
        var result = history.Record(current.HistoryKey, captured.ToJSON(), "调整灯光色彩");
        HandleHistoryResult(result);
        if (result.Status is not (PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate)) return false;
        historyBatch.Cancel();
        historyLabelBaseline = null;
        observedIdentity = current.Identity;
        observedKey = current.HistoryKey;
        observedEditor = captured;
        historyObservedEditor = captured;
        deferredBaselineSync = false;
        if (syncPlate) _ = target.UpdatePlateMirror(current.Identity, captured, true);
        RefreshHistoryRows();
        return true;
    }

    private sealed class LightingFitHost(PortraitPreviewFeature feature) : IPortraitLightingFitFeatureHost
    {
        public bool IsFrameworkThread => Plugin.Framework.IsInFrameworkUpdateThread;
        public bool TryReadBinding(out PortraitLightingFitBinding binding) => feature.TryReadLightingFitBinding(out binding);
        public bool IsEditorOwnerDestroyed(PortraitIdentity identity) => feature.target.IsEditorOwnerDestroyed(identity);
        public PortraitLightingFitNativeWrite WriteColors(PortraitLightingFitBinding expected, IReadOnlyList<int> desired)
        {
            if (!PortraitNativeLightColors.TryCreate(desired, out var colors)) return new(false, null);
            var result = feature.target.WriteLightColors(expected.Identity, expected.Snapshot, colors);
            return new(result.Applied, result.Owned?.Parameters);
        }
        public void SetHistorySuppressed(bool value) => feature.lightingFitHistorySuppressed = value;
        public Task<IReadOnlyList<PortraitLightRgba>> CaptureAsync(PortraitLightingFitBinding expected)
        {
            var frame = feature.lightingFitFrame;
            if (feature.lightingFitCapture is null || frame is null || frame.Sample.Editor != expected.Identity
                || frame.Surface.GenerationKey != expected.SurfaceGenerationKey || frame.Width != expected.State.Width || frame.Height != expected.State.Height
                || !frame.Sample.StateKey.EndsWith(":" + SnapshotDigest(expected.Snapshot), StringComparison.Ordinal))
                return Task.FromException<IReadOnlyList<PortraitLightRgba>>(new InvalidOperationException("Portrait capture changed before sampling."));
            return feature.lightingFitCapture.CaptureAsync(frame);
        }
        public bool CommitHistory(PortraitLightingFitBinding expected, IReadOnlyList<int> baseline) => feature.CommitLightingFit(expected, baseline);
    }
}
