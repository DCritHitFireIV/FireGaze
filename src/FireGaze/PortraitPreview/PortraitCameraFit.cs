using System.Numerics;

namespace FireGaze.PortraitPreview;

/// <summary>Detached ownership stamp; no native pointer, texture or scene handle.</summary>
internal readonly record struct PortraitCameraFitIdentity(Guid Owner, long Revision, uint Generation);
/// <summary>Local-space geometry supplied by a qualified caller, not inferred from a portrait image.</summary>
internal sealed record PortraitCameraFaceGeometry(PortraitCameraFitIdentity Identity, string CoordinateSpace,
    Vector3[] Landmarks, Vector3 FacingNormal);
/// <summary>
/// Observer coordinates use PortraitObserverCamera's -Z-forward basis. SubjectAnchor and any face
/// geometry must share the audited native portrait local space. Roll is clockwise in the image;
/// roll acts in the camera plane before aspect projection. Vertical FOV and aspect define only this
/// solver's conventional pinhole precheck, not the unverified native projection/rotation contract.
/// </summary>
internal sealed record PortraitCameraFitRequest(PortraitComposition Source, string GameBuild,
    PortraitCameraFitIdentity Identity, PortraitObserverPose Observer, Vector3 SubjectAnchor,
    float VerticalFieldOfViewRadians, float ImageRotationDegrees, float ViewportAspect,
    PortraitCameraFaceGeometry? Face = null);
internal sealed record PortraitCameraFitOptions(int EvaluationBudget = 1024);
internal enum PortraitCameraFitStatus { Candidate, NoCandidate, InvalidInput, UnsupportedBuild, Cancelled, StaleOwner }
internal enum PortraitCameraFacePrecheck { Unknown, InsideManagedFrame, Rejected }
internal enum PortraitCameraNativeAssessment { Unknown }
[Flags]
internal enum PortraitCameraFitReason
{
    None = 0, InvalidInput = 1, UnsupportedBuild = 2, BudgetExhausted = 4, QuantizationRejected = 8,
    PoseAdjusted = 16, PivotAdjusted = 32, ZoomAdjusted = 64, ImageRotationAdjusted = 128,
    OwnerChanged = 256, Cancelled = 512, FaceOutsideManagedFrame = 1024, FaceFacingAway = 2048,
    UnknownFaceGeometry = 4096, UnknownNativeOcclusion = 8192,
}
internal sealed class PortraitCameraFitResult
{
    private readonly PortraitComposition? candidate;
    private readonly PortraitCameraFitIdentity identity;
    private static readonly IReadOnlyList<string> limitations = Array.AsReadOnly(new[]
    {
        "Parameter bounds and FOV are audited only for game build 2026.10.06.0000.0000.",
        "The managed pinhole frame is not the native portrait projection or face-part test.",
        "Native occlusion, dynamic pose/weapon geometry and native save validity remain unknown.",
        "The bounded deterministic search is approximate and may not find every representable candidate.",
        "No renderer lease, native scene, write-back, save operation or input capture is provided.",
    });
    internal PortraitCameraFitResult(PortraitCameraFitStatus status, PortraitCameraFitIdentity identity,
        int evaluations, PortraitCameraFitReason reasons, PortraitCameraFacePrecheck facePrecheck,
        PortraitComposition? candidate = null)
    {
        Status = status; this.identity = identity; Evaluations = evaluations; Reasons = reasons;
        FacePrecheck = facePrecheck; this.candidate = candidate;
    }
    public PortraitCameraFitStatus Status { get; }
    public int Evaluations { get; }
    /// <summary>Search rejection flags may describe discarded samples, not the returned candidate.</summary>
    public PortraitCameraFitReason Reasons { get; }
    public PortraitCameraFacePrecheck FacePrecheck { get; }
    public PortraitCameraNativeAssessment NativeOcclusion => PortraitCameraNativeAssessment.Unknown;
    public PortraitCameraNativeAssessment NativeValidity => PortraitCameraNativeAssessment.Unknown;
    public IReadOnlyList<string> Limitations => limitations;
    /// <summary>Caller must obtain a fresh stamp; consuming a result never performs a native write.</summary>
    public bool TryGetCandidate(PortraitCameraFitIdentity current, out PortraitComposition candidate)
    {
        candidate = null!;
        if (Status != PortraitCameraFitStatus.Candidate || this.candidate is null || identity != current
            || current.Owner == Guid.Empty || current.Revision < 0) return false;
        candidate = this.candidate;
        return true;
    }
}
/// <summary>
/// Pure managed, detached candidate fitting. This is an offline parameter backend, not the independent
/// three-dimensional character scene. It neither calls native code nor certifies save/render safety.
/// </summary>
internal static class PortraitCameraFit
{
    public const string VerifiedGameBuild = "2026.10.06.0000.0000";
    public const string CoordinateSpace = "portrait-local-v1";
    public const int MaximumEvaluationBudget = 4096;
    private const float MinimumPitch = -89 * .01745329238474369f;
    private const float MaximumPitch = 30 * .01745329238474369f;

    /// <summary>Keep the audited native float32 operation order; do not simplify in double.</summary>
    public static float NativeFieldOfView(byte zoom)
    {
        var fov = (float)(200 - Math.Min(zoom, (byte)200)) / 200f;
        fov -= .5f;
        fov += .7799999713897705f;
        return fov;
    }

    public static PortraitCameraFitResult Solve(PortraitCameraFitRequest? request,
        Func<PortraitCameraFitIdentity> readCurrentIdentity, CancellationToken cancellationToken = default,
        PortraitCameraFitOptions? options = null)
    {
        var identity = request?.Identity ?? default;
        var evaluated = 0;
        var reasons = PortraitCameraFitReason.UnknownNativeOcclusion;
        var faceCheck = PortraitCameraFacePrecheck.Unknown;
        if (request?.Face is null) reasons |= PortraitCameraFitReason.UnknownFaceGeometry;
        PortraitCameraFitResult Result(PortraitCameraFitStatus status, PortraitCameraFitReason reason = 0,
            PortraitComposition? candidate = null) => new(status, identity, evaluated, reasons | reason, faceCheck, candidate);
        var budget = (options ?? new()).EvaluationBudget;
        if (request is null || readCurrentIdentity is null || identity.Owner == Guid.Empty || identity.Revision < 0
            || budget is < 1 or > MaximumEvaluationBudget || request.Source?.IsValid != true
            || !Finite(request.Observer.Position) || !float.IsFinite(request.Observer.YawRadians)
            || !float.IsFinite(request.Observer.PitchRadians) || !Finite(request.SubjectAnchor)
            || !float.IsFinite(request.VerticalFieldOfViewRadians) || request.VerticalFieldOfViewRadians <= 0
            || request.VerticalFieldOfViewRadians >= Math.PI || !float.IsFinite(request.ViewportAspect)
            || request.ViewportAspect <= 0 || !float.IsFinite(request.ImageRotationDegrees))
            return Result(PortraitCameraFitStatus.InvalidInput, PortraitCameraFitReason.InvalidInput);
        if (request.GameBuild != VerifiedGameBuild)
            return Result(PortraitCameraFitStatus.UnsupportedBuild, PortraitCameraFitReason.UnsupportedBuild);

        PortraitCameraFitResult? Checkpoint()
        {
            if (cancellationToken.IsCancellationRequested)
                return Result(PortraitCameraFitStatus.Cancelled, PortraitCameraFitReason.Cancelled);
            PortraitCameraFitIdentity current;
            try { current = readCurrentIdentity(); }
            catch (Exception) { return Result(PortraitCameraFitStatus.StaleOwner, PortraitCameraFitReason.OwnerChanged); }
            if (cancellationToken.IsCancellationRequested)
                return Result(PortraitCameraFitStatus.Cancelled, PortraitCameraFitReason.Cancelled);
            return current != identity ? Result(PortraitCameraFitStatus.StaleOwner, PortraitCameraFitReason.OwnerChanged) : null;
        }
        if (Checkpoint() is { } stopped) return stopped;

        // Copy optional caller-owned arrays before validation. Later mutations cannot change a solve.
        PortraitCameraFaceGeometry? face = null;
        if (request.Face is { } supplied)
        {
            if (supplied.Identity != identity || supplied.CoordinateSpace != CoordinateSpace
                || supplied.Landmarks is not { Length: > 0 and <= 256 } || !Finite(supplied.FacingNormal)
                || Point.From(supplied.FacingNormal).Length < 1e-12)
                return Result(PortraitCameraFitStatus.InvalidInput, PortraitCameraFitReason.InvalidInput);
            face = supplied with { Landmarks = (Vector3[])supplied.Landmarks.Clone() };
            if (face.Landmarks.Any(p => !Finite(p)))
                return Result(PortraitCameraFitStatus.InvalidInput, PortraitCameraFitReason.InvalidInput);
        }

        var observer = Point.From(request.Observer.Position);
        var subject = Point.From(request.SubjectAnchor);
        Basis(request.Observer.YawRadians, request.Observer.PitchRadians, out var intentForward, out var intentRight, out var intentUp);
        var toSubject = subject - observer;
        var depth = toSubject.Dot(intentForward);
        if (depth <= 1e-8) return Result(PortraitCameraFitStatus.InvalidInput, PortraitCameraFitReason.InvalidInput);
        var desiredTan = Math.Tan(request.VerticalFieldOfViewRadians / 2d);
        var desiredScale = depth * desiredTan;
        var intentX = toSubject.Dot(intentRight);
        var intentY = toSubject.Dot(intentUp);
        var desiredRoll = Math.Clamp(request.ImageRotationDegrees, -90f, 90f);
        var roll = (short)Math.Round(desiredRoll, MidpointRounding.AwayFromZero);
        var desiredAnchor = Project(intentX, intentY, desiredScale, request.ViewportAspect,
            request.ImageRotationDegrees * Math.PI / 180);
        var intentNativePitch = Math.Asin(Math.Clamp(intentForward.Y, -1, 1));
        var intentNativeYaw = Math.Atan2(-intentForward.X, -intentForward.Z);
        var fittedPitch = Math.Clamp(intentNativePitch, MinimumPitch, MaximumPitch);

        var zooms = ZoomCandidates(request.VerticalFieldOfViewRadians, request.Source.CameraZoom);
        var distances = Unique(
            Math.Clamp(request.Source.CameraDistance, .5, 2), Math.Clamp(depth, .5, 2), 1.25,
            .5005, 1.9995, .5, 2, .75, 1, 1.5, 1.75);
        var pitches = Unique(fittedPitch, Math.Clamp(fittedPitch + .001, MinimumPitch + .0005, MaximumPitch - .0005),
            Math.Clamp(fittedPitch - .001, MinimumPitch + .0005, MaximumPitch - .0005),
            Math.Clamp(request.Source.CameraPitch, MinimumPitch + .0005, MaximumPitch - .0005));
        PortraitComposition? best = null;
        var bestCost = double.PositiveInfinity;
        var bestReasons = PortraitCameraFitReason.None;
        var exhausted = false;
        foreach (var zoom in zooms)
        {
            var tan = Math.Tan(NativeFieldOfView(zoom) / 2d);
            foreach (var pitch in pitches)
            {
                // Explicit observer/native conversion: native yaw is negated; pitch keeps its sign.
                Basis(-intentNativeYaw, pitch, out var forward, out var right, out var up);
                var wantedCamera = subject - forward * (desiredScale / tan) - right * intentX - up * intentY;
                foreach (var distance in distances)
                {
                    if (evaluated == budget) { exhausted = true; goto Finished; }
                    if (Checkpoint() is { } cancelled) return cancelled;
                    evaluated++;
                    var wantedPivot = wantedCamera + forward * distance;
                    var pivot = new Point(Math.Clamp(wantedPivot.X, -1.6, 1.6), Math.Clamp(wantedPivot.Y, -1, .5),
                        Math.Clamp(wantedPivot.Z, -.5, .5));
                    var position = pivot - forward * distance;
                    if (!Quantize(position, request.Source.CameraPosition.W, out var qp)
                        || !Quantize(pivot, request.Source.CameraTarget.W, out var qt)
                        || !WithinNativeDomain(qp, qt, out var actualDistance, out var actualYaw, out var actualPitch))
                    { reasons |= PortraitCameraFitReason.QuantizationRejected; continue; }

                    var candidate = request.Source with
                    {
                        CameraPosition = qp, CameraTarget = qt, CameraDistance = actualDistance,
                        CameraYaw = actualYaw, CameraPitch = actualPitch, CameraZoom = zoom,
                        CameraZoomNormalized = NativeFieldOfView(zoom), ImageRotation = roll,
                    };
                    var camera = Point.From(qp);
                    Basis(-actualYaw, actualPitch, out var actualForward, out var actualRight, out var actualUp);
                    var subjectRelative = subject - camera;
                    var actualDepth = subjectRelative.Dot(actualForward);
                    if (actualDepth <= 1e-8) continue;
                    var actualScale = actualDepth * tan;
                    var anchor = Project(subjectRelative.Dot(actualRight), subjectRelative.Dot(actualUp), actualScale,
                        request.ViewportAspect, roll * Math.PI / 180);
                    if (face is not null && !FaceInside(face, camera, actualForward, actualRight, actualUp, tan,
                        request.ViewportAspect, roll, out var faceReason))
                    { reasons |= faceReason; faceCheck = PortraitCameraFacePrecheck.Rejected; continue; }

                    // View direction and framing dominate continuity. Scale is logarithmic so both
                    // too-large and too-small subjects contribute symmetrically. These weights are
                    // approximation policy, not native validation thresholds.
                    var directionError = Math.Max(0, 1 - actualForward.Dot(intentForward));
                    var frameError = Square(anchor.X - desiredAnchor.X) + Square(anchor.Y - desiredAnchor.Y)
                        + Square(Math.Log(actualScale / desiredScale));
                    var continuity = (camera - observer).Length / Math.Max(.5, depth)
                        + Math.Abs(NativeFieldOfView(zoom) - request.VerticalFieldOfViewRadians)
                        + .01 * Math.Abs(actualDistance - request.Source.CameraDistance);
                    var cost = directionError * 10000 + frameError * 100 + continuity * .001;
                    if (!double.IsFinite(cost) || cost >= bestCost) continue;
                    bestCost = cost; best = candidate;
                    bestReasons = PortraitCameraFitReason.None;
                    if (directionError > 1e-10 || (camera - observer).Length > .001)
                        bestReasons |= PortraitCameraFitReason.PoseAdjusted;
                    if ((pivot - wantedPivot).Length > 1e-9) bestReasons |= PortraitCameraFitReason.PivotAdjusted;
                    if (Math.Abs(NativeFieldOfView(zoom) - request.VerticalFieldOfViewRadians) > 1e-6)
                        bestReasons |= PortraitCameraFitReason.ZoomAdjusted;
                    if (Math.Abs(roll - request.ImageRotationDegrees) > 1e-6)
                        bestReasons |= PortraitCameraFitReason.ImageRotationAdjusted;
                }
            }
        }
    Finished:
        if (exhausted) reasons |= PortraitCameraFitReason.BudgetExhausted;
        if (Checkpoint() is { } finalStop) return finalStop;
        if (best is null) return Result(PortraitCameraFitStatus.NoCandidate);
        faceCheck = face is null ? PortraitCameraFacePrecheck.Unknown : PortraitCameraFacePrecheck.InsideManagedFrame;
        return Result(PortraitCameraFitStatus.Candidate, bestReasons, best);
    }

    private static bool WithinNativeDomain(PortraitVector4 position, PortraitVector4 target,
        out float distance, out float yaw, out float pitch)
    {
        distance = yaw = pitch = 0;
        if (!position.IsFinite || !target.IsFinite || target.X is < -1.6f or > 1.6f
            || target.Y is < -1 or > .5f || target.Z is < -.5f or > .5f) return false;
        var offset = Point.From(position) - Point.From(target);
        var length = offset.Length;
        if (length is < .5 or > 2) return false;
        var angle = -Math.Asin(Math.Clamp(offset.Y / length, -1, 1));
        if (angle < MinimumPitch || angle > MaximumPitch) return false;
        distance = (float)length; yaw = (float)Math.Atan2(offset.X, offset.Z); pitch = (float)angle;
        return distance is >= .5f and <= 2 && pitch >= MinimumPitch && pitch <= MaximumPitch;
    }

    private static bool FaceInside(PortraitCameraFaceGeometry face, Point camera, Point forward, Point right,
        Point up, double tan, double aspect, short roll, out PortraitCameraFitReason reason)
    {
        var center = new Point();
        foreach (var landmark in face.Landmarks)
        {
            var point = Point.From(landmark); center += point;
            var local = point - camera;
            var depth = local.Dot(forward);
            if (depth <= 1e-8) { reason = PortraitCameraFitReason.FaceOutsideManagedFrame; return false; }
            var screen = Project(local.Dot(right), local.Dot(up), depth * tan, aspect, roll * Math.PI / 180);
            if (Math.Abs(screen.X) > 1 || Math.Abs(screen.Y) > 1)
            { reason = PortraitCameraFitReason.FaceOutsideManagedFrame; return false; }
        }
        center *= 1d / face.Landmarks.Length;
        if (Point.From(face.FacingNormal).Dot(camera - center) <= 0)
        { reason = PortraitCameraFitReason.FaceFacingAway; return false; }
        reason = PortraitCameraFitReason.None;
        return true;
    }

    private static List<byte> ZoomCandidates(float desired, byte original)
    {
        var nearest = 0; var best = double.PositiveInfinity;
        for (var i = 0; i <= 200; i++)
        {
            var difference = Math.Abs(NativeFieldOfView((byte)i) - desired);
            if (difference < best) { best = difference; nearest = i; }
        }
        var result = new List<byte>(201);
        void Add(int zoom) { var value = (byte)Math.Clamp(zoom, 0, 200); if (!result.Contains(value)) result.Add(value); }
        Add(nearest); Add(original); Add(0); Add(200);
        for (var i = 25; i < 200; i += 25) Add(i);
        Add(nearest - 1); Add(nearest + 1); Add(nearest - 2); Add(nearest + 2);
        for (var i = 0; i <= 200; i++) Add(i);
        return result;
    }

    private static List<double> Unique(params double[] values)
    {
        var result = new List<double>(values.Length);
        foreach (var value in values) if (!result.Contains(value)) result.Add(value);
        return result;
    }
    private static bool Quantize(Point value, float w, out PortraitVector4 result)
    {
        result = new((float)(Half)value.X, (float)(Half)value.Y, (float)(Half)value.Z, (float)(Half)w);
        return result.IsFinite;
    }
    private static (double X, double Y) Project(double x, double y, double depthTan, double aspect, double radians)
        => ((x * Math.Cos(radians) + y * Math.Sin(radians)) / (depthTan * aspect),
            (-x * Math.Sin(radians) + y * Math.Cos(radians)) / depthTan);
    private static void Basis(double yaw, double pitch, out Point forward, out Point right, out Point up)
    {
        var sy = Math.Sin(yaw); var cy = Math.Cos(yaw); var sp = Math.Sin(pitch); var cp = Math.Cos(pitch);
        forward = new(sy * cp, sp, -cy * cp); right = new(cy, 0, sy); up = new(-sy * sp, cp, cy * sp);
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static double Square(double value) => value * value;
    private readonly record struct Point(double X, double Y, double Z)
    {
        public static Point From(Vector3 value) => new(value.X, value.Y, value.Z);
        public static Point From(PortraitVector4 value) => new(value.X, value.Y, value.Z);
        public double Dot(Point value) => X * value.X + Y * value.Y + Z * value.Z;
        public double Length => Math.Sqrt(Dot(this));
        public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Point operator *(Point a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    }
}
