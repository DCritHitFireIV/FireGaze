using FireGaze.PortraitPreview;

internal static unsafe class EditorFailureNoticeCases
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var failure in new[] { "wardrobe", "customize", "pose" })
        {
            using var fixture = new EditorFixture();
            var notices = new List<string>();
            using var target = new NativePortraitTarget(fixture, _ => true, notices.Add);
            target.TryResolve(out var identity, out _, out _);
            var before = target.CaptureEditorState(identity)!;
            var changed = before.Copy();
            var diagnostic = "";
            switch (failure)
            {
                case "wardrobe": changed.Look.Pieces[3] = new(778, 4, 5); fixture.ThrowItem = true; diagnostic = "wardrobe diagnostic sentinel"; break;
                case "customize": changed.Customize[6] = 8; fixture.ThrowCustomize = true; diagnostic = "customize failure"; break;
                case "pose": changed = changed with { Composition = changed.Composition with { BannerTimeline = 89 } }; fixture.ThrowPose = true; diagnostic = "pose failure"; break;
            }
            var applied = target.ApplyEditorState(identity, changed);
            check(!applied && notices.Count == 1 && notices[0].Contains("肖像") && notices[0].Contains("重试")
                && !notices[0].Contains(diagnostic) && !notices[0].Contains("保持不变") && !notices[0].Contains("已还原"),
                $"editor {failure} native failure: one actionable player notice omits technical details and unproven restoration claims");
            check(target.LastError?.Contains(diagnostic) == true,
                $"editor {failure} native failure: original exception remains available to development diagnostics");
            check(target.CaptureEditorState(identity)!.SameForHistory(before),
                $"editor {failure} native failure: sanitizing the notice preserves existing rollback behavior");
        }
    }
}
