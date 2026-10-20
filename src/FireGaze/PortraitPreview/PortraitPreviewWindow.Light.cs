using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private bool showLight;
    private PortraitLightView lightView = PortraitLightView.Side;
    private long lightTokenSerial;
    // Every managed caller reserves from the same sequence, including bounded diagnostics.
    // Reserving a token never reads the editor or invokes a native function.
    private long NextLightToken() => Interlocked.Increment(ref lightTokenSerial);
    private long lightPointerToken;
    private bool lightNegativeHidden;
    private PortraitLightAngles lightPointerAngles;

    private void DrawLightWindow()
    {
        if (!showLight) { CancelLightInteraction();return; }
        PrepareLightWindow();
        var visible=FireGazeWindowPlacement.Begin("主光方向###FireGazePortraitLight", ref showLight, "FireGazePortraitLight");
        if (visible)
        {

            var current=feature.DirectionalLight;
            if (current is null)
            {
                CancelLightInteraction();
                ImGui.TextWrapped("请先打开 FireGaze预览，等待肖像加载。");
            }
            else DrawLightDirection(new(current.Vertical,current.Horizontal));
        }
        else CancelLightInteraction();
        ImGui.End();
        if (!showLight) CancelLightInteraction();
    }

    private void DrawLightDirection(PortraitLightAngles current)
    {
        if (DrawTab("俯视",lightView==PortraitLightView.Top,true)) ChangeLightView(PortraitLightView.Top);
        ImGui.SameLine();
        if (DrawTab("侧视",lightView==PortraitLightView.Side,true)) ChangeLightView(PortraitLightView.Side);
        ImGui.TextWrapped("拖动光点调整主光方向。");

        var angles=lightPointerToken==0?current:lightPointerAngles;
        if (lightPointerToken==0) _=PortraitLightDiagram.TryHiddenSide(angles,lightView,out lightNegativeHidden);
        var negative=lightNegativeHidden;
        var sideLabel=lightView==PortraitLightView.Top?"从下方照射":"从左侧照射";
        if (ImGui.Checkbox(sideLabel,ref negative))
        {
            CancelLightInteraction();
            lightNegativeHidden=negative;
            if (PortraitLightDirection.TryProject(current.VerticalDegrees,current.HorizontalDegrees,lightView,out var point)
                && PortraitLightDirection.TryFromProjection(point,lightView,negative,out var flipped))
            {
                var token=NextLightToken();
                feature.BeginLightDrag(token);
                feature.EndLightDrag(token,flipped.VerticalDegrees,flipped.HorizontalDegrees);
            }
        }

        var side=Math.Min(ImGui.GetContentRegionAvail().X,300*Scale);
        var origin=ImGui.GetCursorScreenPos();
        var size=new Vector2(side,side);
        if (!PortraitLightDiagram.TryCreate(origin,size,out var disc,24*Scale)) return;
        _=ImGui.InvisibleButton("###PortraitLightDirectionDisc",size,ImGuiButtonFlags.MouseButtonLeft);
        if (ImGui.IsItemActivated())
        {
            lightPointerToken=NextLightToken();
            _=PortraitLightDiagram.TryHiddenSide(current,lightView,out lightNegativeHidden);
            lightPointerAngles=current;
            feature.BeginLightDrag(lightPointerToken);
        }
        if (lightPointerToken!=0)
        {
            if (PortraitLightDiagram.TryAnglesAtPointer(disc,ImGui.GetMousePos(),lightView,lightNegativeHidden,out var desired))
            {
                lightPointerAngles=desired;
                feature.MoveLightDrag(lightPointerToken,desired.VerticalDegrees,desired.HorizontalDegrees);
            }
            if (ImGui.IsItemDeactivated() || !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                feature.EndLightDrag(lightPointerToken,lightPointerAngles.VerticalDegrees,lightPointerAngles.HorizontalDegrees);
                lightPointerToken=0;
            }
        }
        angles=lightPointerToken==0?current:lightPointerAngles;
        DrawLightDisc(disc,angles);
        ImGui.TextUnformatted($"垂直 {angles.VerticalDegrees:F0}° · 水平 {angles.HorizontalDegrees:F0}°");
        ImGui.TextDisabled("方向示意 · 环境光没有方向");
    }

    private void DrawLightDisc(PortraitLightDisc disc,PortraitLightAngles angles)
    {
        var draw=ImGui.GetWindowDrawList();
        var guide=ImGui.GetColorU32(FireGazePalette.Muted);
        var text=ImGui.GetColorU32(FireGazePalette.Text);
        var accent=ImGui.GetColorU32(FireGazePalette.Focus);
        draw.AddCircleFilled(disc.Center,disc.Radius,ImGui.GetColorU32(FireGazePalette.Panel),64);
        draw.AddCircle(disc.Center,disc.Radius,guide,64,Scale);
        draw.AddLine(disc.Center-new Vector2(disc.Radius,0),disc.Center+new Vector2(disc.Radius,0),guide);
        draw.AddLine(disc.Center-new Vector2(0,disc.Radius),disc.Center+new Vector2(0,disc.Radius),guide);
        draw.AddCircleFilled(disc.Center,9*Scale,text,24);
        // The central mark identifies the portrait subject; the light marker is a hollow ring.
        draw.AddLine(disc.Center+new Vector2(-14,12)*Scale,disc.Center+new Vector2(14,12)*Scale,text,2*Scale);
        var top=lightView==PortraitLightView.Top?"前":"上";
        var bottom=lightView==PortraitLightView.Top?"后":"下";
        var left=lightView==PortraitLightView.Top?"左":"后";
        var right=lightView==PortraitLightView.Top?"右":"前";
        draw.AddText(disc.Center-new Vector2(ImGui.CalcTextSize(top).X*.5f,disc.Radius+22*Scale),text,top);
        draw.AddText(disc.Center+new Vector2(-ImGui.CalcTextSize(bottom).X*.5f,disc.Radius+3*Scale),text,bottom);
        draw.AddText(disc.Center+new Vector2(-disc.Radius-21*Scale,-ImGui.GetTextLineHeight()*.5f),text,left);
        draw.AddText(disc.Center+new Vector2(disc.Radius+4*Scale,-ImGui.GetTextLineHeight()*.5f),text,right);
        if (!PortraitLightDiagram.TryMarker(disc,angles,lightView,out var marker)) return;
        draw.AddLine(disc.Center,marker,accent,2*Scale);
        draw.AddCircleFilled(marker,8*Scale,ImGui.GetColorU32(FireGazePalette.Panel),24);
        draw.AddCircle(marker,8*Scale,accent,24,2.5f*Scale);
    }

    private void ChangeLightView(PortraitLightView view)
    {
        if (view==lightView) return;
        CancelLightInteraction();
        lightView=view;
    }

    private void CancelLightInteraction()
    {
        if (lightPointerToken==0) return;
        feature.CancelLightDrag(lightPointerToken);
        lightPointerToken=0;
    }
}
