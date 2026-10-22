namespace FireGaze.PortraitPreview;

/// <summary>Names the final changes of one edit without replaying input or retaining native objects.</summary>
internal static class PortraitEditLabels
{
    private static readonly string[] SlotNames =
        ["主手武器", "副手装备", "头饰", "上衣", "手部装备", "裤装", "鞋子", "耳饰", "项链", "手镯", "右戒指", "左戒指", "眼镜"];

    public static string Describe(PortraitEditorSnapshot? before, PortraitEditorSnapshot after,
        IReadOnlyList<PortraitAppearanceGroup>? beforeAppearance = null,
        IReadOnlyList<PortraitAppearanceGroup>? afterAppearance = null)
    {
        if (before is null) return "编辑肖像";
        var changes = new List<string>();
        for (var slot = 0; slot < SlotNames.Length; slot++)
        {
            var oldPiece = before.Look.Pieces[slot]; var newPiece = after.Look.Pieces[slot];
            if (oldPiece.ItemID != newPiece.ItemID)
                changes.Add((newPiece.ItemID == 0 ? "清空" : "更换") + SlotNames[slot]);
            else if (newPiece.ItemID != 0 && (oldPiece.Stain0 != newPiece.Stain0 || oldPiece.Stain1 != newPiece.Stain1))
                changes.Add(SlotNames[slot] + "染色" + (oldPiece.Stain0 != newPiece.Stain0 && oldPiece.Stain1 != newPiece.Stain1
                    ? "" : oldPiece.Stain0 != newPiece.Stain0 ? " 1" : " 2"));
        }
        if (before.Look.HeadVisible != after.Look.HeadVisible) changes.Add(after.Look.HeadVisible ? "显示头饰" : "隐藏头饰");
        if (before.Look.WeaponVisible != after.Look.WeaponVisible) changes.Add(after.Look.WeaponVisible ? "显示武器" : "隐藏武器");
        if (before.Look.Visor != after.Look.Visor) changes.Add(after.Look.Visor ? "合上面罩" : "打开面罩");
        if (!before.Customize.SequenceEqual(after.Customize))
        {
            var named = false;
            if (beforeAppearance is not null && afterAppearance is not null)
            foreach (var group in afterAppearance)
            {
                var oldGroup = beforeAppearance.FirstOrDefault(old => old.ID == group.ID);
                if (oldGroup is null || oldGroup.CurrentValue == group.CurrentValue) continue;
                changes.Add("修改" + group.Label); named = true;
            }
            if (!named) changes.Add("修改外观");
        }
        if (before.HideVieraEars != after.HideVieraEars) changes.Add(after.HideVieraEars ? "隐藏兔耳" : "显示兔耳");

        var a = before.Composition; var b = after.Composition;
        var poseChanged = a.BannerTimeline != b.BannerTimeline || a.PoseClassJob != b.PoseClassJob;
        if (poseChanged) changes.Add("更换姿势");
        else if (a.Paused && b.Paused && a.AnimationProgress != b.AnimationProgress) changes.Add("调整动作进度");
        if (a.Paused != b.Paused) changes.Add(b.Paused ? "暂停动作" : "播放动作");
        if (a.Expression != b.Expression) changes.Add("更换表情");
        if (a.HeadFacingCamera != b.HeadFacingCamera) changes.Add(b.HeadFacingCamera ? "头部面向镜头" : "取消头部面向镜头");
        else if (a.HeadDirection != b.HeadDirection) changes.Add("调整头部朝向");
        if (a.EyesFacingCamera != b.EyesFacingCamera) changes.Add(b.EyesFacingCamera ? "眼睛面向镜头" : "取消眼睛面向镜头");
        else if (a.EyeDirection != b.EyeDirection) changes.Add("调整视线");
        if (a.CameraPosition != b.CameraPosition || a.CameraTarget != b.CameraTarget || a.CameraYaw != b.CameraYaw
            || a.CameraPitch != b.CameraPitch || a.CameraDistance != b.CameraDistance
            || a.CameraZoomNormalized != b.CameraZoomNormalized || a.CameraZoom != b.CameraZoom) changes.Add("调整镜头");
        if (a.ImageRotation != b.ImageRotation) changes.Add("旋转肖像");
        if (a.DirectionalRed != b.DirectionalRed || a.DirectionalGreen != b.DirectionalGreen || a.DirectionalBlue != b.DirectionalBlue
            || a.DirectionalBrightness != b.DirectionalBrightness || a.DirectionalVerticalAngle != b.DirectionalVerticalAngle
            || a.DirectionalHorizontalAngle != b.DirectionalHorizontalAngle || a.AmbientRed != b.AmbientRed
            || a.AmbientGreen != b.AmbientGreen || a.AmbientBlue != b.AmbientBlue || a.AmbientBrightness != b.AmbientBrightness) changes.Add("调整灯光");
        if (a.Background != b.Background) changes.Add("切换背景");
        if (a.Frame != b.Frame) changes.Add("更换边框");
        if (a.Decoration != b.Decoration) changes.Add("更换装饰");
        if (a.CharacterVisible != b.CharacterVisible) changes.Add(b.CharacterVisible ? "显示角色" : "隐藏角色");
        return changes.Count switch
        {
            0 => "编辑肖像",
            <= 3 => string.Join("、", changes),
            _ => string.Join("、", changes.Take(3)) + $"等 {changes.Count} 项修改",
        };
    }
}
