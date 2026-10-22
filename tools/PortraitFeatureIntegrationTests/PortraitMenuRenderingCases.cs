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
            Expect(ImGui.Text.Contains("试换装备、染色和外貌，立即查看自己的铭牌效果。结束预览后还原外观。"),
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
            var transition = PortraitOpeningAvailability.Issue("ChangingArea");
            var combat = PortraitOpeningAvailability.Issue("InCombat");
            var failedRestore = PortraitOpeningAvailability.Issue("RestoreFailed");
            var restoring = PortraitOpeningAvailability.Issue("Restoring");
            feature.OpeningAvailability = PortraitOpeningAvailability.Finish(false, true, false,
                [failedRestore, transition, combat, restoring]);
            ImGui.ResetDraw(); feature.DrawContent();
            Expect(ImGui.Text.Count(text => text == warning) == 1
                && ImGui.Text.Count(text => text == transition.Message) == 1
                && ImGui.Text.Count(text => text == combat.Message) == 1,
                "a restoration warning never hides simultaneous confirmed area-transition and combat reasons");
            Expect(!ImGui.Text.Contains(failedRestore.Message) && !ImGui.Text.Contains(restoring.Message),
                "restoration details appear once while only equivalent restoration blockers are deduplicated");
        }
        using (var feature = new PortraitPreviewFeature(Path.Combine(root, "menu-area-transition.json")))
        {
            feature.SetMenuObservationForTest(false, null);
            var transition = PortraitOpeningAvailability.Issue("ChangingArea");
            var combat = PortraitOpeningAvailability.Issue("InCombat");
            feature.OpeningAvailability = PortraitOpeningAvailability.Finish(false, false, false, [combat, transition]);
            var captures = feature.Native.EditorCaptureCalls;
            ImGui.ResetDraw(); feature.DrawContent();
            Expect(!feature.MenuState.CanOpenEditor && !feature.MenuState.CanOpenPreview
                && ImGui.Text.Count(text => text == transition.Message) == 1 && ImGui.Text.Count(text => text == combat.Message) == 1,
                "real menu disables unsafe entry and explains every cached player blocker once");
            Expect(ImGui.Text.Contains("请勿在传送或切换区域时使用，请等加载结束并站稳。")
                && feature.Native.EditorCaptureCalls == captures,
                "real menu keeps the transition advice visible without native sampling during draw");
            feature.OpeningAvailability = new(true, false, false, []);
            ImGui.ResetDraw(); feature.DrawContent();
            Expect(feature.MenuState.CanOpenEditor && !ImGui.Text.Contains(transition.Message)
                && feature.Native.EditorCaptureCalls == captures,
                "a recovered cached state restores the button without opening or recapturing an editor");
        }
        using (var feature = new PortraitPreviewFeature(Path.Combine(root, "menu-opening-guidance.json")))
        {
            feature.SetMenuObservationForTest(false, null);
            feature.OpeningAvailability = new(false, false, true, []);
            ImGui.ResetDraw(); feature.DrawContent();
            Expect(ImGui.Text.Contains("正在打开铭牌，请稍候。") && !feature.MenuState.CanOpenEditor,
                "pending opening keeps a visible wait explanation with the disabled button");
            feature.SetMenuObservationForTest(true, 2);
            feature.OpeningAvailability = new(false, true, false, []);
            ImGui.ResetDraw(); feature.DrawContent();
            Expect(ImGui.Text.Contains("铭牌编辑器已打开，可以启用 FireGaze 预览。")
                && !feature.MenuState.CanOpenEditor && feature.MenuState.CanOpenPreview,
                "an existing matching editor tells the player to enable preview without reopening it");
            feature.OpeningAvailability = PortraitOpeningAvailability.Finish(false, true, false,
                [PortraitOpeningAvailability.Issue("InCombat")]);
            ImGui.ResetDraw(); feature.DrawContent();
            Expect(!feature.MenuState.CanOpenPreview && !ImGui.Text.Contains("可启用预览")
                && ImGui.Text.Contains("暂时无法启用预览"),
                "a ready editor with a player blocker never advertises preview as ready");
        }
        check(failures.Count == 0, "portrait menu rendering: " + string.Join("; ", failures));
    }
}
