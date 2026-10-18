using FireGaze.PortraitPreview;

var passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    passed++; Console.WriteLine("PASS " + name);
}
PortraitEditorSnapshot Initial() => new()
{
    GameBuild = "fixture", Customize = new byte[26],
    Look = new(Enumerable.Repeat(new PortraitPiece(100, 0, 0), 13).ToArray(), true, true, false),
    Composition = new() { BannerTimeline = 1, AnimationProgress = .3f },
};
PortraitEditorSnapshot Piece(PortraitEditorSnapshot source, PortraitSlot slot, PortraitPiece piece)
{
    var result = source.Copy(); result.Look.Pieces[(int)slot] = piece; return result;
}
void Label(Func<PortraitEditorSnapshot, PortraitEditorSnapshot> change, string expected, string name)
{
    var initial = Initial(); var wanted = change(initial.Copy()); var actual = PortraitEditLabels.Describe(initial, wanted);
    Check(actual == expected, $"{name}: expected {expected}, got {actual}");
}

foreach (var test in new (PortraitSlot Slot, string Name)[]
{
    (PortraitSlot.MainHand, "主手武器"), (PortraitSlot.OffHand, "副手装备"), (PortraitSlot.Head, "头饰"),
    (PortraitSlot.Body, "上衣"), (PortraitSlot.Hands, "手部装备"), (PortraitSlot.Legs, "裤装"),
    (PortraitSlot.Feet, "鞋子"), (PortraitSlot.Ears, "耳饰"), (PortraitSlot.Neck, "项链"),
    (PortraitSlot.Wrists, "手镯"), (PortraitSlot.RightRing, "右戒指"), (PortraitSlot.LeftRing, "左戒指"),
    (PortraitSlot.Facewear, "眼镜"),
})
{
    Label(s => Piece(s, test.Slot, new(200, 3, 4)), "更换" + test.Name, "new equipment suppresses incidental dye changes " + test.Slot);
    Label(s => Piece(s, test.Slot, default), "清空" + test.Name, "clear identifies its equipment slot " + test.Slot);
}
Label(s => Piece(s, PortraitSlot.Body, new(100, 4, 0)), "上衣染色 1", "first dye identifies its channel");
Label(s => Piece(s, PortraitSlot.Body, new(100, 0, 4)), "上衣染色 2", "second dye identifies its channel");
Label(s => Piece(s, PortraitSlot.Body, new(100, 4, 5)), "上衣染色", "two dye changes have one concise slot label");
Label(s => s with { Look = s.Look with { HeadVisible = false } }, "隐藏头饰", "head visibility intent");
Label(s => s with { Look = s.Look with { WeaponVisible = false } }, "隐藏武器", "weapon visibility intent");
Label(s => s with { Look = s.Look with { Visor = true } }, "合上面罩", "visor uses visible intent");
Label(s => s with { HideVieraEars = true }, "隐藏兔耳", "ear visibility is not omitted");

foreach (var test in new (Func<PortraitComposition, PortraitComposition> Change, string Label, string Name)[]
{
    (c => c with { CameraYaw = .2f }, "调整镜头", "yaw"),
    (c => c with { CameraPitch = .2f }, "调整镜头", "pitch"),
    (c => c with { CameraDistance = 2 }, "调整镜头", "distance"),
    (c => c with { CameraZoomNormalized = .2f }, "调整镜头", "precise zoom"),
    (c => c with { CameraPosition = new(1,2,3,4) }, "调整镜头", "position"),
    (c => c with { CameraTarget = new(1,2,3,4) }, "调整镜头", "target"),
    (c => c with { CameraZoom = 5 }, "调整镜头", "native zoom"),
    (c => c with { ImageRotation = 15 }, "旋转肖像", "rotation"),
    (c => c with { BannerTimeline = 2 }, "更换姿势", "pose"),
    (c => c with { PoseClassJob = 2 }, "更换姿势", "pose job"),
    (c => c with { Expression = 2 }, "更换表情", "expression"),
    (c => c with { HeadDirection = new(1,2) }, "调整头部朝向", "head direction"),
    (c => c with { EyeDirection = new(1,2) }, "调整视线", "eye direction"),
    (c => c with { HeadFacingCamera = true, HeadDirection = new(1,2) }, "头部面向镜头", "head follow suppresses its incidental direction"),
    (c => c with { EyesFacingCamera = true, EyeDirection = new(1,2) }, "眼睛面向镜头", "eye follow suppresses its incidental direction"),
    (c => c with { Paused = true }, "暂停动作", "pause"),
    (c => c with { CharacterVisible = false }, "隐藏角色", "character visibility"),
    (c => c with { Background = 2 }, "切换背景", "background"),
    (c => c with { Frame = 2 }, "更换边框", "frame"),
    (c => c with { Decoration = 2 }, "更换装饰", "decoration"),
    (c => c with { DirectionalRed = 1 }, "调整灯光", "directional red"),
    (c => c with { DirectionalGreen = 1 }, "调整灯光", "directional green"),
    (c => c with { DirectionalBlue = 1 }, "调整灯光", "directional blue"),
    (c => c with { DirectionalBrightness = 1 }, "调整灯光", "directional brightness"),
    (c => c with { DirectionalVerticalAngle = 1 }, "调整灯光", "vertical light"),
    (c => c with { DirectionalHorizontalAngle = 1 }, "调整灯光", "horizontal light"),
    (c => c with { AmbientRed = 1 }, "调整灯光", "ambient red"),
    (c => c with { AmbientGreen = 1 }, "调整灯光", "ambient green"),
    (c => c with { AmbientBlue = 1 }, "调整灯光", "ambient blue"),
    (c => c with { AmbientBrightness = 1 }, "调整灯光", "ambient brightness"),
}) Label(s => s with { Composition = test.Change(s.Composition) }, test.Label, test.Name);

var playing = Initial();
Check(PortraitEditLabels.Describe(playing, playing with { Composition = playing.Composition with { AnimationProgress = .8f } }) == "编辑肖像",
    "passive playback never claims the player adjusted the timeline");
var paused = playing with { Composition = playing.Composition with { Paused = true } };
Check(PortraitEditLabels.Describe(paused, paused with { Composition = paused.Composition with { AnimationProgress = .8f } }) == "调整动作进度",
    "paused timeline seek is a distinct edit");
Check(PortraitEditLabels.Describe(paused, paused with { Composition = paused.Composition with { Paused = false } }) == "播放动作",
    "playback restart is named without a false timeline seek");

foreach (var field in new[] { ("hairColor", "发色"), ("eyebrows", "眉毛"), ("lipColor", "唇色"), ("furPattern", "毛皮花纹"), ("feature1", "黑痣与伤痕 1") })
{
    var before = Initial(); var after = before.Copy(); after.Customize[10] = 3;
    PortraitAppearanceGroup[] groupsBefore = [new(field.Item1, field.Item2, 1, [])];
    PortraitAppearanceGroup[] groupsAfter = [new(field.Item1, field.Item2, 3, [])];
    Check(PortraitEditLabels.Describe(before, after, groupsBefore, groupsAfter) == "修改" + field.Item2,
        "beauty labels use the actual race-specific catalogue field " + field.Item1);
}
Label(s => { s.Customize[10] = 3; return s; }, "修改外观", "an uncatalogued appearance edit avoids guessing its meaning");
Label(s => Piece(s with { Composition = s.Composition with { CameraYaw = 1, AmbientBrightness = 2 } }, PortraitSlot.Body, new(300, 0, 0)),
    "更换上衣、调整镜头、调整灯光", "compound edit retains all three changed operation categories");
Label(s => Piece(s with { Composition = s.Composition with { CameraYaw = 1, AmbientBrightness = 2, Background = 1, Frame = 2 } }, PortraitSlot.Body, new(300, 0, 0)),
    "更换上衣、调整镜头、调整灯光等 5 项修改", "large compound edit has a bounded readable summary");
Check(PortraitEditLabels.Describe(null, Initial()) == "编辑肖像", "missing baseline does not invent an operation");
Console.WriteLine($"PortraitOperationLabelTests: {passed} passed, 0 failed");
