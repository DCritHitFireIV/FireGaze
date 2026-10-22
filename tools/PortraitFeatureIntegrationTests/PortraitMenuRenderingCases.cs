using Dalamud.Bindings.ImGui;
using FireGaze.PortraitPreview;

internal static class PortraitMenuRenderingCases
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var failures = new List<string>();
        void Expect(bool passed, string name)
        {
            if (passed) check(true, name);
            else { failures.Add(name); Console.WriteLine("FAIL " + name); }
        }

        foreach (var (available, entry, expected) in new (bool, byte?, string)[]
        {
            (false, 2, "未打开"), (true, null, "加载中"),
            (true, 0, "普通肖像"), (true, 1, "套装"), (true, 2, "铭牌"),
            (true, byte.MaxValue, "未知"),
        })
        {
            using var feature = new PortraitPreviewFeature(Path.Combine(root, "menu-type-" + expected + ".json"));
            feature.SetMenuObservationForTest(available, entry);
            var captures = feature.Native.EditorCaptureCalls;
            ImGui.ResetDraw();
            feature.DrawContent();
            Expect(ImGui.Text.All(text => !text.StartsWith("编辑器：", StringComparison.Ordinal)),
                "real menu draw omits implementation status summary: " + expected);
            Expect(ImGui.Text.Contains("试换装备、染色和外貌，在预览实际的本人铭牌效果。关闭预览后外观才会还原。"),
                "real menu draw uses concise preview purpose copy: " + expected);
            Expect(feature.Native.EditorCaptureCalls == captures,
                "rendering cached editor type performs no native snapshot capture: " + expected);
            Expect(ImGui.Controls.All(label => !new[] { "撤回", "重做", "历史记录", "同步铭牌预览" }.Contains(label)),
                "main menu does not render preview-window edit controls: " + expected);
        }

        using (var feature = new PortraitPreviewFeature(Path.Combine(root, "menu-restore-warning.json")))
        {
            const string warning = "本地铭牌预览尚未还原，正在重试。";
            feature.SetMenuObservationForTest(true, 2, warning);
            ImGui.ResetDraw();
            feature.DrawContent();
            Expect(ImGui.Text.Count(text => text == warning) == 1,
                "real menu draw renders a retained restoration failure exactly once");
            Expect(ImGui.Text.Count(text => text == "还原异常") == 1,
                "restoration failure retains one visible status heading");
        }
        check(failures.Count == 0, "portrait menu rendering: " + string.Join("; ", failures));
    }
}
