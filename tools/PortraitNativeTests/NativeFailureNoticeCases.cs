using FireGaze.PortraitPreview;
using System.Reflection;

internal static unsafe class NativeFailureNoticeCases
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var rollbackFails in new[] { false, true })
        {
            using var fixture = new Fixture();
            var notices = new List<string>();
            using var target = new NativePortraitTarget(fixture, _ => true, notices.Add);
            target.TryResolve(out var identity, out _, out _);
            var changed = target.Capture(identity)!.Copy();
            changed.Pieces[3] = new(777, 9, 10);
            fixture.ThrowItem = true;
            fixture.ThrowGlasses = rollbackFails;
            var applied = target.Apply(identity, changed);
            check(!applied && notices.Count == 1 && notices[0].Contains("装备") && notices[0].Contains("重试")
                && !notices[0].Contains("native test failure") && !notices[0].Contains("diagnostic")
                && !notices[0].Contains("保持不变") && !notices[0].Contains("已还原"),
                $"wardrobe failure rollback={rollbackFails}: player receives one actionable notice without native detail or unproven restoration claims");
            check(target.LastError?.Contains("native test failure") == true
                && (!rollbackFails || target.LastError.Contains("rollback glasses diagnostic sentinel")),
                $"wardrobe failure rollback={rollbackFails}: diagnostics preserve original and rollback exceptions");
        }

        foreach (var reentrant in new[] { false, true })
        {
            using var fixture = new Fixture();
            var notices = new List<string>();
            var mirrorApi = new RefusalMirrorApi { ThrowClose = true };
            var mirror = new PortraitPlateMirror(mirrorApi);
            var target = new NativePortraitTarget(fixture, _ => false, notices.Add);
            typeof(NativePortraitTarget).GetField("plateMirror", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, mirror);
            if (reentrant) mirrorApi.AfterInstallImage = () => { mirrorApi.RefuseWrite = true; target.Dispose(); };
            mirror.Update(new(1, 2, 3, 4, 5), new PortraitEditorSnapshot(), true);
            if (!reentrant) { mirrorApi.RefuseWrite = true; target.Dispose(); }
            target.Dispose();
            check(!target.PlateMirrorDisposalRestored && notices.Count == 1
                && notices[0].Contains("铭牌") && notices[0].Contains("关闭")
                && !notices[0].Contains("diagnostic sentinel"),
                $"mirror teardown reentrant={reentrant}: one player recovery notice excludes underlying exception");
            check(target.LastError?.Contains("mirror close diagnostic sentinel") == true && mirrorApi.Releases == 1,
                $"mirror teardown reentrant={reentrant}: diagnostic evidence and native release ownership survive notice sanitization");
        }
    }
}
