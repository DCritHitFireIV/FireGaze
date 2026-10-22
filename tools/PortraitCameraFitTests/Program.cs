using System.Numerics;
using FireGaze.PortraitPreview;

// Every expectation exercises production solver output, using independent axis fixtures and native
// float32 bit values from the audited EXE. No native calls, files, game process or synthetic pointers.
var passed = 0; var failed = 0;
void Check(bool condition, string name)
{
    if (condition) passed++; else { failed++; Console.WriteLine("FAIL " + name); }
}
void Case(string name, Action body)
{
    try { body(); } catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
}
var identity = new PortraitCameraFitIdentity(Guid.Parse("1f4e7785-daf9-4445-b63c-dfd7cb27d837"), 12, 3);
var source = new PortraitComposition
{
    CameraPosition = new(0, 0, 1.5f, .3333f), CameraTarget = new(0, 0, 0, 1),
    CameraDistance = 1.5f, CameraZoom = 100, CameraZoomNormalized = .78f,
    BannerTimeline = 109, AnimationProgress = .45f, Expression = 8,
    HeadDirection = new(.2f, -.1f), EyeDirection = new(-.3f, .4f),
    DirectionalRed = 171, DirectionalGreen = 82, DirectionalBlue = 29, DirectionalBrightness = 71,
    DirectionalHorizontalAngle = -48, DirectionalVerticalAngle = 22,
    AmbientRed = 38, AmbientGreen = 190, AmbientBlue = 21, AmbientBrightness = 25,
    Background = 38, Frame = 61, Decoration = 42, PoseClassJob = 33,
    HeadFacingCamera = true, EyesFacingCamera = false, Paused = true, CharacterVisible = false,
};
PortraitCameraFitRequest Request() => new(source, PortraitCameraFit.VerifiedGameBuild, identity,
    new(new(0, 0, 1.5f), 0, 0), Vector3.Zero, .78f, 0, 1);
PortraitCameraFitResult Solve(PortraitCameraFitRequest request, int budget = 256)
    => PortraitCameraFit.Solve(request, () => identity, default, new(budget));
PortraitComposition Candidate(PortraitCameraFitResult result, string name)
{
    Check(result.Status == PortraitCameraFitStatus.Candidate, name + " candidate status");
    if (!result.TryGetCandidate(identity, out var value)) throw new Exception(name + " missing candidate");
    return value;
}
void CheckDomains(PortraitComposition value, string name)
{
    var p = value.CameraPosition; var t = value.CameraTarget;
    var dx = (double)p.X - t.X; var dy = (double)p.Y - t.Y; var dz = (double)p.Z - t.Z;
    var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
    var pitch = -Math.Asin(dy / distance);
    Check(value.IsValid && distance >= .5 && distance <= 2, name + " quantized distance in domain");
    Check(pitch >= -89 * 0.01745329238474369f && pitch <= 30 * 0.01745329238474369f,
        name + " quantized pitch in domain");
    Check(t.X is >= -1.6f and <= 1.6f && t.Y is >= -1 and <= .5f && t.Z is >= -.5f and <= .5f,
        name + " pivot in domain");
    Check(value.CameraZoom <= 200 && value.ImageRotation is >= -90 and <= 90, name + " discrete domains");
    Check(Math.Abs(value.CameraDistance - distance) < 2e-7 && Math.Abs(value.CameraPitch - pitch) < 2e-7,
        name + " fields derived from quantized endpoints");
    Check(Math.Abs(Math.Atan2(dx, dz) - value.CameraYaw) < 2e-7, name + " yaw derived from endpoints");
    Check(new[] { p.X, p.Y, p.Z, p.W, t.X, t.Y, t.Z, t.W }.All(x => (float)(Half)x == x),
        name + " all exported components use Half");
}

Case("native float FOV order", () =>
{
    foreach (var (zoom, bits) in new (byte, uint)[]
    {
        (0, 0x3FA3D70A), (1, 0x3FA33333), (37, 0x3F8C28F6), (100, 0x3F47AE14),
        (150, 0x3F07AE14), (199, 0x3E91EB84), (200, 0x3E8F5C28), (255, 0x3E8F5C28),
    })
        Check(BitConverter.SingleToUInt32Bits(PortraitCameraFit.NativeFieldOfView(zoom)) == bits,
            $"native zoom {zoom} exact float order");
});
Case("valid detached candidate", () =>
{
    var result = Solve(Request()); var value = Candidate(result, "identity view");
    CheckDomains(value, "identity view");
    Check(value.CameraPosition.X == 0 && value.CameraPosition.Y == 0 && value.CameraPosition.Z > 0,
        "zero observer looks toward negative Z");
    Check(value.CameraZoom == 100 && value.CameraZoomNormalized == BitConverter.UInt32BitsToSingle(0x3F47AE14),
        "coherent byte zoom and FOV");
    var restoredCamera = value with
    {
        CameraPosition = source.CameraPosition, CameraTarget = source.CameraTarget, CameraDistance = source.CameraDistance,
        CameraYaw = source.CameraYaw, CameraPitch = source.CameraPitch, CameraZoom = source.CameraZoom,
        CameraZoomNormalized = source.CameraZoomNormalized, ImageRotation = source.ImageRotation,
    };
    Check(restoredCamera == source, "all non-camera composition fields preserved");
    Check(source.CameraPosition.W == .3333f && source.CameraZoomNormalized == .78f, "source not changed");
    Check(result.FacePrecheck == PortraitCameraFacePrecheck.Unknown, "absent face stays Unknown");
    Check(result.NativeOcclusion == PortraitCameraNativeAssessment.Unknown
        && result.NativeValidity == PortraitCameraNativeAssessment.Unknown, "native verdicts stay Unknown");
    Check(result.Limitations.Count > 0, "concrete native limitations returned");
});
Case("independent yaw and pitch fixtures", () =>
{
    var rightView = Request() with { Observer = new(new(-1.5f, 0, 0), MathF.PI / 2, 0) };
    var right = Candidate(Solve(rightView), "positive yaw"); CheckDomains(right, "positive yaw");
    Check(right.CameraPosition.X < right.CameraTarget.X && Math.Abs(right.CameraYaw + Math.PI / 2) < .002,
        "positive observer yaw maps to negative native yaw");
    var belowView = Request() with { Observer = new(new(0, -.75f, 1.2990381f), 0, MathF.PI / 6) };
    var below = Candidate(Solve(belowView), "positive pitch"); CheckDomains(below, "positive pitch");
    Check(below.CameraPosition.Y < below.CameraTarget.Y && below.CameraPitch > .52,
        "positive observer pitch remains positive native pitch");
});
Case("native pitch clamp and extreme intents", () =>
{
    foreach (var pitch in new[] { -MathF.PI / 2, MathF.PI / 2, 30f, -30f })
    {
        var f = new Vector3(MathF.Sin(.7f) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(.7f) * MathF.Cos(pitch));
        var req = Request() with { Observer = new(-f * 3, .7f, pitch) };
        var r = Solve(req, 1024); var c = Candidate(r, $"pitch {pitch}"); CheckDomains(c, $"pitch {pitch}");
    }
    var far = Request() with { Observer = new(new(100000, -100000, 100000), -.8f, .4f), SubjectAnchor = new(200000, 0, -200000) };
    Check(Solve(far).Evaluations <= 256, "huge finite intent remains bounded");
});
Case("Half-induced minimum-distance rejection", () =>
{
    var req = Request() with
    {
        Source = source with { CameraDistance = .5f }, Observer = new(new(0, 0, .8f), 0, 0),
        SubjectAnchor = new(0, 0, .3f), VerticalFieldOfViewRadians = BitConverter.UInt32BitsToSingle(0x3F47AE14),
    };
    var first = Solve(req, 1);
    Check(first.Status == PortraitCameraFitStatus.NoCandidate && !first.TryGetCandidate(identity, out _),
        "0.7998046875 minus 0.300048828125 cannot pass minimum distance");
    Check(first.Reasons.HasFlag(PortraitCameraFitReason.QuantizationRejected), "rejection identifies Half boundary");
    var recovered = Candidate(Solve(req, 256), "interior candidate"); CheckDomains(recovered, "interior candidate");
});
Case("aspects roll and FOV endpoints", () =>
{
    foreach (var (aspect, roll, fov) in new[] { (.1f, -120f, .1f), (10f, 120f, 2.5f), (1.777778f, 37.6f, .53f) })
    {
        var c = Candidate(Solve(Request() with { ViewportAspect = aspect, ImageRotationDegrees = roll,
            VerticalFieldOfViewRadians = fov }, 1024), "aspect/roll");
        CheckDomains(c, "aspect/roll");
        Check(c.ImageRotation == (short)Math.Clamp(Math.Round(roll, MidpointRounding.AwayFromZero), -90, 90),
            "roll quantized to native short domain");
    }
    Check(Candidate(Solve(Request() with { VerticalFieldOfViewRadians = .1f }), "narrow FOV").CameraZoom == 200,
        "narrow FOV chooses nearest native zoom endpoint");
    Check(Candidate(Solve(Request() with { VerticalFieldOfViewRadians = 2.5f }), "wide FOV").CameraZoom == 0,
        "wide FOV chooses nearest native zoom endpoint");
});
Case("managed face frame and orientation", () =>
{
    var face = new PortraitCameraFaceGeometry(identity, PortraitCameraFit.CoordinateSpace,
        [new(-.03f, 0, 0), new(.03f, 0, 0), new(0, .05f, 0)], Vector3.UnitZ);
    var inside = Solve(Request() with { Face = face }); Candidate(inside, "known face");
    Check(inside.FacePrecheck == PortraitCameraFacePrecheck.InsideManagedFrame, "known face geometric precheck");
    Check(inside.NativeValidity == PortraitCameraNativeAssessment.Unknown, "known managed geometry is no native proof");
    var outside = Solve(Request() with { Face = face with { Landmarks = [new(100, 0, 0)] } });
    Check(outside.Status == PortraitCameraFitStatus.NoCandidate && outside.FacePrecheck == PortraitCameraFacePrecheck.Rejected,
        "out-of-frame landmarks exclude candidate");
    var facing = Solve(Request() with { Face = face with { FacingNormal = -Vector3.UnitZ } });
    Check(facing.Status == PortraitCameraFitStatus.NoCandidate
        && facing.Reasons.HasFlag(PortraitCameraFitReason.FaceFacingAway), "back-facing geometry excludes candidate");
    foreach (var bad in new[] { face with { Identity = identity with { Revision = 13 } },
        face with { CoordinateSpace = "world" }, face with { Landmarks = [] },
        face with { FacingNormal = Vector3.Zero }, face with { Landmarks = [new(float.NaN, 0, 0)] } })
    {
        var invalidFace = Solve(Request() with { Face = bad });
        Check(invalidFace.Status == PortraitCameraFitStatus.InvalidInput, "bad geometry provenance or values rejected");
        Check(invalidFace.FacePrecheck == PortraitCameraFacePrecheck.Unknown,
            "untrusted geometry cannot acquire a managed face verdict");
    }
    var unexamined = Solve(Request() with { Face = face, GameBuild = "unknown" });
    Check(unexamined.FacePrecheck == PortraitCameraFacePrecheck.Unknown,
        "unsupported build cannot acquire a managed face verdict");
});
Case("bad input has no candidate", () =>
{
    foreach (var req in new[]
    {
        Request() with { Source = null! }, Request() with { Identity = default },
        Request() with { Observer = new(new(float.NaN, 0, 1), 0, 0) },
        Request() with { Observer = new(new(0, 0, 1), float.PositiveInfinity, 0) },
        Request() with { SubjectAnchor = new(float.PositiveInfinity, 0, 0) },
        Request() with { Observer = new(Vector3.Zero, 0, 0) },
        Request() with { Observer = new(new(0, 0, -1), 0, 0) },
        Request() with { VerticalFieldOfViewRadians = 0 }, Request() with { VerticalFieldOfViewRadians = MathF.PI },
        Request() with { ViewportAspect = 0 }, Request() with { ViewportAspect = float.NaN },
        Request() with { ImageRotationDegrees = float.PositiveInfinity },
        Request() with { Source = source with { AnimationProgress = -1 } },
    })
    {
        var r = Solve(req); Check(r.Status == PortraitCameraFitStatus.InvalidInput && r.Evaluations == 0,
            "invalid input fails before evaluation"); Check(!r.TryGetCandidate(identity, out _), "invalid input carries no candidate");
    }
    var unsupported = Solve(Request() with { GameBuild = "2026.01.01.0000.0000" });
    Check(unsupported.Status == PortraitCameraFitStatus.UnsupportedBuild && unsupported.Evaluations == 0,
        "unsupported build cannot reuse audited bounds");
    Check(PortraitCameraFit.Solve(null, () => identity).Status == PortraitCameraFitStatus.InvalidInput, "null request rejected");
});
Case("image roll precedes aspect projection", () =>
{
    // At z=1.5 and FOV .78, the pinhole half-height is .61658. A local X=.9 point
    // fits the aspect-2 horizontal frame, but a 90-degree camera roll places it outside vertically.
    var face = new PortraitCameraFaceGeometry(identity, PortraitCameraFit.CoordinateSpace, [new(.9f, 0, 0)], Vector3.UnitZ);
    var horizontal = Solve(Request() with { Face = face, ViewportAspect = 2 }, 1);
    Check(horizontal.Status == PortraitCameraFitStatus.Candidate, "wide horizontal frame includes off-center face point");
    var rolled = Solve(Request() with { Face = face, ViewportAspect = 2, ImageRotationDegrees = 90 }, 1);
    Check(rolled.Status == PortraitCameraFitStatus.NoCandidate
        && rolled.Reasons.HasFlag(PortraitCameraFitReason.FaceOutsideManagedFrame),
        "roll moves point outside vertical frame without stretching by aspect");
});
Case("face input copied and source retained on failure", () =>
{
    var points = new[] { new Vector3(-.03f, 0, 0), new Vector3(.03f, 0, 0) };
    var face = new PortraitCameraFaceGeometry(identity, PortraitCameraFit.CoordinateSpace, points, Vector3.UnitZ);
    var calls = 0;
    var result = PortraitCameraFit.Solve(Request() with { Face = face }, () =>
    {
        if (++calls == 2) points[0] = new(100, 0, 0); return identity;
    }, default, new(17));
    Check(result.Status == PortraitCameraFitStatus.Candidate, "later caller array mutation cannot alter copied geometry");
    var failed = Solve(Request() with { Source = source with { CameraPosition = new(0, 0, 1.5f, float.MaxValue) } });
    Check(failed.Status == PortraitCameraFitStatus.NoCandidate && failed.Reasons.HasFlag(PortraitCameraFitReason.QuantizationRejected),
        "nonrepresentable homogeneous component cannot leak a candidate");
    Check(source.CameraPosition.W == .3333f && source.CameraZoomNormalized == .78f && source.BannerTimeline == 109,
        "failed search retains original composition");
    Check(Solve(Request() with { Face = face with { Landmarks = new Vector3[257] } }).Evaluations == 0,
        "oversized geometry rejected before bounded candidate search");
});
Case("budget and deterministic output", () =>
{
    foreach (var budget in new[] { 1, 17, 256, 4096 })
    {
        var a = Solve(Request(), budget); var b = Solve(Request(), budget);
        Check(a.Evaluations <= budget && a.Evaluations > 0, "evaluation budget enforced");
        var ca = Candidate(a, "deterministic A"); var cb = Candidate(b, "deterministic B");
        Check(ca == cb && a.Evaluations == b.Evaluations && a.Reasons == b.Reasons, "stable candidate and diagnostics");
    }
    Check(Solve(Request(), 1).Reasons.HasFlag(PortraitCameraFitReason.BudgetExhausted), "truncated search disclosed");
    foreach (var budget in new[] { 0, -1, 4097, int.MaxValue })
        Check(Solve(Request(), budget).Status == PortraitCameraFitStatus.InvalidInput, "unbounded/invalid budget rejected");
});
Case("owner cancellation and stale result", () =>
{
    var token = new CancellationTokenSource(); token.Cancel();
    var before = PortraitCameraFit.Solve(Request(), () => identity, token.Token);
    Check(before.Status == PortraitCameraFitStatus.Cancelled && before.Evaluations == 0, "cancel before work");
    var calls = 0; using var duringToken = new CancellationTokenSource();
    var during = PortraitCameraFit.Solve(Request(), () => { if (++calls == 3) duringToken.Cancel(); return identity; }, duringToken.Token);
    Check(during.Status == PortraitCameraFitStatus.Cancelled && !during.TryGetCandidate(identity, out _), "cancel during work discards best");
    var baseline = Solve(Request(), 17); calls = 0;
    using var finalToken = new CancellationTokenSource();
    var final = PortraitCameraFit.Solve(Request(), () =>
    {
        if (++calls == baseline.Evaluations + 2) finalToken.Cancel(); return identity;
    }, finalToken.Token, new(17));
    Check(final.Status == PortraitCameraFitStatus.Cancelled && !final.TryGetCandidate(identity, out _), "cancel final checkpoint discards best");
    foreach (var replacement in new[] { identity with { Owner = Guid.NewGuid() }, identity with { Revision = 13 },
        identity with { Generation = 4 } })
    {
        calls = 0;
        var stale = PortraitCameraFit.Solve(Request(), () => ++calls < 3 ? identity : replacement, default, new(17));
        Check(stale.Status == PortraitCameraFitStatus.StaleOwner && !stale.TryGetCandidate(identity, out _), "owner/revision/generation changes discard best");
        Check(!baseline.TryGetCandidate(replacement, out _), "stale result cannot be consumed");
    }
    calls = 0;
    var finalOwner = PortraitCameraFit.Solve(Request(), () => ++calls == baseline.Evaluations + 2
        ? identity with { Revision = 13 } : identity, default, new(17));
    Check(finalOwner.Status == PortraitCameraFitStatus.StaleOwner && !finalOwner.TryGetCandidate(identity, out _),
        "owner change immediately before return discards completed search");
    Check(PortraitCameraFit.Solve(Request(), () => throw new InvalidOperationException()).Status == PortraitCameraFitStatus.StaleOwner,
        "unavailable owner fails closed");
});

Console.WriteLine($"PortraitCameraFitTests: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
