using FireGaze.PortraitPreview;

internal static class PortraitOperationLabelTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var cases = new (string Name, Func<PortraitEditorSnapshot, PortraitEditorSnapshot> Change, string Label)[]
        {
            ("body", s => WithPiece(s, PortraitSlot.Body, new(300, 0, 0)), "更换上衣"),
            ("dye", s => WithPiece(s, PortraitSlot.Body, new(100, 4, 0)), "上衣染色 1"),
            ("hair", s => WithCustomize(s, 6, 2), "修改发型"),
            ("camera", s => s with { Composition = s.Composition with { CameraYaw = .5f } }, "调整镜头"),
            ("rotate", s => s with { Composition = s.Composition with { ImageRotation = 20 } }, "旋转肖像"),
            ("light", s => s with { Composition = s.Composition with { AmbientBrightness = 1 } }, "调整灯光"),
            ("background", s => s with { Composition = s.Composition with { Background = 1 } }, "切换背景"),
            ("expression", s => s with { Composition = s.Composition with { Expression = 1 } }, "更换表情"),
        };
        foreach (var test in cases)
        {
            using var feature = new PortraitPreviewFeature(Path.Combine(root, "operation-label-" + test.Name + ".json"));
            feature.Native.Snapshot = WithPiece(feature.Native.Snapshot, PortraitSlot.Body, new(100, 0, 0));
            feature.Sample();
            feature.Native.Snapshot = test.Change(feature.Native.Snapshot);
            feature.Sample(pointer: true);
            feature.Sample(pointer: false);
            check(feature.History[0].Label == test.Label,
                $"history labels distinguish {test.Name} by the actual changed controls (got {feature.History[0].Label})");
        }

        using (var feature = new PortraitPreviewFeature(Path.Combine(root, "operation-label-gesture.json")))
        {
            feature.Sample();
            feature.EditComposition(c => c with { CameraYaw = .5f }); feature.Sample(pointer: true);
            feature.EditComposition(c => c with { DirectionalBrightness = 3 }); feature.Sample(pointer: true);
            feature.Sample(pointer: false);
            check(feature.History.Count == 2 && feature.History[0].Label == "调整镜头、调整灯光",
                "one continuous gesture preserves both camera and lighting changes instead of only its final sample");
            feature.EditComposition(c => c with { Background = 1 }); feature.Sample(pointer: true); feature.Sample(pointer: false);
            check(feature.History[0].Label == "切换背景", "a new gesture does not inherit the preceding gesture's operation labels");
        }

        using (var feature = new PortraitPreviewFeature(Path.Combine(root, "operation-label-keyboard.json")))
        {
            feature.Sample();
            feature.EditComposition(c => c with { ImageRotation = 12 }); feature.Sample(keyboard: true);
            feature.EditComposition(c => c with { CameraPitch = .1f }); feature.Sample(keyboard: true);
            feature.Sample(keyboard: false); feature.Sample(advanceMilliseconds: 550);
            check(feature.History.Count == 2 && feature.History[0].Label == "调整镜头、旋转肖像",
                "a continuous keyboard burst records its final state with a summary of all changed controls");
        }

        using (var feature = new PortraitPreviewFeature(Path.Combine(root, "operation-label-direct.json")))
        {
            feature.Native.Snapshot = WithPiece(feature.Native.Snapshot, PortraitSlot.Body, new(100, 0, 0));
            feature.Sample();
            feature.Native.Snapshot = WithPiece(feature.Native.Snapshot, PortraitSlot.Body, default);
            feature.RecordDirect("清空装备部位");
            check(feature.History[0].Label == "清空上衣", "direct clear names the changed equipment slot");
            feature.Native.Snapshot = feature.Native.Snapshot with { Look = feature.Native.Snapshot.Look with { WeaponVisible = false } };
            feature.RecordDirect("装备显示设置");
            check(feature.History[0].Label == "隐藏武器", "direct visibility action records its actual visible effect");
        }
    }

    private static PortraitEditorSnapshot WithPiece(PortraitEditorSnapshot source, PortraitSlot slot, PortraitPiece piece)
    {
        var result = source.Copy(); result.Look.Pieces[(int)slot] = piece; return result;
    }
    private static PortraitEditorSnapshot WithCustomize(PortraitEditorSnapshot source, int index, byte value)
    {
        var result = source.Copy(); result.Customize[index] = value; return result;
    }
}
