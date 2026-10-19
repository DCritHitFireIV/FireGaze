using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;

namespace FireGaze.PortraitPreview;

/// <summary>Interactive reference space inspired by scene editors, with native portrait write-back.</summary>
internal sealed partial class PortraitPreviewWindow
{
    private bool showSpaceLight;
    private PortraitLightingSpace? lightingSpace;
    private PortraitComposition? lightingSpaceSource;
    private PortraitLightingSpaceBinding? lightingSpaceBinding;
    private bool spaceOrbiting;
    private bool spaceLightDragging;
    private readonly PortraitSpaceWheelBatch spaceDolly = new();

    public void OpenLightingSpace() => showSpaceLight = true;

    private void DrawLightingSpaceWindow()
    {
        if (!showSpaceLight) return;
        ImGui.SetNextWindowSizeConstraints(new Vector2(480, 380) * Scale, new Vector2(900, 780) * Scale);
        if (!ImGui.Begin("空间灯光参照###FireGazePortraitLightingSpace", ref showSpaceLight))
        {
            ImGui.End();
            return;
        }

        var binding = feature.CurrentLightingSpaceBinding;
        var composition = binding?.Composition;
        if (composition is null)
        {
            lightingSpace = null;
            lightingSpaceSource = null;
            lightingSpaceBinding = null;
            ImGui.TextWrapped("请先打开肖像同步预览，等待肖像模型加载完成。");
            ImGui.End();
            return;
        }
        if (lightingSpace is null || lightingSpaceSource is null || lightingSpaceBinding is null
            || !spaceOrbiting && !spaceLightDragging && !PortraitLightingSpaceBinding.MatchesSnapshot(lightingSpaceBinding.Snapshot, binding!.Snapshot)
            || lightingSpaceSource.CameraTarget != composition.CameraTarget
            || lightingSpaceSource.CameraYaw != composition.CameraYaw || lightingSpaceSource.CameraPitch != composition.CameraPitch
            || lightingSpaceSource.CameraDistance != composition.CameraDistance
            || !spaceOrbiting && !spaceLightDragging && lightingSpaceSource.DirectionalVerticalAngle != composition.DirectionalVerticalAngle
            || !spaceOrbiting && !spaceLightDragging && lightingSpaceSource.DirectionalHorizontalAngle != composition.DirectionalHorizontalAngle)
        {
            lightingSpace = PortraitLightingSpace.From(composition);
            lightingSpaceSource = composition;
            lightingSpaceBinding = binding;
        }

        ImGui.TextWrapped("在这个参照空间中拖动相机和主光，调整结果会写回当前肖像预览。人物容器与光点是取景参考；游戏原生仍只有主光方向和环境光。");
        ImGui.TextDisabled("左键拖动相机 · 滚轮推拉 · 右键拖动主光");
        var available = ImGui.GetContentRegionAvail();
        var viewport = new Vector2(MathF.Max(280, available.X), MathF.Max(220, MathF.Min(available.Y - 112 * Scale, 480 * Scale)));
        DrawLightingSpaceViewport(viewport);
        ImGui.Separator();
        var camera = lightingSpace.Camera;
        ImGui.TextUnformatted($"相机  方位 {camera.AzimuthRadians * 180 / MathF.PI:F0}° · 俯仰 {camera.ElevationRadians * 180 / MathF.PI:F0}° · 距离 {camera.Distance:F2}");
        var light = lightingSpace.DirectionalLight;
        ImGui.TextUnformatted($"主光  垂直 {light.VerticalDegrees:F0}° · 水平 {light.HorizontalDegrees:F0}°");
        if (ImGui.Button("应用到肖像"))
        {
            CommitLightingSpace();
        }
        ImGui.SameLine();
        if (ImGui.Button("重置参照"))
        {
            lightingSpace = PortraitLightingSpace.From(composition);
            lightingSpaceSource = composition;
            lightingSpaceBinding = binding;
        }
        ImGui.SameLine();
        if (ImGui.Button("主光方向")) showLight = true;
        ImGui.TextDisabled("空间灯光每次完成一段拖动会写入一条历史记录，可撤回。");
        ImGui.End();
        if (!showSpaceLight)
        {
            lightingSpace = null;
            lightingSpaceSource = null;
            lightingSpaceBinding = null;
            spaceOrbiting = spaceLightDragging = false;
            spaceDolly.Cancel();
        }
    }

    private void DrawLightingSpaceViewport(Vector2 size)
    {
        if (lightingSpace is null) return;
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("###PortraitLightingSpaceViewport", size,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(FireGazePalette.PortraitPanel), 8 * Scale);
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(FireGazePalette.PortraitBorder), 8 * Scale, ImDrawFlags.None, Scale);

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) spaceOrbiting = true;
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) spaceLightDragging = true;
        if (spaceOrbiting && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            lightingSpace.Orbit(ImGui.GetIO().MouseDelta, size.Y);
        if (spaceLightDragging && ImGui.IsMouseDown(ImGuiMouseButton.Right))
            lightingSpace.TrySetLightFromViewport(Vector2.Clamp((ImGui.GetMousePos() - origin) / size, Vector2.Zero, Vector2.One));
        var wheel = ImGui.GetIO().MouseWheel;
        if (hovered && MathF.Abs(wheel) > .001f)
        {
            lightingSpace.Dolly(wheel);
            spaceDolly.Observe(wheel);
        }
        else if (spaceDolly.TryCommit(wheel, spaceOrbiting || spaceLightDragging))
        {
            CommitLightingSpace();
        }
        if (spaceOrbiting && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            spaceOrbiting = false;
            CommitLightingSpace();
        }
        if (spaceLightDragging && !ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            spaceLightDragging = false;
            CommitLightingSpace();
        }

        var center = new Vector3(0, 0, 0);
        var head = new Vector3(0, .78f, 0);
        var shoulders = new Vector3(0, .42f, 0);
        var feet = new Vector3(0, -.78f, 0);
        var headLeft = new Vector3(-.18f, .78f, 0);
        var headRight = new Vector3(.18f, .78f, 0);
        var bodyLeft = new Vector3(-.3f, .35f, 0);
        var bodyRight = new Vector3(.3f, .35f, 0);
        var feetLeft = new Vector3(-.2f, -.78f, 0);
        var feetRight = new Vector3(.2f, -.78f, 0);
        void Line(Vector3 a, Vector3 b, uint color, float width = 1.5f)
        {
            var pa = lightingSpace.Project(a, size);
            var pb = lightingSpace.Project(b, size);
            if (pa.IsVisible && pb.IsVisible) draw.AddLine(origin + pa.Screen, origin + pb.Screen, color, width * Scale);
        }
        var subjectColor = ImGui.GetColorU32(FireGazePalette.Action);
        var guideColor = ImGui.GetColorU32(FireGazePalette.PortraitBorder);
        var lightColor = ImGui.GetColorU32(FireGazePalette.BrandEmber);
        Line(headLeft, headRight, subjectColor, 3);
        Line(headLeft, shoulders, subjectColor, 3);
        Line(headRight, shoulders, subjectColor, 3);
        Line(shoulders, bodyLeft, subjectColor, 3);
        Line(shoulders, bodyRight, subjectColor, 3);
        Line(bodyLeft, feetLeft, subjectColor, 3);
        Line(bodyRight, feetRight, subjectColor, 3);
        Line(feetLeft, feetRight, subjectColor, 3);
        Line(new(-1.25f, -.86f, 0), new(1.25f, -.86f, 0), guideColor);
        Line(new(-1.25f, -.86f, -1.0f), new(1.25f, -.86f, -1.0f), guideColor);
        Line(new(-1.25f, -.86f, 1.0f), new(1.25f, -.86f, 1.0f), guideColor);
        Line(new(-1.25f, -.86f, -1.0f), new(-1.25f, -.86f, 1.0f), guideColor);
        Line(new(1.25f, -.86f, -1.0f), new(1.25f, -.86f, 1.0f), guideColor);

        var subject = lightingSpace.Project(center, size);
        var anchor = lightingSpace.Project(lightingSpace.LightAnchor(), size);
        if (subject.IsVisible && anchor.IsVisible)
        {
            draw.AddLine(origin + subject.Screen, origin + anchor.Screen, lightColor, 2 * Scale);
            draw.AddCircleFilled(origin + anchor.Screen, 8 * Scale, lightColor);
            draw.AddText(origin + anchor.Screen + new Vector2(10, -8) * Scale,
                ImGui.GetColorU32(FireGazePalette.Text), "主光");
        }
        var camera = lightingSpace.Project(lightingSpace.CameraOffset(), size);
        if (camera.IsVisible)
        {
            draw.AddCircleFilled(origin + camera.Screen, 5 * Scale, ImGui.GetColorU32(FireGazePalette.Focus));
            draw.AddText(origin + camera.Screen + new Vector2(8, 5) * Scale,
                ImGui.GetColorU32(FireGazePalette.Text), "相机");
        }
        if (subject.IsVisible)
            draw.AddCircleFilled(origin + subject.Screen, 4 * Scale, ImGui.GetColorU32(FireGazePalette.Text));
        draw.AddText(origin + new Vector2(12, 12) * Scale, ImGui.GetColorU32(FireGazePalette.Muted),
            "人物容器  ·  参照空间");
    }

    private void CommitLightingSpace()
    {
        if (lightingSpace is null || lightingSpaceBinding is not { } expected) return;
        feature.ApplyLightingSpace(expected, lightingSpace.ApplyTo(expected.Composition));
    }
}
