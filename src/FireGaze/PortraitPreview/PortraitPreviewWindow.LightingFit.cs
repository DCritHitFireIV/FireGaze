using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private bool showLightingFit;
    private readonly PortraitLightingFitControls lightingControls = new();
    private int lightingChannel;
    private Vector2? lightingRegionStart;
    private string lightingRegionSource = "";
    private bool lightingSubjectCurrent;

    private void DrawLightingFitWindow()
    {
        if (!showLightingFit) return;
        PrepareLightingFitWindow();
        if (FireGazeWindowPlacement.Begin("色彩曲线###FireGazePortraitLightingFit", ref showLightingFit, "FireGazePortraitLightingFit"))
        {

            var state = feature.LightingFit;
            var busy = state?.Busy == true;
            ImGui.TextWrapped("设置目标色调，用主光与环境光尽量接近。部分色彩无法通过灯光还原。");
            BindLightingSubject(state);
            using (UiHelpers.Disabled(busy))
            {
                for (var channel=0;channel<3;channel++)
                {
                    if (channel>0) ImGui.SameLine();
                    if (DrawTab(new[]{"红","绿","蓝"}[channel],lightingChannel==channel,true)) lightingChannel=channel;
                }
                DrawLightingCurve();
                if (ImGui.Button("重置曲线")) lightingControls.ResetCurves();
                ImGui.TextWrapped("暂停动作，在下方框选角色的受光区域，避开背景、边框与装饰。");
                DrawLightingSubject(busy, state);
            }
            var ready=state?.CanStart==true && lightingSubjectCurrent && lightingControls.Region is not null;
            using (UiHelpers.Disabled(!ready))
                if (ImGui.Button("匹配灯光") && lightingControls.Region is { } region && lightingControls.TryTarget(out var target))
                    feature.StartLightingFit(target!,region,state!.FixedStateKey);
            ImGui.SameLine();
            using (UiHelpers.Disabled(!busy))
                if (ImGui.Button("取消")) feature.CancelLightingFit();
            if (!string.IsNullOrWhiteSpace(state?.Status)) ImGui.TextWrapped(state.Status);
            else ImGui.TextDisabled("请打开肖像预览并暂停动作。");
            ImGui.TextDisabled("仅本地预览。结果可在编辑记录中撤回。");
        }
        ImGui.End();
        if (!showLightingFit) { BindLightingSubject(null); feature.CancelLightingFit(); }
    }

    private void BindLightingSubject(PortraitLightingFitView? state)
    {
        var key = state is null ? "" : state.EditorOwnerKey + ":" + state.SourceKey + ":" + state.FixedStateKey;
        if (key != lightingRegionSource) lightingRegionStart = null;
        lightingRegionSource = key;
        lightingControls.BindSource(key);
    }

    private void DrawLightingCurve()
    {
        var values=lightingControls.GetPoints(lightingChannel);
        var color=new[]{FireGazePalette.ChartRed,FireGazePalette.ChartGreen,FireGazePalette.ChartBlue}[lightingChannel];
        var origin=ImGui.GetCursorScreenPos();
        var size=new Vector2(Math.Max(1,ImGui.GetContentRegionAvail().X),150*Scale);
        ImGui.Dummy(size);
        var draw=ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin,origin+size,ImGui.GetColorU32(FireGazePalette.Panel));
        for(var grid=0;grid<=4;grid++)
        {
            var fraction=grid/4f;
            var guide=ImGui.GetColorU32(FireGazePalette.Muted with { W=.22f });
            draw.AddLine(origin+new Vector2(size.X*fraction,0),origin+new Vector2(size.X*fraction,size.Y),guide);
            draw.AddLine(origin+new Vector2(0,size.Y*fraction),origin+new Vector2(size.X,size.Y*fraction),guide);
        }
        var output=new[]{0,values[0],values[1],values[2],255};
        var input=new[]{0,64,128,192,255};
        for(var index=1;index<5;index++)
        {
            var from=origin+new Vector2(input[index-1]/255f*size.X,(1-output[index-1]/255f)*size.Y);
            var to=origin+new Vector2(input[index]/255f*size.X,(1-output[index]/255f)*size.Y);
            draw.AddLine(from,to,ImGui.GetColorU32(color),2*Scale);
            if(index<4)draw.AddCircleFilled(to,4*Scale,ImGui.GetColorU32(color));
        }
        for(var index=0;index<3;index++)
        {
            var value=values[index];
            ImGui.SetNextItemWidth(Math.Max(80*Scale,ImGui.GetContentRegionAvail().X-70*Scale));
            if(ImGui.SliderInt(new[]{"暗部","中间调","亮部"}[index],ref value,index==0?0:values[index-1],index==2?255:values[index+1]))
                lightingControls.SetPoint(lightingChannel,index,value);
        }
    }

    private void DrawLightingSubject(bool busy, PortraitLightingFitView? state)
    {
        lightingSubjectCurrent=false;
        using var live=feature.AcquireCurrentLiveImage();
        if(live is null || live.Sample.Scope!=PortraitImageScope.Portrait || state is null
            || live.Sample.StateKey!=state.CaptureStateKey)
        { lightingRegionStart=null; ImGui.TextDisabled("等待肖像图像…");return; }
        lightingSubjectCurrent=true;
        var width=Math.Min(ImGui.GetContentRegionAvail().X,230*Scale);
        var size=new Vector2(width,width*live.Height/live.Width);
        var origin=ImGui.GetCursorScreenPos();
        ImGui.Image(live.Texture.Handle,size);
        if(!busy && ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            lightingRegionStart=Vector2.Clamp((ImGui.GetMousePos()-origin)/size,Vector2.Zero,Vector2.One);
        var region=lightingControls.Region;
        if(lightingRegionStart is { } start)
        {
            var end=Vector2.Clamp((ImGui.GetMousePos()-origin)/size,Vector2.Zero,Vector2.One);
            if(PortraitSampleRegion.TryFromDrag(start,end,out var proposed)) region=proposed;
            if(!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            { lightingControls.SelectRegion(start,end);lightingRegionStart=null; }
        }
        if(region is { } selected)
            ImGui.GetWindowDrawList().AddRect(origin+new Vector2(selected.Left,selected.Top)*size,
                origin+new Vector2(selected.Right,selected.Bottom)*size,ImGui.GetColorU32(FireGazePalette.Focus),0,ImDrawFlags.None,2*Scale);
    }
}
