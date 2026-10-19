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
    private IDalamudTextureWrap? analysisPreview;

    private void DrawAnalysisWindow()
    {
        if (!showAnalysis) return;
        ImGui.SetNextWindowSize(new Vector2(510, 680) * Scale, ImGuiCond.FirstUseEver);
        if (ImGui.Begin("肖像分析###FireGazePortraitAnalysis", ref showAnalysis))
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
                DrawHistogram("红", result.Red, new Vector4(.95f, .45f, .45f, 1));
                DrawHistogram("绿", result.Green, new Vector4(.45f, .85f, .55f, 1));
                DrawHistogram("蓝", result.Blue, new Vector4(.45f, .65f, .95f, 1));
                ImGui.TextUnformatted($"暗部截断 {result.BlackClipFraction:P1} · 高光截断 {result.WhiteClipFraction:P1}");
                ImGui.TextDisabled("透明区域不计入统计。明暗分布仅供画面参考。");
                ImGui.Separator();
                ImGui.TextUnformatted("醒目区域参考");
                ImGui.TextWrapped("提示画面中较醒目的区域，帮助检查主体是否突出；不预测真实观看顺序。");
                DrawAttentionEstimate(result);
            }
            if (analysisPreview is { } compared && analysis.CurrentResult is not null)
                DrawComparison(compared, "analysis:" + analysisSourceKey);
        }
        ImGui.End();
        if (!showAnalysis) { analysis.Invalidate(); analysisPreview?.Dispose(); analysisPreview = null; comparisonReference.Clear(); }
    }

    private void StartCurrentAnalysis()
    {
        var scope = feature.LiveCaptureScope;
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
        IDalamudTextureWrap? retained = null;
        var live = feature.AcquireCurrentLiveImage();
        if (live is not null && live.Sample.Scope == scope)
        {
            var liveKey = LiveSourceKey(live, scope);
            if (analysisSourceKey == liveKey && analysis.CurrentResult is not null)
            {
                live.Dispose();
                return;
            }
            ResetAnalysisSource();
            retained = live.Texture.CreateWrapSharingLowLevelResource();
            analysisPreview = live.Texture.CreateWrapSharingLowLevelResource();
            analysisSourceKey = liveKey;
            analysisRecord = current.ID;
            live.Dispose();
            if (!analysis.TryStart(liveKey, token => ReadAnalysis(retained, token))) retained.Dispose();
            analysisInputStatus = "";
            return;
        }
        live?.Dispose();
        if (scope == PortraitImageScope.VisibleAdventurerPlate)
        {
            ResetAnalysisSource();
            analysisRecord = current.ID;
            analysisInputStatus = "当前完整铭牌图像尚未就绪，请打开铭牌并稍候再试。";
            return;
        }
        if (photos?.Get(current.ID) is not { } texture)
        {
            ResetAnalysisSource();
            analysisRecord = current.ID;
            analysisInputStatus = "当前高分图像尚未就绪，请暂停动作并稍候再试。";
            return;
        }
        // Public texture sharing owns the resource until readback completes, including close/unload.
        var key = $"history:{feature.SourceGuideKey}:{current.ID}";
        if (analysisSourceKey == key && analysis.CurrentResult is not null) return;
        ResetAnalysisSource();
        retained = texture.CreateWrapSharingLowLevelResource();
        analysisPreview = texture.CreateWrapSharingLowLevelResource();
        analysisSourceKey = key;
        analysisRecord = current.ID;
        if (!analysis.TryStart(key, token => ReadAnalysis(retained, token))) retained.Dispose();
        analysisInputStatus = "";
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
        analysisPreview?.Dispose();
        analysisPreview = null;
        analysisSourceKey = "";
        comparisonReference.Clear();
        analysisInputStatus = "";
    }

    private static async Task<PortraitImageAnalysisResult> ReadAnalysis(IDalamudTextureWrap retained, CancellationToken token)
    {
        using (retained)
        {
            var raw = await Plugin.TextureReadback.GetRawImageAsync(retained,
                new TextureModificationArgs { Uv1 = Vector2.One }, leaveWrapOpen: true, cancellationToken: token).ConfigureAwait(false);
            var format = raw.Specification.DxgiFormat;
            if (format is not (28 or 29 or 87 or 91)) throw new InvalidOperationException("Unsupported analysis texture format.");
            token.ThrowIfCancellationRequested();
            return await Task.Run(() => PortraitImageAnalysis.Analyze(raw.RawData,
                raw.Specification.Width, raw.Specification.Height, raw.Specification.Pitch,
                format is 28 or 29 ? PortraitPixelOrder.Rgba : PortraitPixelOrder.Bgra,
                new(MinimumAlpha: 255)), token).ConfigureAwait(false);
        }
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
