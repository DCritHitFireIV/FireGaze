using FireGaze.PortraitPreview;

internal static class NativeLiveFeatureWiringCases
{
    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
        test("preview lease lifecycle gives each new session one image start and never carries an old frame", () =>
        {
            var target = new SessionTarget();
            var session = new PortraitPreviewSession(target);
            var source = new Source(); var now = 0L;
            using var controller = new PortraitNativeLiveController(source,
                () => new(Source.KeyA.Editor, Source.KeyA.Owner, session.BlockSave(Source.KeyA.Editor), true, "off", true, false), () => now);
            check(session.Open(Source.KeyA.Editor)
                && controller.TryStartSessionOnce(session.Revision, Source.KeyA.Editor),
                "the first successful preview lease can start its image");
            controller.Update();
            check(controller.TryBorrow(out var old), "the first session publishes its native image");
            check(session.Open(Source.KeyA.Editor)
                && !controller.TryStartSessionOnce(session.Revision, Source.KeyA.Editor),
                "opening the current preview window does not restart the image");
            controller.Stop(PortraitNativeLiveStopReason.UserClosed);
            check(session.Close() && !old!.TryGetImage(out _), "closing restores the lease and retires its frame");
            check(session.Open(Source.KeyA.Editor)
                && !controller.TryStartSessionOnce(session.Revision, Source.KeyA.Editor)
                && !controller.TryBorrow(out _),
                "a new preview waits for old Draw retirement without displaying the previous session image");
            old!.Dispose(); now = 250;
            check(controller.TryStartSessionOnce(session.Revision, Source.KeyA.Editor), "the new preview starts silently after retirement");
            controller.Update();
            check(controller.TryBorrow(out var fresh), "the new session publishes only after a fresh acquisition");
            fresh!.Dispose();
        });
    }

    private sealed class SessionTarget : IPortraitPreviewTarget
    {
        public bool IsCurrent(PortraitIdentity identity) => identity == Source.KeyA.Editor;
        public PortraitLook? Capture(PortraitIdentity identity) => IsCurrent(identity)
            ? new(new PortraitPiece[13], true, true, false) : null;
        public bool Apply(PortraitIdentity identity, PortraitLook look) => IsCurrent(identity);
    }
}
