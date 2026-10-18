using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private readonly PortraitComparisonReference<IDalamudTextureWrap> comparisonReference = new();
    private bool comparisonWipe;
    private float comparisonFraction = .5f;

    private void DrawComparison(IDalamudTextureWrap current, string scope)
    {
        if (!ImGui.CollapsingHeader("照片对比")) return;
        ImGui.TextDisabled("保留参考照片后，可继续编辑再比较。");
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (ImGui.Button("设为参考")) comparisonReference.Set(scope, current.CreateWrapSharingLowLevelResource());
        ContinueToolbar(right, ImGui.CalcTextSize("清除参考").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.Button("清除参考")) comparisonReference.Clear();
        ContinueToolbar(right, ImGui.CalcTextSize("并排").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.RadioButton("并排", !comparisonWipe)) comparisonWipe = false;
        ContinueToolbar(right, ImGui.CalcTextSize("叠加").X + ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.RadioButton("叠加", comparisonWipe)) comparisonWipe = true;
        var reference = comparisonReference.For(scope);
        if (reference is null) return;
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        var canvas = new Vector2(width, Math.Min(340 * Scale, width * 1.6f));
        if (comparisonWipe && Math.Abs(reference.Width / (double)reference.Height - current.Width / (double)current.Height) < .0001)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.SliderFloat("###PortraitComparisonCut", ref comparisonFraction, 0, 1, "参考 %.2f");
            var origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(canvas);
            if (!PortraitComparisonLayout.TryFit(canvas, new(current.Width, current.Height), out var fit)
                || !PortraitComparisonLayout.TryWipe(fit.Size, comparisonFraction, out var wipe)) return;
            var minimum = origin + fit.Offset;
            var maximum = minimum + fit.Size;
            var draw = ImGui.GetWindowDrawList();
            draw.AddImage(current.Handle, minimum, maximum);
            if (wipe.Cut > 0)
                draw.AddImage(reference.Handle, minimum, new(minimum.X + wipe.Cut, maximum.Y),
                    Vector2.Zero, new Vector2(wipe.UvCut, 1));
            draw.AddLine(new(minimum.X + wipe.Cut, minimum.Y), new(minimum.X + wipe.Cut, maximum.Y),
                ImGui.GetColorU32(FireGazePalette.Focus), Scale);
            ImGui.TextDisabled("左侧参考 · 右侧当前照片");
        }
        else
        {
            if (comparisonWipe) ImGui.TextDisabled("照片比例不同，使用并排对比。");
            var gap = ImGui.GetStyle().ItemSpacing.X;
            var cell = new Vector2(Math.Max(1, (width - gap) / 2), canvas.Y);
            var origin = ImGui.GetCursorScreenPos();
            ImGui.Dummy(canvas);
            DrawComparedPhoto(reference, origin, cell);
            DrawComparedPhoto(current, origin + new Vector2(cell.X + gap, 0), cell);
            ImGui.TextDisabled("左侧参考 · 右侧当前照片");
        }
    }

    private static void DrawComparedPhoto(IDalamudTextureWrap photo, Vector2 origin, Vector2 available)
    {
        if (!PortraitComparisonLayout.TryFit(available, new(photo.Width, photo.Height), out var fit)) return;
        ImGui.GetWindowDrawList().AddImage(photo.Handle, origin + fit.Offset, origin + fit.Offset + fit.Size);
    }
}
