using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

/// <summary>Opt-in analysis of an existing disposable photo, never a mutation of the live model.</summary>
internal sealed partial class PortraitPreviewWindow
{
    private readonly PortraitAnalysisRequest analysis = new();
    private bool showAnalysis;
    private long analysisRecord;
    private PortraitImageScope analysisScope = PortraitImageScope.Portrait;
    private string analysisSourceKey = "";
    private string analysisInputStatus = "";
    private PortraitAnalysisSubject? analysisSubject;
    private PortraitAnalysisSubject? analysisResultSubject;
    private PortraitAnalysisProvenance? analysisProvenance;
    private IDalamudTextureWrap? analysisPreview;
    private IDisposable? analysisPreviewLifetime;
    private readonly PortraitCaptureRetirement analysisPhysical = new();
    private int analysisResourcesDisposed;
    internal bool IsSourceCensusIdle => !analysis.IsBusy && analysisPhysical.Pending == 0 && analysisPhysical.CleanupFailures == 0;

    private void DrawAnalysisWindow()
    {
        if (!showAnalysis) return;
        ObserveAnalysisSubject();
        FireGazeWindowPlacement.Prepare("FireGazePortraitAnalysis", new Vector2(510, 680) * Scale, Scale, "FireGazePortraitPreview");
        FireGazeWindowPlacement.Constrain(new Vector2(360, 340) * Scale, new Vector2(760, 980) * Scale, Scale);
        if (FireGazeWindowPlacement.Begin("肖像分析###FireGazePortraitAnalysis", ref showAnalysis, "FireGazePortraitAnalysis"))
        {

            ImGui.TextWrapped("基于当前成图，供构图与明暗参考。不会改动肖像。");
            var scope = feature.LiveCaptureScope;
            if (ImGui.RadioButton("肖像", scope == PortraitImageScope.Portrait))
                feature.SetLiveCaptureScope(PortraitImageScope.Portrait);
            ImGui.SameLine();
            if (ImGui.RadioButton("可见铭牌", scope == PortraitImageScope.VisibleAdventurerPlate))
                feature.SetLiveCaptureScope(PortraitImageScope.VisibleAdventurerPlate);
            scope = feature.LiveCaptureScope;
            if (analysisScope != scope)
            {
                analysisScope = scope;
                ResetAnalysisSource();
            }
            ObserveAnalysisSubject();
            ImGui.TextDisabled(scope == PortraitImageScope.Portrait
                ? "读取肖像角色与边框装饰。"
                : "读取当前屏幕可见的完整铭牌，不改变铭牌数据。");
            var current = feature.History.FirstOrDefault(record => record.IsCurrent);
            if (current?.ID != analysisRecord)
            {
                if (analysisSourceKey.StartsWith("history:", StringComparison.Ordinal)) ResetAnalysisSource();
                analysisRecord = current?.ID ?? 0;
            }
            if (ImGui.Button("分析当前记录")) StartCurrentAnalysis();
            if (analysis.IsBusy) ImGui.TextDisabled("正在读取图像…");
            if (!string.IsNullOrEmpty(analysisInputStatus)) ImGui.TextWrapped(analysisInputStatus);
            if (!string.IsNullOrEmpty(analysis.Error)) ImGui.TextWrapped("分析暂时不可用，请重试。");
            if (analysis.CurrentResult is { } result)
            {
                ImGui.TextDisabled($"图像尺寸：{result.Width} × {result.Height}");
                DrawHistogram("明暗分布", result.Luma, FireGazePalette.Text);
                DrawHistogram("红", result.Red, FireGazePalette.ChartRed);
                DrawHistogram("绿", result.Green, FireGazePalette.ChartGreen);
                DrawHistogram("蓝", result.Blue, FireGazePalette.ChartBlue);
                ImGui.TextUnformatted($"暗部截断 {result.BlackClipFraction:P1} · 高光截断 {result.WhiteClipFraction:P1}");
                ImGui.TextDisabled("透明区域不计入统计。明暗分布仅供画面参考。");
                ImGui.Separator();
                ImGui.TextUnformatted("醒目区域参考");
                ImGui.TextWrapped("提示画面中较醒目的区域，帮助检查主体是否突出；不预测真实观看顺序。");
                DrawAttentionEstimate(result);
            }
            if (analysisPreview is { } compared && analysis.CurrentResult is not null)
                DrawComparison(compared, analysisResultSubject is not null && analysisResultSubject == analysisSubject);
        }
        ImGui.End();
        if (!showAnalysis) { ResetAnalysisSource(); ClearAnalysisComparison(); analysisSubject = null; }
    }

    private void StartCurrentAnalysis()
    {
        if (feature.SourceCensusBlocksOperations) { analysisInputStatus = "只读原生诊断正在运行，请结束后重试。"; return; }
        var scope = feature.LiveCaptureScope;
        ObserveAnalysisSubject();
        if (analysisScope != scope)
        {
            analysisScope = scope;
            ResetAnalysisSource();
        }
        var current = feature.History.FirstOrDefault(record => record.IsCurrent);
        if (current is null)
        {
            ResetAnalysisSource();
            analysisInputStatus = "当前照片尚未就绪，请暂停动作并稍候再试。";
            return;
        }
        if (analysis.IsBusy) return;
        if (!analysisPhysical.TryEnter(out var inputLifetime)) return;
        IDalamudTextureWrap? retained = null;
        PortraitCapturedImage? live = null;
        try
        {
            // Admission precedes the external sharing prefix, which may reenter window close.
            live = feature.AcquireCurrentLiveImage();
            if (Volatile.Read(ref analysisResourcesDisposed) != 0) return;
            IDalamudTextureWrap source;
            string key;
            PortraitAnalysisProvenance provenance;
            PortraitAnalysisSubject? resultSubject;
            if (live is not null && live.Sample.Scope == scope)
            {
                key = LiveSourceKey(live, scope);
                source = live.Texture;
                provenance = new(scope == PortraitImageScope.Portrait ? "实时肖像" : "可见铭牌", DateTimeOffset.Now, false);
                resultSubject = AnalysisSampleMatchesSubject(live.Sample) ? analysisSubject : null;
            }
            else
            {
                analysisPhysical.Release(live); live = null;
                if (scope == PortraitImageScope.VisibleAdventurerPlate)
                {
                    ResetAnalysisSource();
                    analysisRecord = current.ID;
                    analysisInputStatus = "当前完整铭牌图像尚未就绪，请打开铭牌并稍候再试。";
                    return;
                }
                if (analysisPhysical.CleanupFailures != 0 || photos?.Get(current.ID) is not { } texture)
                {
                    ResetAnalysisSource();
                    analysisRecord = current.ID;
                    analysisInputStatus = "当前高分图像尚未就绪，请暂停动作并稍候再试。";
                    return;
                }
                key = $"history:{feature.SourceGuideKey}:{current.ID}";
                source = texture;
                provenance = new("历史记录", current.At, true);
                resultSubject = analysisSubject;
            }
            if (analysisSourceKey == key && analysis.CurrentResult is not null) return;
            ResetAnalysisSource();
            if (analysisPhysical.CleanupFailures != 0 || Volatile.Read(ref analysisResourcesDisposed) != 0) return;
            retained = source.CreateWrapSharingLowLevelResource();
            if (!TrySetAnalysisPreview(source)) return;
            // Detect a failed intermediate release before submitting another provider operation.
            analysisPhysical.Release(live); live = null;
            if (analysisPhysical.CleanupFailures != 0 || Volatile.Read(ref analysisResourcesDisposed) != 0)
            { ReleaseAnalysisPreview(); return; }
            analysisSourceKey = key;
            analysisProvenance = provenance;
            analysisResultSubject = resultSubject;
            analysisRecord = current.ID;
            var owned = retained; var held = inputLifetime;
            if (analysis.TryStart(key, token => ReadAnalysis(owned, held, token)))
            { retained = null; inputLifetime = null; }
            analysisInputStatus = "";
        }
        catch (Exception failure)
        {
            analysisPhysical.ObserveFailure(failure);
            ReleaseAnalysisPreview();
            analysisInputStatus = "暂时无法读取图像，请重试。";
        }
        finally { analysisPhysical.Release(live); analysisPhysical.Release(retained); inputLifetime?.Dispose(); }
    }

    private static string LiveSourceKey(PortraitCapturedImage image, PortraitImageScope scope)
    {
        var sample = image.Sample;
        var plate = sample.Plate?.VisualKey ?? "";
        return $"live:{scope}:{sample.Owner.ContentID}:{sample.Editor.Generation}:{sample.StateKey}:{sample.Viewport.ID}:"
            + $"{sample.Viewport.Size.X:R}x{sample.Viewport.Size.Y:R}:{plate}:{image.Width}x{image.Height}";
    }

    private void ResetAnalysisSource()
    {
        analysis.Invalidate();
        ReleaseAnalysisPreview();
        analysisSourceKey = "";
        analysisProvenance = null;
        analysisResultSubject = null;
        analysisInputStatus = "";
    }

    private void ObserveAnalysisSubject()
    {
        // The feature publishes a single Framework-observed reference; never read native owners here.
        var session = feature.CurrentAnalysisSession;
        var next = session is { Owner.IsValid: true, Editor.IsValid: true, Entry.IsValid: true }
            && session.Editor.Generation != 0
            && session.Entry.ContentID == session.Owner.ContentID
            ? new PortraitAnalysisSubject(session, feature.LiveCaptureScope) : null;
        if (analysisSubject == next) return;
        ResetAnalysisSource();
        ClearAnalysisComparison();
        analysisSubject = next;
    }

    private bool AnalysisSampleMatchesSubject(PortraitCaptureSample sample)
        => analysisSubject is { } subject && sample.Owner == subject.Session.Owner
            && sample.Editor == subject.Session.Editor && sample.Scope == subject.Scope
            && (sample.Scope != PortraitImageScope.VisibleAdventurerPlate || sample.Plate is { } plate
                && plate.AccountID == subject.Session.Owner.AccountID && plate.ContentID == subject.Session.Owner.ContentID);

    private bool TrySetAnalysisPreview(IDalamudTextureWrap source)
    {
        if (!analysisPhysical.TryEnter(out var held)) return false;
        IDalamudTextureWrap? owned = null;
        try
        {
            owned = source.CreateWrapSharingLowLevelResource();
            if (Volatile.Read(ref analysisResourcesDisposed) != 0 || analysisPhysical.CleanupFailures != 0) return false;
            analysisPreview = owned; analysisPreviewLifetime = held;
            owned = null; held = null;
            return true;
        }
        finally { analysisPhysical.Release(owned); held?.Dispose(); }
    }

    private void ReleaseAnalysisPreview()
    {
        var texture = analysisPreview; analysisPreview = null;
        var held = analysisPreviewLifetime; analysisPreviewLifetime = null;
        analysisPhysical.Release(texture); held?.Dispose();
    }

    private void DisposeAnalysisResources()
    {
        Interlocked.Exchange(ref analysisResourcesDisposed, 1);
        analysis.Dispose();
        ReleaseAnalysisPreview();
        ClearAnalysisComparison();
        _ = analysisPhysical.Seal();
    }

    public Task DrainPhysicalAsync() => analysisPhysical.Seal();

    private async Task<PortraitImageAnalysisResult> ReadAnalysis(IDalamudTextureWrap retained, IDisposable held, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var raw = await Plugin.TextureReadback.GetRawImageAsync(retained,
                new TextureModificationArgs { Uv1 = Vector2.One }, leaveWrapOpen: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var format = raw.Specification.DxgiFormat;
            if (format is not (28 or 29 or 87 or 91)) throw new InvalidOperationException("Unsupported analysis texture format.");
            token.ThrowIfCancellationRequested();
            return await Task.Run(() => PortraitImageAnalysis.Analyze(raw.RawData,
                raw.Specification.Width, raw.Specification.Height, raw.Specification.Pitch,
                format is 28 or 29 ? PortraitPixelOrder.Rgba : PortraitPixelOrder.Bgra,
                new(MinimumAlpha: 255)), token).ConfigureAwait(false);
        }
        catch (Exception failure) { analysisPhysical.ObserveFailure(failure); throw; }
        finally { analysisPhysical.Release(retained); held.Dispose(); }
    }

    private static void DrawHistogram(string label, IReadOnlyList<int> bins, Vector4 color)
    {
        ImGui.TextUnformatted(label);
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(120 * Scale, ImGui.GetContentRegionAvail().X), 48 * Scale);
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(FireGazePalette.Panel));
        var maximum = Math.Max(1, bins.Max());
        for (var index = 0; index < bins.Count; index++)
        {
            var left = origin.X + index * size.X / bins.Count;
            var right = origin.X + (index + 1) * size.X / bins.Count;
            var top = origin.Y + size.Y * (1 - bins[index] / (float)maximum);
            draw.AddRectFilled(new(left, top), new(right, origin.Y + size.Y), ImGui.GetColorU32(color));
        }
    }

    private void DrawAttentionEstimate(PortraitImageAnalysisResult result)
    {
        var texture = analysisPreview;
        if (texture is null) return;
        var origin = ImGui.GetCursorScreenPos();
        var available = new Vector2(Math.Max(160 * Scale, ImGui.GetContentRegionAvail().X), 256 * Scale);
        if (!PortraitComparisonLayout.TryFit(available, new(texture.Width, texture.Height), out var fit)) return;
        ImGui.Dummy(available);
        var imageOrigin = origin + fit.Offset;
        var imageMaximum = imageOrigin + fit.Size;
        ImGui.GetWindowDrawList().AddImage(texture.Handle, imageOrigin, imageMaximum);
        var draw = ImGui.GetWindowDrawList();
        foreach (var region in result.Hotspots)
        {
            var minimum = imageOrigin + new Vector2(region.X / (float)result.Width, region.Y / (float)result.Height) * fit.Size;
            var maximum = minimum + new Vector2(region.Width / (float)result.Width, region.Height / (float)result.Height) * fit.Size;
            draw.AddRect(minimum, maximum, ImGui.GetColorU32(FireGazePalette.Warn), 2 * Scale, ImDrawFlags.None, 2 * Scale);
        }
        if (result.Hotspots.Count == 0) ImGui.TextDisabled("当前画面没有明显突出的区域。");
    }
}
