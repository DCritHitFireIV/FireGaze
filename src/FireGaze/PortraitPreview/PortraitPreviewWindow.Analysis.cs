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
    private string analysisInputStatus = "";

    private void DrawAnalysisWindow()
    {
        if (!showAnalysis) return;
        ImGui.SetNextWindowSize(new Vector2(510, 680) * Scale, ImGuiCond.FirstUseEver);
        if (ImGui.Begin("肖像分析###FireGazePortraitAnalysis", ref showAnalysis))
        {
            ImGui.TextWrapped("基于当前历史记录的肖像照片，供构图与明暗参考。不会改动肖像。");
            var current = feature.History.FirstOrDefault(record => record.IsCurrent);
            if (current?.ID != analysisRecord)
            {
                analysis.Invalidate();
                analysisRecord = current?.ID ?? 0;
                analysisInputStatus = "";
            }
            if (ImGui.Button("分析当前记录")) StartCurrentAnalysis();
            if (analysis.IsBusy) ImGui.TextDisabled("正在读取图像…");
            if (!string.IsNullOrEmpty(analysisInputStatus)) ImGui.TextWrapped(analysisInputStatus);
            if (!string.IsNullOrEmpty(analysis.Error)) ImGui.TextWrapped("分析暂时不可用，请重试。");
            if (analysis.CurrentResult is { } result)
            {
                ImGui.TextDisabled($"{result.Width} × {result.Height} · 有效像素 {result.VisiblePixels:N0}");
                DrawHistogram("明暗分布", result.Luma, FireGazePalette.Text);
                DrawHistogram("红", result.Red, new Vector4(.95f, .45f, .45f, 1));
                DrawHistogram("绿", result.Green, new Vector4(.45f, .85f, .55f, 1));
                DrawHistogram("蓝", result.Blue, new Vector4(.45f, .65f, .95f, 1));
                ImGui.TextUnformatted($"暗部截断 {result.BlackClipFraction:P1} · 高光截断 {result.WhiteClipFraction:P1}");
                ImGui.TextDisabled("按图像编码值统计，透明边缘不参与；不是物理测光。");
                ImGui.Separator();
                ImGui.TextUnformatted("显著区域（估计）");
                ImGui.TextWrapped("基于局部反差与边缘的提示，供检查哪些元素较醒目；不代表真实眼动或第一眼。");
                DrawAttentionEstimate(result);
            }
        }
        ImGui.End();
        if (!showAnalysis) analysis.Invalidate();
    }

    private void StartCurrentAnalysis()
    {
        var current = feature.History.FirstOrDefault(record => record.IsCurrent);
        if (current is null || photos?.Get(current.ID) is not { } texture)
        {
            analysisInputStatus = "当前照片尚未就绪，请暂停动作并稍候再试。";
            return;
        }
        if (analysis.IsBusy) return;
        // Public texture sharing owns the resource until readback completes, including close/unload.
        var retained = texture.CreateWrapSharingLowLevelResource();
        var key = current.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!analysis.TryStart(key, token => ReadAnalysis(retained, token))) retained.Dispose();
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
        if (photos?.Get(analysisRecord) is not { } texture) return;
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(160, 256) * Scale;
        ImGui.Image(texture.Handle, size);
        var draw = ImGui.GetWindowDrawList();
        foreach (var region in result.Hotspots)
        {
            var minimum = origin + new Vector2(region.X / (float)result.Width, region.Y / (float)result.Height) * size;
            var maximum = minimum + new Vector2(region.Width / (float)result.Width, region.Height / (float)result.Height) * size;
            draw.AddRect(minimum, maximum, ImGui.GetColorU32(FireGazePalette.Warn), 2 * Scale, ImDrawFlags.None, 2 * Scale);
        }
        if (result.Hotspots.Count == 0) ImGui.TextDisabled("当前画面没有明显的局部反差热点。");
    }
}
