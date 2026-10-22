using System.Numerics;
using FireGaze.PortraitPreview;

internal static class ObserverNavigationCases
{
    internal static void Run(Action<bool, string> check)
    {
#if OWNED_NAVIGATION
        PortraitObserverInput Input(Vector3 move, bool focus = true, bool capture = true, bool blocked = false)
            => new(move, Vector2.Zero, focus, capture, blocked);
        PortraitOwnedSceneObserverCameraSnapshot Front(Vector3 target = default)
            => new(target + Vector3.UnitZ, target, 0, 0, 1);
        bool Near(Vector3 actual, Vector3 expected) => Vector3.Distance(actual, expected) < .00001f;
        var axes = new (PortraitOwnedSceneObserverCameraSnapshot Camera, Vector3 Move, Vector3 Expected)[]
        {
            (Front(), Vector3.UnitZ, new(0, 0, -.007479697f)),
            (Front(), Vector3.UnitX, new(.007479697f, 0, 0)),
            (new(new(1, 0, 0), default, MathF.PI / 2, 0, 1), Vector3.UnitZ, new(-.007479697f, 0, 0)),
            (new(new(1, 0, 0), default, MathF.PI / 2, 0, 1), Vector3.UnitX, new(0, 0, -.007479697f)),
            (new(new(0, 0, -1), default, MathF.PI, 0, 1), Vector3.UnitZ, new(0, 0, .007479697f)),
            (new(new(0, 0, -1), default, MathF.PI, 0, 1), Vector3.UnitX, new(-.007479697f, 0, 0)),
        };
        foreach (var (source, move, expected) in axes)
        {
            var nav = new PortraitOwnedViewportNavigation();
            check(!nav.Step(1, source, Input(Vector3.Zero), .05, out var neutral) && neutral == source,
                "neutral capture arms navigation without invalidating the published image");
            check(nav.Step(1, source, Input(move), .05, out var moved) && Near(moved.Target, expected),
                "native front/side/back navigation follows the visible camera basis");
            check(moved.IsNativeSupported && Near(moved.Position - source.Position, expected)
                && moved.Yaw == source.Yaw && moved.Pitch == source.Pitch && moved.Distance == source.Distance,
                $"navigation translates camera and target together and preserves orbit/projection ({moved.Position-source.Position} / {expected}, yaw {moved.Yaw}/{source.Yaw})");
        }

        var held = new PortraitOwnedViewportNavigation(); var front = Front();
        check(!held.Step(1, front, Input(Vector3.UnitZ), .05, out var unchanged) && unchanged == front,
            "keys held before viewport capture cannot start navigation");
        held.Step(1, front, Input(Vector3.Zero), .05, out _);
        held.Step(1, front, Input(Vector3.UnitZ), .05, out var moving);
        check(held.Step(1, moving, Input(Vector3.Zero), .05, out var braking) && braking.Target.Z < moving.Target.Z,
            "releasing movement while capture remains owned follows the existing braking response");
        foreach (var denied in new[] { Input(Vector3.UnitZ, focus: false), Input(Vector3.UnitZ, capture: false),
            Input(Vector3.UnitZ, blocked: true) })
        {
            var nav = new PortraitOwnedViewportNavigation();
            nav.Step(1, front, Input(Vector3.Zero), .05, out _);
            nav.Step(1, front, Input(Vector3.UnitZ), .05, out var started);
            check(!nav.Step(1, started, denied, .05, out var stopped) && stopped == started
                && !nav.Step(1, started, Input(Vector3.UnitZ), .05, out _),
                "lost focus/capture or text/modal ownership stops immediately and requires neutral rearming");
        }

        var restarted = new PortraitOwnedViewportNavigation();
        restarted.Step(1, front, Input(Vector3.Zero), .05, out _);
        restarted.Step(1, front, Input(Vector3.UnitZ), .05, out var old);
        check(!restarted.Step(2, old, Input(Vector3.UnitZ), .05, out var newAttempt) && newAttempt == old,
            "a new attempt cannot inherit held input or old momentum");
        restarted.Step(2, old, Input(Vector3.Zero), .05, out _);
        restarted.Step(2, old, Input(Vector3.UnitZ), .05, out var beforePreset);
        check(!restarted.Step(2, front, Input(Vector3.UnitZ), .05, out var preset) && preset == front,
            "source/preset/reset changes invalidate navigation instead of overwriting the new camera");
        restarted.ReleaseInput();
        check(!restarted.Step(2, beforePreset, Input(Vector3.UnitZ), .05, out _),
            "explicit close/retirement releases all input state");

        var limits = new PortraitOwnedViewportNavigation();
        var atLimit = Front(new(1.599f, .499f, .499f));
        limits.Step(1, atLimit, Input(Vector3.Zero), .05, out _);
        check(limits.Step(1, atLimit, Input(new(1, 1, -1)), .05, out var clipped)
            && clipped.Target == new Vector3(1.6f, .5f, .5f) && clipped.IsNativeSupported
            && Near(clipped.Position - clipped.Target, Vector3.UnitZ),
            "clamping the translated target keeps one consistent orbital offset at all three upper bounds");
        check(!limits.Step(1, clipped, Input(Vector3.Zero), .05, out var still) && still == clipped,
            "reaching a bound removes momentum instead of repeatedly revoking the image");

        var turning = new PortraitOwnedViewportNavigation();
        turning.Step(1, front, Input(Vector3.Zero), .05, out _);
        turning.Step(1, front, new(Vector3.Zero, new(MathF.PI / 2, 0), true, true, false), .05, out var side);
        check(!turning.Step(1, side, Input(Vector3.Zero), .05, out var sameSide) && sameSide == side,
            "a look-only orbit never becomes a target translation on the following frame");
        check(turning.Step(1, side, Input(Vector3.UnitZ), .05, out var sideMoved)
            && Near(sideMoved.Target, new(.007479697f, 0, 0)),
            "movement after a look-only orbit uses the new visible basis without a target jump");

        var pitching = new PortraitOwnedViewportNavigation();
        pitching.Step(1, front, Input(Vector3.Zero), .05, out _);
        pitching.Step(1, front, new(Vector3.Zero, new(0, 2), true, true, false), .05, out var upperPitch);
        check(upperPitch.IsNativeSupported && MathF.Abs(upperPitch.Pitch - MathF.PI / 6) < .00001f,
            "look input clamps at the native thirty-degree upper pitch");
        check(pitching.Step(1, upperPitch, Input(Vector3.UnitZ), .05, out var pitchMoved)
            && Near(pitchMoved.Target, new(0, .0037398485f, -.006477607f)),
            "movement after native pitch clipping follows the displayed thirty-degree camera");

        foreach (var seconds in new[] { double.NaN, 0, -1 })
        {
            var nav = new PortraitOwnedViewportNavigation(); nav.Step(1, front, Input(Vector3.Zero), .05, out _);
            check(!nav.Step(1, front, Input(Vector3.UnitZ), seconds, out var invalid) && invalid == front,
                "invalid elapsed time cannot reach the native camera");
        }
        var invalidNav = new PortraitOwnedViewportNavigation();
        check(!invalidNav.Step(1, front with { Distance = 3 }, Input(Vector3.Zero), .05, out _)
            && !invalidNav.Step(0, front, Input(Vector3.Zero), .05, out _),
            "unsupported cameras and absent attempt identity cannot arm navigation");
#else
        check(false, "owned viewport navigation is missing: no native coordinate conversion or lifecycle-bound movement");
#endif
    }
}
