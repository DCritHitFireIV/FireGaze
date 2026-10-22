using System.Numerics;
#if HAS_OBSERVER_CAMERA
using FireGaze.PortraitPreview;
#endif

var passed = 0;
var failed = 0;
void Check(bool value, string name)
{
    Console.WriteLine((value ? "PASS " : "FAIL ") + name);
    if (value) passed++; else failed++;
}

#if HAS_OBSERVER_CAMERA
bool Near(float actual, float expected, float epsilon = .00001f) => Math.Abs(actual - expected) <= epsilon;
bool NearVector(Vector3 actual, Vector3 expected, float epsilon = .00001f)
    => Vector3.Distance(actual, expected) <= epsilon;
bool Finite(Vector3 vector) => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
PortraitObserverInput Input(Vector3 move, Vector2 look = default, bool focused = true, bool captured = true,
    bool blocked = false) => new(move, look, focused, captured, blocked);
var options = new PortraitObserverCameraSettings(2f, .1, .2, .1);
PortraitObserverCamera Camera(PortraitObserverPose pose = default)
    => new(pose, options);
void Advance(PortraitObserverCamera camera, PortraitObserverInput input, int frames, double secondsPerFrame)
{
    for (var index = 0; index < frames; index++) camera.Step(input, secondsPerFrame);
}
bool Throws(Action operation)
{
    try { operation(); return false; }
    catch (ArgumentOutOfRangeException) { return true; }
}

// Wrong axes/signs would move the observer away from the user's visible W/A/S/D directions.
var identity = Camera();
Check(identity.Snapshot.Forward == -Vector3.UnitZ, "zero observer angles look along negative Z");
Check(identity.Snapshot.Right == Vector3.UnitX && identity.Snapshot.Up == Vector3.UnitY,
    "zero observer angles establish right X and up Y");
var directions = new (Vector3 Input, Vector3 Expected, string Name)[]
{
    (Vector3.UnitZ, -Vector3.UnitZ, "W forward"),
    (-Vector3.UnitZ, Vector3.UnitZ, "S backward"),
    (-Vector3.UnitX, -Vector3.UnitX, "A left"),
    (Vector3.UnitX, Vector3.UnitX, "D right"),
    (Vector3.UnitY, Vector3.UnitY, "E up"),
    (-Vector3.UnitY, -Vector3.UnitY, "Q down"),
};
foreach (var direction in directions)
{
    var camera = Camera();
    Check(camera.Step(Input(direction.Input), .05), direction.Name + " accepts focused captured input");
    Check(NearVector(camera.Snapshot.Pose.Position, direction.Expected * .021306132f),
        direction.Name + " follows the local basis with a gradual start");
}

var turned = Camera(new(Vector3.Zero, MathF.PI / 2, 0));
turned.Step(Input(Vector3.UnitZ), .05);
Check(NearVector(turned.Snapshot.Pose.Position, new(.021306132f, 0, 0)),
    "turning right by ninety degrees makes W travel along positive X");
var tilted = Camera(new(Vector3.Zero, 0, MathF.PI / 6));
tilted.Step(Input(Vector3.UnitZ), .05);
Check(NearVector(tilted.Snapshot.Pose.Position, new(0, .010653066f, -.018451649f)),
    "pitched observer W moves forward and upward in camera space");
var localUp = Camera(new(Vector3.Zero, 0, MathF.PI / 6));
localUp.Step(Input(Vector3.UnitY), .05);
Check(NearVector(localUp.Snapshot.Pose.Position, new(0, .018451649f, .010653066f)),
    "vertical observer motion follows camera up rather than world up");

// Leaving a diagonal unnormalised makes exploration faster just by adding a second key.
var straight = Camera(); straight.Step(Input(Vector3.UnitZ), .1);
var diagonal = Camera(); diagonal.Step(Input(new(1, 0, 1)), .1);
Check(Near(straight.Snapshot.Velocity.Length(), diagonal.Snapshot.Velocity.Length())
    && Near(straight.Snapshot.Pose.Position.Length(), diagonal.Snapshot.Pose.Position.Length()),
    "two-axis movement does not gain speed or displacement");
var threeAxis = Camera(); threeAxis.Step(Input(Vector3.One), .1);
Check(Near(threeAxis.Snapshot.Pose.Position.Length(), straight.Snapshot.Pose.Position.Length()),
    "three-axis movement does not gain speed");
var bounded = Camera(); bounded.Step(Input(new(float.MaxValue, 0, float.MaxValue)), .1);
Check(NearVector(bounded.Snapshot.Pose.Position, diagonal.Snapshot.Pose.Position),
    "oversized finite input axes clamp safely before normalisation");
var partial = Camera(); partial.Step(Input(new(0, 0, .5f)), .05);
Check(Near(partial.Snapshot.Pose.Position.Z, -.010653066f),
    "sub-unit input preserves precision instead of forcing full speed");

// Capture must be explicit: a hover, text field, modal or tool interaction cannot move the observer.
foreach (var input in new[]
{
    Input(Vector3.One, new(.3f, .2f), focused: false),
    Input(Vector3.One, new(.3f, .2f), captured: false),
    Input(Vector3.One, new(.3f, .2f), blocked: true),
    default(PortraitObserverInput),
})
{
    var camera = Camera(); var before = camera.Snapshot;
    Check(!camera.Step(input, .05) && camera.Snapshot == before,
        "unowned or blocked input changes neither pose nor velocity");
}
var released = Camera(); released.Step(Input(Vector3.UnitZ), .05);
var beforeRelease = released.Snapshot.Pose;
Check(!released.Step(Input(Vector3.UnitZ, focused: false), .05)
    && released.Snapshot.Pose == beforeRelease && released.Snapshot.Velocity == Vector3.Zero,
    "losing focus immediately removes momentum without drifting");
released.Step(Input(Vector3.Zero), .05);
Check(released.Snapshot.Pose == beforeRelease,
    "regaining focus does not replay old key intent or residual momentum");
foreach (var blockedInput in new[] { Input(Vector3.One, captured: false), Input(Vector3.One, blocked: true) })
{
    var camera = Camera(); camera.Step(Input(Vector3.UnitZ), .05); var pose = camera.Snapshot.Pose;
    camera.Step(blockedInput, .05);
    Check(camera.Snapshot.Pose == pose && camera.Snapshot.Velocity == Vector3.Zero,
        "capture loss or a competing control stops existing momentum immediately");
}
var explicitRelease = Camera(); explicitRelease.Step(Input(Vector3.UnitZ), .05);
var explicitPose = explicitRelease.Snapshot.Pose;
explicitRelease.ReleaseInput(); explicitRelease.Step(Input(Vector3.Zero), .05);
Check(explicitRelease.Snapshot.Pose == explicitPose && explicitRelease.Snapshot.Velocity == Vector3.Zero,
    "explicit window close release stops momentum before the next frame");

// Analytic values prevent a per-frame lerp or Euler displacement from masquerading as damping.
var response = Camera(); response.Step(Input(Vector3.UnitZ), .05);
Check(Near(response.Snapshot.Velocity.Z, -.786938681f)
    && Near(response.Snapshot.Pose.Position.Z, -.021306132f),
    "startup velocity and travel match the analytic exponential response");
response.Step(Input(Vector3.Zero), .05);
Check(Near(response.Snapshot.Velocity.Z, -.612868461f)
    && Near(response.Snapshot.Pose.Position.Z, -.056120176f),
    "release uses its own braking response and integrates coasting distance");
var frameRates = new[] { 30, 60, 144 };
PortraitObserverCameraSnapshot? previous = null;
foreach (var rate in frameRates)
{
    var camera = Camera();
    Advance(camera, Input(Vector3.UnitZ), rate, 1d / rate);
    Advance(camera, Input(Vector3.Zero), rate, 1d / rate);
    var snapshot = camera.Snapshot;
    if (previous is { } last)
        Check(NearVector(snapshot.Pose.Position, last.Pose.Position, .00002f)
            && NearVector(snapshot.Velocity, last.Velocity, .00002f),
            "startup and brake travel agree between different frame rates");
    previous = snapshot;
}
var whole = Camera(); whole.Step(Input(Vector3.UnitZ), .1); whole.Step(Input(Vector3.Zero), .1);
var split = Camera(); Advance(split, Input(Vector3.UnitZ), 10, .01); Advance(split, Input(Vector3.Zero), 10, .01);
Check(NearVector(whole.Snapshot.Pose.Position, split.Snapshot.Pose.Position)
    && NearVector(whole.Snapshot.Velocity, split.Snapshot.Velocity),
    "splitting a fixed ramp and brake interval preserves position and velocity");
var tinyInterval = Camera(); tinyInterval.Step(Input(Vector3.UnitZ), 1e-12);
Check(tinyInterval.Snapshot.Pose.Position.Z < 0
    && Math.Abs(tinyInterval.Snapshot.Pose.Position.Z + 1e-23) < 1e-26
    && Math.Abs(tinyInterval.Snapshot.Velocity.Z + 2e-11) < 1e-15,
    "a tiny positive startup interval cannot reverse travel through exponential cancellation");

// Invalid input must never poison a future native/render consumer with nonfinite camera state.
foreach (var seconds in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0d, -1d })
{
    var camera = Camera(); camera.Step(Input(Vector3.UnitZ), .05); var pose = camera.Snapshot.Pose;
    Check(!camera.Step(Input(Vector3.One, new(.5f, .5f)), seconds)
        && camera.Snapshot.Pose == pose && camera.Snapshot.Velocity == Vector3.Zero,
        "invalid or nonpositive elapsed time stops without editing pose");
}
foreach (var input in new[]
{
    Input(new(float.NaN, 0, 0)), Input(new(0, float.PositiveInfinity, 0)),
    Input(new(0, 0, float.NegativeInfinity)), Input(Vector3.Zero, new(float.NaN, 0)),
    Input(Vector3.Zero, new(0, float.PositiveInfinity)),
})
{
    var camera = Camera(); camera.Step(Input(Vector3.UnitZ), .05); var pose = camera.Snapshot.Pose;
    Check(!camera.Step(input, .05) && camera.Snapshot.Pose == pose && camera.Snapshot.Velocity == Vector3.Zero,
        "nonfinite movement or look input cannot corrupt pose or retain momentum");
}
var stalled = Camera(); stalled.Step(Input(Vector3.UnitZ), 600d);
var capped = Camera(); capped.Step(Input(Vector3.UnitZ), .1);
Check(stalled.Snapshot == capped.Snapshot,
    "a long frame processes only one bounded interval and never catches up lost minutes");
var maximumTime = Camera(); maximumTime.Step(Input(Vector3.UnitZ), double.MaxValue);
Check(maximumTime.Snapshot == capped.Snapshot,
    "extreme finite elapsed time does not overflow the damping calculation");

// Turning is bounded and uses the latest basis, while observation owns no portrait parameters.
var looking = Camera(); looking.Step(Input(Vector3.Zero, new(7 * MathF.PI / 2, 100)), .05);
Check(Near(looking.Snapshot.Pose.YawRadians, -MathF.PI / 2)
    && looking.Snapshot.Pose.PitchRadians < MathF.PI / 2
    && looking.Snapshot.Pose.PitchRadians > 1.5f,
    "look yaw wraps and pitch stops short of a pole");
looking.Step(Input(Vector3.Zero, new(0, -200)), .05);
Check(looking.Snapshot.Pose.PitchRadians > -MathF.PI / 2 && looking.Snapshot.Pose.PitchRadians < -1.5f,
    "downward look also stops short of the opposite pole");
var simultaneous = Camera(); simultaneous.Step(Input(Vector3.UnitZ, new(MathF.PI / 2, 0)), .05);
Check(NearVector(simultaneous.Snapshot.Pose.Position, new(.021306132f, 0, 0)),
    "movement in a look gesture uses the newly selected observer direction");
var enormousLook = Camera(); enormousLook.Step(Input(Vector3.One, new(float.MaxValue, float.MaxValue)), .05);
Check(Finite(enormousLook.Snapshot.Pose.Position) && Finite(enormousLook.Snapshot.Forward)
    && Finite(enormousLook.Snapshot.Right) && Finite(enormousLook.Snapshot.Up),
    "extreme finite look remains a valid finite orthogonal camera basis");
foreach (var pose in new[]
{
    new PortraitObserverPose(new(float.NaN, 0, 0), 0, 0),
    new PortraitObserverPose(Vector3.Zero, float.NaN, 0),
    new PortraitObserverPose(Vector3.Zero, 0, float.PositiveInfinity),
})
    Check(Throws(() => _ = Camera(pose)), "construction rejects nonfinite camera poses");
foreach (var invalid in new[]
{
    options with { MoveSpeed = 0 }, options with { MoveSpeed = float.PositiveInfinity },
    options with { MoveSpeed = float.MaxValue }, options with { StartupSeconds = 0 },
    options with { StartupSeconds = float.NaN }, options with { StopSeconds = -1 },
    options with { MaximumStepSeconds = 0 }, options with { MaximumStepSeconds = 1 },
})
    Check(Throws(() => _ = new PortraitObserverCamera(default, invalid)),
        "invalid response settings cannot introduce unbounded motion");
var normalizedStart = Camera(new(new(3, 2, 1), 5 * MathF.PI, 200));
Check(Near(MathF.Abs(normalizedStart.Snapshot.Pose.YawRadians), MathF.PI)
    && normalizedStart.Snapshot.Pose.PitchRadians < MathF.PI / 2,
    "initial orientation is normalised before any consumer receives it");
var initial = new PortraitObserverPose(new(3, 2, 1), .2f, .1f);
var first = Camera(initial); var second = Camera(initial); var detached = first.Snapshot;
first.Step(Input(Vector3.One, new(.3f, .2f)), .05);
Check(second.Snapshot == detached && initial == detached.Pose,
    "moving one observer cannot mutate another camera or its detached source pose");
var editedCopy = detached with { Pose = detached.Pose with { Position = new(999, 999, 999) } };
Check(first.Snapshot.Pose.Position != editedCopy.Pose.Position && second.Snapshot == detached,
    "editing a returned snapshot cannot write through into either camera");
var repeat = Camera();
for (var index = 0; index < 5000; index++)
    repeat.Step(Input(new(index % 3 - 1, index % 2, 1), new(.01f, -.005f)), 1d / 144);
var final = repeat.Snapshot;
Check(Finite(final.Pose.Position) && Finite(final.Velocity)
    && Near(final.Forward.Length(), 1) && Near(final.Right.Length(), 1) && Near(final.Up.Length(), 1)
    && Math.Abs(Vector3.Dot(final.Forward, final.Right)) < .00001f
    && Math.Abs(Vector3.Dot(final.Forward, final.Up)) < .00001f,
    "repeated turn and movement preserve finite state and an orthonormal basis");
#else
Check(false, "managed independent observer camera is not implemented");
#endif

Console.WriteLine($"PortraitObserverCameraTests: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
