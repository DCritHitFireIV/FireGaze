using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitPreviewWindow
{
    private bool ownedOrbiting;
    private void DrawOwnedViewport()
    {
        ImGui.TextWrapped(feature.OwnedViewportStatus);
        ImGui.TextDisabled("左键拖动观察 · 滚轮推拉 · 独立观察不会改动肖像取景");
        var available = ImGui.GetContentRegionAvail();
        var size = new Vector2(MathF.Max(280,available.X),MathF.Max(220,MathF.Min(available.Y-90*Scale,480*Scale)));
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("###OwnedPortraitViewport",size,ImGuiButtonFlags.MouseButtonLeft);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin,origin+size,ImGui.GetColorU32(FireGazePalette.PortraitPanel),8*Scale);
        using (var image = feature.TryBorrowOwnedViewport())
        {
            if (image is not null)
            {
                var texture = image.Texture;
                var scale = MathF.Min(size.X/texture.Width,size.Y/texture.Height);
                var fit = new Vector2(texture.Width,texture.Height)*scale;
                var minimum = origin+(size-fit)/2;
                draw.AddImage(texture.Handle,minimum,minimum+fit);
            }
            else draw.AddText(origin+new Vector2(12,12)*Scale,ImGui.GetColorU32(FireGazePalette.Muted),"等待独立人物图像…");
        }
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) ownedOrbiting=true;
        if (feature.OwnedObserver is { } camera)
        {
            var changed=false;
            if(ownedOrbiting && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var delta=ImGui.GetIO().MouseDelta;
                camera=Observe(camera,camera.Yaw+delta.X*MathF.PI/size.Y,
                    Math.Clamp(camera.Pitch-delta.Y*MathF.PI/size.Y,-89*MathF.PI/180,30*MathF.PI/180),camera.Distance);
                changed=delta.LengthSquared()>.000001f;
            }
            if(hovered && MathF.Abs(ImGui.GetIO().MouseWheel)>.001f)
            {
                camera=Observe(camera,camera.Yaw,camera.Pitch,Math.Clamp(camera.Distance-ImGui.GetIO().MouseWheel*.08f,.5f,2f));
                changed=true;
            }
            if(changed)feature.SetOwnedObserver(camera);
        }
        if(!ImGui.IsMouseDown(ImGuiMouseButton.Left))ownedOrbiting=false;
        if(feature.OwnedObserver is null)ImGui.TextDisabled("源相机超出当前独立观察范围；首图仍保留源取景。");
        ImGui.BeginDisabled(feature.OwnedObserver is null||!feature.OwnedViewportExperimentActive);
        if(ImGui.Button("同机位"))feature.ResetOwnedObserver();
        ImGui.SameLine(); if(ImGui.Button("正面"))SetOwnedDirection(0);
        ImGui.SameLine(); if(ImGui.Button("侧面"))SetOwnedDirection(MathF.PI/2);
        ImGui.SameLine(); if(ImGui.Button("背面"))SetOwnedDirection(MathF.PI);
        ImGui.EndDisabled();
        if(ImGui.Button("结束实验／返回取景示意"))feature.CloseOwnedViewportView();
    }
    private void SetOwnedDirection(float yaw)
    {
        if(feature.OwnedObserver is { } camera)feature.SetOwnedObserver(Observe(camera,yaw,camera.Pitch,camera.Distance));
    }
    private static PortraitOwnedSceneObserverCameraSnapshot Observe(PortraitOwnedSceneObserverCameraSnapshot camera,
        float yaw,float pitch,float distance)
    {
        yaw = MathF.IEEERemainder(yaw,MathF.PI*2);
        var offset = new Vector3(MathF.Sin(yaw)*MathF.Cos(pitch),-MathF.Sin(pitch),MathF.Cos(yaw)*MathF.Cos(pitch))*distance;
        return new(camera.Target+offset,camera.Target,yaw,pitch,distance);
    }
}
