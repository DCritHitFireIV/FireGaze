using FireGaze.PortraitPreview;

internal static class NativeLiveControllerCases
{
    public static void Run(Action<string, Action> test, Action<bool, string> check)
    {
#if !HAS_NATIVE_LIVE_CONTROLLER
        test("live controller never auto-acquires before an explicit bound enable", () =>
            check(false, "missing explicit identity-bound controller; the raw view retries after acquisition failure and source invalidation"));
#else
        test("an active preview silently retries its initial busy image start", () =>
        {
            var source = new Source(); var reads = 0; var now = 0L; var context = Ready() with { CapturesDrained = false };
            using var controller = new PortraitNativeLiveController(source, () => { reads++; return context; }, () => now);
            check(!controller.TryStartSessionOnce(1, Source.KeyA.Editor), "a busy session cannot acquire an image");
            context = Ready();
            now = 250;
            check(controller.TryStartSessionOnce(1, Source.KeyA.Editor),
                "the same active session must retry after readiness recovers without a player click");
            controller.Update();
            check(controller.TryBorrow(out var fresh), "recovered readiness publishes a current native image");
            fresh?.Dispose();
        });
        test("a temporary native texture miss retries silently within the same session", () =>
        {
            var source = new Source { Fail = true }; var now = 0L;
            using var controller = new PortraitNativeLiveController(source, Ready, () => now);
            check(controller.TryStartSessionOnce(1, Source.KeyA.Editor), "a ready session starts once");
            controller.Update();
            check(!controller.TryBorrow(out _), "missing texture never exposes a previous image");
            source.Fail = false;
            now = 250;
            check(controller.TryStartSessionOnce(1, Source.KeyA.Editor), "transient acquire failure must automatically recover");
            controller.Update();
            check(controller.TryBorrow(out var fresh), "automatic recovery publishes a new image");
            fresh?.Dispose();
        });
        test("native misses back off without reading context or acquiring every frame", () =>
        {
            var source = new Source { Fail = true }; var now = 0L; var reads = 0;
            using var controller = new PortraitNativeLiveController(source, () => { reads++; return Ready(); }, () => now);
            controller.TryStartSessionOnce(1, Source.KeyA.Editor); controller.Update();
            var firstReads = reads;
            foreach (var time in new long[] { 1, 100, 249 })
            {
                now = time;
                check(!controller.TryStartSessionOnce(1, Source.KeyA.Editor), "retry is not due before 250ms");
            }
            check(reads == firstReads && source.Calls == 1, "ordinary frames do not retry native acquisition or context");
            foreach (var time in new long[] { 250, 750, 1750, 3750, 5750 })
            {
                now = time;
                check(controller.TryStartSessionOnce(1, Source.KeyA.Editor), "due retries use 250/500/1000/2000ms capped delays");
                controller.Update();
            }
            check(source.Calls == 6, "only scheduled attempts call the native source");
        });
        test("capture work temporarily suspends the same lease and resumes after it drains", () =>
        {
            var source = new Source(); var now = 0L; var context = Ready();
            using var controller = new PortraitNativeLiveController(source, () => context, () => now);
            controller.TryStartSessionOnce(1, Source.KeyA.Editor); controller.Update();
            controller.TryBorrow(out var old);
            context = context with { CapturesDrained = false };
            controller.Update();
            check(!old!.TryGetImage(out _) && source.Calls == 1, "capture busy retires the image without native work");
            old.Dispose(); context = Ready(); now = 250;
            check(controller.TryStartSessionOnce(1, Source.KeyA.Editor), "same owned lease resumes after captures drain");
            controller.Update();
            check(controller.TryBorrow(out var fresh), "resume publishes only freshly acquired image");
            fresh?.Dispose();
        });
        test("temporary editor readiness loss retires the frame and recovers only the same identity", () =>
        {
            var source = new Source(); var now = 0L; var context = Ready();
            using var controller = new PortraitNativeLiveController(source, () => context, () => now);
            controller.TryStartSessionOnce(1, Source.KeyA.Editor); controller.Update();
            context = context with { Editor = default, SessionOwnsEditor = false };
            controller.Update();
            check(!controller.TryBorrow(out _) && source.Calls == 1, "unresolved editor never leaves an old frame visible");
            context = Ready(); now = 250;
            check(controller.TryStartSessionOnce(1, Source.KeyA.Editor), "same identity can recover after temporary loading");
            controller.Update();
            check(controller.TryBorrow(out var fresh), "loading recovery publishes a current frame"); fresh?.Dispose();
        });
        test("automatic retries bind the initial owner and never follow another editor allocation", () =>
        {
            foreach (var changed in new[] { Ready() with { Owner = new(9, 10) },
                Ready() with { Editor = Source.KeyA.Editor with { Generation = 2 } },
                Ready() with { SessionOwnsEditor = false }, Ready() with { CanBlockSave = false } })
            {
                var source = new Source { Fail = true }; var now = 0L; var context = Ready();
                using var controller = new PortraitNativeLiveController(source, () => context, () => now);
                controller.TryStartSessionOnce(1, Source.KeyA.Editor); controller.Update();
                context = changed; now = 250;
                check(!controller.TryStartSessionOnce(1, Source.KeyA.Editor), "ownership drift blocks the retry before native acquisition");
                context = Ready(); now = 10000;
                check(!controller.TryStartSessionOnce(1, Source.KeyA.Editor) && source.Calls == 1,
                    "original identity returning cannot resurrect the cancelled lease");
            }
        });
        test("close and lifecycle invalidation permanently cancel the same session retry", () =>
        {
            foreach (var reason in new[] { PortraitNativeLiveStopReason.UserClosed, PortraitNativeLiveStopReason.Disabled,
                PortraitNativeLiveStopReason.TerritoryChanged, PortraitNativeLiveStopReason.LoggedOut })
            {
                var source = new Source { Fail = true }; var now = 0L;
                using var controller = new PortraitNativeLiveController(source, Ready, () => now);
                controller.TryStartSessionOnce(1, Source.KeyA.Editor); controller.Update(); controller.Stop(reason);
                now = 10000; source.Fail = false;
                check(!controller.TryStartSessionOnce(1, Source.KeyA.Editor) && source.Calls == 1,
                    "explicit stop must never silently restart the old preview lease");
                check(controller.TryStartSessionOnce(2, Source.KeyA.Editor), "only a new preview lease may start again");
            }
            var invalidated = new Source { Fail = true }; var clock = 0L;
            using var stopped = new PortraitNativeLiveController(invalidated, Ready, () => clock);
            stopped.TryStartSessionOnce(1, Source.KeyA.Editor); stopped.Update(); invalidated.Invalidate();
            clock = 10000;
            check(!stopped.TryStartSessionOnce(1, Source.KeyA.Editor) && invalidated.Calls == 1,
                "PreFinalize-style source invalidation is final for this lease");
        });
        test("automatic retry waits for old Draw borrowers to physically retire", () =>
        {
            var source = new Source(); var now = 0L;
            using var controller = new PortraitNativeLiveController(source, Ready, () => now);
            controller.TryStartSessionOnce(1, Source.KeyA.Editor); controller.Update(); controller.TryBorrow(out var old);
            source.Fail = true; controller.Update(); source.Fail = false; now = 250;
            check(!controller.TryStartSessionOnce(1, Source.KeyA.Editor) && source.Calls == 2,
                "retired but borrowed wrap blocks the next acquisition");
            old!.Dispose(); now = 750;
            check(controller.TryStartSessionOnce(1, Source.KeyA.Editor), "retired Draw lease permits the scheduled retry");
            controller.Update(); check(source.Calls == 3, "retry acquires only after physical retirement");
            check(controller.TryBorrow(out var fresh), "retired retry publishes its own frame"); fresh?.Dispose();
        });
        test("controller construction and disabled updates do not read context or native source", () =>
        {
            var source = new Source(); var reads = 0;
            using var controller = new PortraitNativeLiveController(source, () => { reads++; return Ready(); });
            controller.Update(); controller.Update();
            check(!controller.IsEnabled && !controller.TryBorrow(out _) && reads == 0 && source.Calls == 0,
                "ordinary construction, Draw and disabled updates must cause zero reads");
        });
        test("explicit enable validates context then publishes a detached borrow", () =>
        {
            var source = new Source(); using var controller = new PortraitNativeLiveController(source, Ready);
            check(controller.TryEnable(Source.KeyA.Editor) && controller.IsEnabled && source.Calls == 0,
                "explicit bound enable validates but does not acquire");
            controller.Update();
            check(controller.TryBorrow(out var borrow), "one successful source update enables Draw");
            using (borrow) check(borrow!.TryGetImage(out var image) && image.Handle.Handle == 123
                && image.Key.Editor == Source.KeyA.Editor && image.Key.Owner == Source.KeyA.Owner,
                "Draw receives only the verified detached native-image descriptor");
        });
        test("capture modes outstanding work and lighting fits reject enable without source work", () =>
        {
            foreach (var rejected in new[]
            {
                Ready() with { CaptureMode = "live" }, Ready() with { CaptureMode = "history" },
                Ready() with { CaptureMode = "unknown" }, Ready() with { CapturesDrained = false },
                Ready() with { LightingFitBusy = true }, Ready() with { SessionOwnsEditor = false },
                Ready() with { CanBlockSave = false }, Ready() with { Owner = default },
            })
            {
                var source = new Source(); using var controller = new PortraitNativeLiveController(source, () => rejected);
                check(!controller.TryEnable(Source.KeyA.Editor) && !controller.IsEnabled, "unsafe context rejects the request");
                controller.Update(); check(source.Calls == 0 && !controller.TryBorrow(out _), "rejection leaves no acquisition intent");
            }
        });
        test("enable compares every editor identity field and rejects zero allocation generation", () =>
        {
            foreach (var requested in ChangedIdentities())
            {
                var source = new Source(); using var controller = new PortraitNativeLiveController(source, Ready);
                check(!controller.TryEnable(requested), "a different editor/state/view/character/generation cannot bind");
                controller.Update(); check(source.Calls == 0 && !controller.IsEnabled, "wrong identity does not start a source");
            }
        });
        test("failed acquisition clears intent until another explicit enable", () =>
        {
            var source = new Source(); var reads = 0;
            using var controller = new PortraitNativeLiveController(source, () => { reads++; return Ready(); });
            controller.TryEnable(Source.KeyA.Editor); controller.Update(); controller.TryBorrow(out var old);
            source.Fail = true; controller.Update();
            check(!controller.IsEnabled && !old!.TryGetImage(out _) && !controller.TryBorrow(out _),
                "an acquire failure immediately stops old and new Draw access");
            var stoppedReads = reads; var calls = source.Calls; source.Fail = false;
            controller.Update(); controller.Update();
            check(reads == stoppedReads && source.Calls == calls, "readiness returning cannot restart the failed attempt");
            old!.Dispose(); check(source.Textures[0].Disposes == 1, "last old borrow releases once after stop");
            check(controller.TryEnable(Source.KeyA.Editor), "a new explicit request may retry");
            controller.Update(); check(controller.TryBorrow(out var current), "a new request can publish again"); current?.Dispose();
        });
        test("source exception clears intent without reading fresh context again", () =>
        {
            var source = new Source(); var reads = 0;
            using var controller = new PortraitNativeLiveController(source, () => { reads++; return Ready(); });
            controller.TryEnable(Source.KeyA.Editor); source.OnAcquire = () => throw new InvalidOperationException("test acquire failure");
            controller.Update(); var stoppedReads = reads; controller.Update();
            check(!controller.IsEnabled && !controller.TryBorrow(out _) && source.Calls == 1 && reads == stoppedReads,
                "the raw view's caught acquisition error must not leave the controller retrying");
        });
        test("source invalidation permanently stops even a same-address new generation", () =>
        {
            var source = new Source(); var context = Ready();
            using var controller = new PortraitNativeLiveController(source, () => context);
            controller.TryEnable(Source.KeyA.Editor); controller.Update(); controller.TryBorrow(out var old);
            source.Invalidate(); context = context with { Editor = context.Editor with { Generation = 2 } };
            source.Key = source.Key with { Editor = context.Editor, Epoch = 2 };
            controller.Update();
            check(!controller.IsEnabled && source.Calls == 1 && !old!.TryGetImage(out _) && !controller.TryBorrow(out _),
                "PreFinalize-style invalidation cannot automatically follow a replacement allocation");
            old!.Dispose(); check(source.Textures[0].Disposes == 1, "invalidation retires the old retained frame");
            check(controller.TryEnable(context.Editor), "replacement requires its own explicit request");
        });
        test("context drift stops before acquiring and does not resume when original identity returns", () =>
        {
            foreach (var changed in ChangedIdentities())
            {
                var source = new Source(); var context = Ready();
                using var controller = new PortraitNativeLiveController(source, () => context);
                controller.TryEnable(Source.KeyA.Editor); controller.Update();
                context = context with { Editor = changed }; controller.Update(); context = Ready(); controller.Update();
                check(!controller.IsEnabled && source.Calls == 1 && !controller.TryBorrow(out _),
                    "each identity field is checked again before native source work");
            }
        });
        test("new conflicting capture state stops a previously enabled controller", () =>
        {
            foreach (var changed in new[] { Ready() with { CapturesDrained = false }, Ready() with { CaptureMode = "live" },
                Ready() with { LightingFitBusy = true }, Ready() with { Owner = new(7, 8) } })
            {
                var source = new Source(); var context = Ready();
                using var controller = new PortraitNativeLiveController(source, () => context);
                controller.TryEnable(Source.KeyA.Editor); controller.Update(); context = changed; controller.Update();
                context = Ready(); controller.Update();
                check(!controller.IsEnabled && source.Calls == 1 && !controller.TryBorrow(out _),
                    "mutual exclusion and account/character ownership remain enforced after enable");
            }
        });
        test("a mismatched acquired descriptor is never offered to Draw", () =>
        {
            foreach (var key in ChangedIdentities().Select(identity => Source.KeyA with { Editor = identity })
                .Append(Source.KeyA with { Owner = new(9, 10) }))
            {
                var source = new Source { Key = key }; using var controller = new PortraitNativeLiveController(source, Ready);
                controller.TryEnable(Source.KeyA.Editor); controller.Update(); controller.Update();
                check(!controller.IsEnabled && source.Calls == 1 && !controller.TryBorrow(out _)
                    && source.Textures.Single().Disposes == 1, "the controller cannot publish a source frame for another identity");
            }
        });
        test("owner drift during acquisition retires the frame without publishing", () =>
        {
            var source = new Source(); var context = Ready();
            using var controller = new PortraitNativeLiveController(source, () => context);
            controller.TryEnable(Source.KeyA.Editor);
            source.OnAcquire = () => context = context with { Editor = context.Editor with { Generation = 2 } };
            controller.Update();
            check(!controller.IsEnabled && !controller.TryBorrow(out _) && source.Textures.Single().Disposes == 1,
                "post-acquire detached context must still belong to the bound editor");
        });
        test("invalidation during enable or update cannot resurrect a cancelled request", () =>
        {
            var source = new Source(); var invalidate = true;
            using var controller = new PortraitNativeLiveController(source, () =>
            {
                if (invalidate) source.Invalidate(); return Ready();
            });
            check(!controller.TryEnable(Source.KeyA.Editor) && !controller.IsEnabled, "enable's context callback cannot outlive invalidation");
            invalidate = false; check(controller.TryEnable(Source.KeyA.Editor), "another explicit enable starts a new request");
            invalidate = true; controller.Update();
            check(!controller.IsEnabled && source.Calls == 0, "update must recheck its ticket before acquiring");
        });
        test("stop during acquire prevents publication and releases the candidate", () =>
        {
            var source = new Source(); using var controller = new PortraitNativeLiveController(source, Ready);
            controller.TryEnable(Source.KeyA.Editor);
            source.OnAcquire = () => controller.Stop(PortraitNativeLiveStopReason.UserClosed);
            controller.Update(); controller.Update();
            check(!controller.IsEnabled && !controller.TryBorrow(out _) && source.Calls == 1
                && source.Textures.Single().Disposes == 1, "a reentrant close invalidates both acquisition tickets");
        });
        test("context exceptions and off-thread updates fail closed without native acquisition", () =>
        {
            var source = new Source(); var failContext = false;
            using var controller = new PortraitNativeLiveController(source, () => failContext ? throw new InvalidOperationException("context gone") : Ready());
            controller.TryEnable(Source.KeyA.Editor); failContext = true; controller.Update();
            failContext = false; controller.Update();
            check(!controller.IsEnabled && source.Calls == 0, "context errors stop rather than retaining intent");
            controller.TryEnable(Source.KeyA.Editor); source.IsFrameworkThread = false; controller.Update();
            source.IsFrameworkThread = true; controller.Update();
            check(!controller.IsEnabled && source.Calls == 0, "wrong-thread update cannot acquire or resume later");
        });
        test("lifecycle stop reasons retire borrowed frames and never restart themselves", () =>
        {
            foreach (var reason in new[] { PortraitNativeLiveStopReason.UserClosed, PortraitNativeLiveStopReason.TerritoryChanged,
                PortraitNativeLiveStopReason.LoggedOut, PortraitNativeLiveStopReason.UpdateFailed, PortraitNativeLiveStopReason.OwnerChanged })
            {
                var source = new Source(); using var controller = new PortraitNativeLiveController(source, Ready);
                controller.TryEnable(Source.KeyA.Editor); controller.Update(); controller.TryBorrow(out var old);
                controller.Stop(reason); controller.Update();
                check(!controller.IsEnabled && controller.StopReason == reason && source.Calls == 1 && !old!.TryGetImage(out _),
                    "explicit lifecycle stop clears ownership immediately");
                old!.Dispose(); check(source.Textures.Single().Disposes == 1, "borrowed resources retire after the final consumer");
            }
        });
        test("controller disposal cannot restart or own the caller's source", () =>
        {
            var source = new Source(); var reads = 0;
            var controller = new PortraitNativeLiveController(source, () => { reads++; return Ready(); });
            controller.TryEnable(Source.KeyA.Editor); controller.Update(); controller.TryBorrow(out var old);
            controller.Dispose(); controller.Dispose(); var stoppedReads = reads; source.Invalidate();
            controller.Update(); check(!controller.TryEnable(Source.KeyA.Editor) && !controller.TryBorrow(out _)
                && !old!.TryGetImage(out _) && reads == stoppedReads && source.Calls == 1,
                "dispose is final and requires no additional native or context reads");
            old!.Dispose(); check(source.Textures.Single().Disposes == 1, "outstanding Draw lease is retired once");
        });
#endif
    }

#if HAS_NATIVE_LIVE_CONTROLLER
    private static PortraitNativeLiveContext Ready() => new(Source.KeyA.Editor, Source.KeyA.Owner,
        SessionOwnsEditor: true, CanBlockSave: true, CaptureMode: "off", CapturesDrained: true, LightingFitBusy: false);

    private static IEnumerable<PortraitIdentity> ChangedIdentities()
    {
        var value = Source.KeyA.Editor;
        return [value with { Editor = 100 }, value with { State = 101 }, value with { Model = 102 },
            value with { Character = 103 }, value with { Generation = 2 }, value with { Generation = 0 }, default];
    }
#endif
}
