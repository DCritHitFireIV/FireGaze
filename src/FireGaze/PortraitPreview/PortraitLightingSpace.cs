using System.Numerics;

namespace FireGaze.PortraitPreview;

/// <summary>Reference-camera state for the portrait workspace. Native values stay in radians and game units.</summary>
internal readonly record struct PortraitSpaceCamera(float AzimuthRadians, float ElevationRadians, float Distance)
{
    public float Yaw => AzimuthRadians;
    public float Pitch => ElevationRadians;
}

internal readonly record struct PortraitSpaceProjection(Vector2 Screen, float Depth, bool IsVisible);

/// <summary>Coalesces consecutive wheel ticks into one undoable camera edit.</summary>
internal sealed class PortraitSpaceWheelBatch
{
    private bool pending;

    public void Observe(float wheelDelta)
    {
        if (float.IsFinite(wheelDelta) && MathF.Abs(wheelDelta) > .001f)
            pending = true;
    }

    public bool TryCommit(float wheelDelta, bool anotherGestureActive)
    {
        if (float.IsFinite(wheelDelta) && MathF.Abs(wheelDelta) > .001f)
        {
            pending = true;
            return false;
        }
        if (anotherGestureActive) return false;
        var commit = pending;
        pending = false;
        return commit;
    }

    public void Cancel() => pending = false;
}

/// <summary>
/// A small interactive 3D reference space for the portrait editor. It maps the orbit camera and
/// one directional light to the native portrait fields; it does not claim to create point lights
/// or a separate rendered scene.
/// </summary>
internal sealed class PortraitLightingSpace
{
    public const float MinimumDistance = .35f;
    public const float MaximumDistance = 3.5f;
    public const float MinimumElevation = -1.22f;
    public const float MaximumElevation = 1.22f;
    private const float OrbitRadiansPerViewport = MathF.PI;
    private readonly float targetX;
    private readonly float targetY;
    private readonly float targetZ;

    public PortraitSpaceCamera Camera { get; private set; }
    public PortraitLightAngles DirectionalLight { get; private set; }

    private PortraitLightingSpace(PortraitSpaceCamera camera, PortraitLightAngles light, PortraitVector4 target)
    {
        Camera = camera;
        DirectionalLight = light;
        targetX = target.X;
        targetY = target.Y;
        targetZ = target.Z;
    }

    public static PortraitLightingSpace From(PortraitComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var camera = new PortraitSpaceCamera(
            WrapRadians(composition.CameraYaw),
            Math.Clamp(composition.CameraPitch, MinimumElevation, MaximumElevation),
            Math.Clamp(float.IsFinite(composition.CameraDistance) ? composition.CameraDistance : 1.2f,
                MinimumDistance, MaximumDistance));
        var vertical = composition.DirectionalVerticalAngle;
        var horizontal = composition.DirectionalHorizontalAngle;
        if (!PortraitLightDirection.TryNormalize(vertical, horizontal, out var light))
            light = new(0, 0);
        return new(camera, light, composition.CameraTarget);
    }

    public void Orbit(Vector2 pixelDelta, float viewportHeight)
    {
        if (!float.IsFinite(pixelDelta.X) || !float.IsFinite(pixelDelta.Y)
            || !float.IsFinite(viewportHeight) || viewportHeight <= 0) return;
        var scale = OrbitRadiansPerViewport / viewportHeight;
        Camera = Camera with
        {
            AzimuthRadians = WrapRadians(Camera.AzimuthRadians + pixelDelta.X * scale),
            ElevationRadians = Math.Clamp(Camera.ElevationRadians - pixelDelta.Y * scale,
                MinimumElevation, MaximumElevation),
        };
    }

    public void Dolly(float wheelDelta)
    {
        if (!float.IsFinite(wheelDelta)) return;
        Camera = Camera with { Distance = Math.Clamp(Camera.Distance - wheelDelta * .08f, MinimumDistance, MaximumDistance) };
    }

    public bool TrySetLightFromViewport(Vector2 normalized)
    {
        if (!float.IsFinite(normalized.X) || !float.IsFinite(normalized.Y)) return false;
        var point = Vector2.Clamp(normalized, Vector2.Zero, Vector2.One) * 2f - Vector2.One;
        if (!PortraitLightDirection.TryFromProjection(point, PortraitLightView.Top, false, out var angles)) return false;
        DirectionalLight = angles;
        return true;
    }

    public PortraitComposition ApplyTo(PortraitComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var offset = new Vector3(
            MathF.Sin(Camera.AzimuthRadians) * MathF.Cos(Camera.ElevationRadians) * Camera.Distance,
            -MathF.Sin(Camera.ElevationRadians) * Camera.Distance,
            MathF.Cos(Camera.AzimuthRadians) * MathF.Cos(Camera.ElevationRadians) * Camera.Distance);
        return composition with
        {
            CameraYaw = Camera.AzimuthRadians,
            CameraPitch = Camera.ElevationRadians,
            CameraDistance = Camera.Distance,
            CameraPosition = new PortraitVector4(composition.CameraTarget.X + offset.X, composition.CameraTarget.Y + offset.Y,
                composition.CameraTarget.Z + offset.Z, composition.CameraPosition.W),
            DirectionalVerticalAngle = (short)Math.Clamp(MathF.Round(DirectionalLight.VerticalDegrees), -180, 180),
            DirectionalHorizontalAngle = (short)Math.Clamp(MathF.Round(DirectionalLight.HorizontalDegrees), -180, 180),
        };
    }

    /// <summary>
    /// Validates a detached spatial candidate against the range that the native portrait camera
    /// accepts. The reference viewport may use a wider artistic orbit, but only a candidate that
    /// is already inside this domain may cross the native write boundary.
    /// </summary>
    public static bool TryCreateNativeCandidate(PortraitComposition source, PortraitComposition desired,
        out PortraitComposition candidate)
    {
        candidate = null!;
        if (source?.IsValid != true || desired?.IsValid != true) return false;
        if (desired.CameraTarget.X is < -1.6f or > 1.6f || desired.CameraTarget.Y is < -1f or > .5f
            || desired.CameraTarget.Z is < -.5f or > .5f || desired.CameraDistance is < .5f or > 2f
            || desired.CameraPitch < -89f * .01745329238474369f || desired.CameraPitch > 30f * .01745329238474369f
            || !float.IsFinite(desired.CameraYaw) || !float.IsFinite(desired.CameraPitch)
            || !float.IsFinite(desired.CameraDistance)) return false;
        var offset = new Vector3(desired.CameraPosition.X - desired.CameraTarget.X,
            desired.CameraPosition.Y - desired.CameraTarget.Y,
            desired.CameraPosition.Z - desired.CameraTarget.Z);
        var measured = offset.Length();
        if (!float.IsFinite(measured) || MathF.Abs(measured - desired.CameraDistance) > .025f) return false;
        var expectedPitch = -MathF.Asin(Math.Clamp(offset.Y / measured, -1f, 1f));
        if (!float.IsFinite(expectedPitch) || MathF.Abs(expectedPitch - desired.CameraPitch) > .03f) return false;
        var expectedYaw = MathF.Atan2(offset.X, offset.Z);
        if (!float.IsFinite(expectedYaw) || MathF.Abs(WrapRadians(expectedYaw - desired.CameraYaw)) > .03f) return false;
        if (desired.DirectionalVerticalAngle is < -180 or > 180 || desired.DirectionalHorizontalAngle is < -180 or > 180)
            return false;
        candidate = desired;
        return true;
    }

    public PortraitSpaceProjection Project(Vector3 world, Vector2 viewportSize = default)
    {
        if (viewportSize == default) viewportSize = new(320, 260);
        var cameraPosition = new Vector3(targetX, targetY, targetZ) + CameraOffset();
        var target = new Vector3(targetX, targetY, targetZ);
        // Geometry supplied by the viewport is expressed around the local subject origin.
        // Translate it to the native camera target before projecting so nonzero portrait
        // target coordinates do not shift the proxy subject away from the view center.
        world += target;
        var forward = target - cameraPosition;
        if (forward.LengthSquared() <= 1e-8f) return new(default, 0, false);
        forward = Vector3.Normalize(forward);
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        if (right.LengthSquared() <= 1e-8f) right = Vector3.UnitX;
        var up = Vector3.Normalize(Vector3.Cross(forward, right));
        var delta = world - cameraPosition;
        var depth = Vector3.Dot(delta, forward);
        if (!float.IsFinite(depth) || depth <= .05f) return new(default, depth, false);
        var focal = MathF.Min(viewportSize.X, viewportSize.Y) * .72f;
        var scale = focal / depth;
        var screen = new Vector2(
            viewportSize.X * .5f + Vector3.Dot(delta, right) * scale,
            viewportSize.Y * .53f - Vector3.Dot(delta, up) * scale);
        return new(screen, depth, float.IsFinite(screen.X) && float.IsFinite(screen.Y));
    }

    public Vector3 CameraOffset() => new(
        MathF.Sin(Camera.AzimuthRadians) * MathF.Cos(Camera.ElevationRadians) * Camera.Distance,
        -MathF.Sin(Camera.ElevationRadians) * Camera.Distance,
        MathF.Cos(Camera.AzimuthRadians) * MathF.Cos(Camera.ElevationRadians) * Camera.Distance);

    public Vector3 LightAnchor(float distance = 1.55f)
    {
        if (!PortraitLightDirection.TryDirection(DirectionalLight.VerticalDegrees, DirectionalLight.HorizontalDegrees, out var direction))
            return new(0, 0, distance);
        return direction * distance;
    }

    private static float WrapRadians(float radians)
    {
        if (!float.IsFinite(radians)) return 0;
        var wrapped = radians % (MathF.PI * 2);
        if (wrapped > MathF.PI) wrapped -= MathF.PI * 2;
        if (wrapped <= -MathF.PI) wrapped += MathF.PI * 2;
        return wrapped;
    }
}
