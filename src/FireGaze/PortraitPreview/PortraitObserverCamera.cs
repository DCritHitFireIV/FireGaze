using System.Numerics;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Detached observer pose. Zero angles face -Z; positive yaw turns toward +X and positive pitch looks up.
/// This is not a native portrait pose and carries no projection or field-of-view assumptions.
/// </summary>
internal readonly record struct PortraitObserverPose(Vector3 Position, float YawRadians, float PitchRadians);

internal readonly record struct PortraitObserverCameraSettings(float MoveSpeed, double StartupSeconds,
    double StopSeconds, double MaximumStepSeconds)
{
    public static PortraitObserverCameraSettings Default => new(1f, .15, .10, .05);
}

/// <summary>
/// MoveLocal is right/up/forward input; look is an angular delta, not a rate. The future UI must supply
/// explicit viewport focus/capture and block input while text, modals or another control owns the gesture.
/// This type neither reads physical keys nor suppresses/produces game input.
/// </summary>
internal readonly record struct PortraitObserverInput(Vector3 MoveLocal, Vector2 LookDeltaRadians,
    bool ViewportFocused, bool InputCaptured, bool InputBlocked);

internal readonly record struct PortraitObserverCameraSnapshot(PortraitObserverPose Pose, Vector3 Velocity,
    Vector3 Forward, Vector3 Right, Vector3 Up);

/// <summary>
/// Managed observer-camera math for a future independent portrait scene. UNCONNECTED: no native target,
/// UI, GPU, scene allocation, input hook or portrait write-back. Call on one owner thread.
/// </summary>
internal sealed class PortraitObserverCamera
{
    private const float MaximumPitch = (float)(Math.PI / 2 - .01);
    private readonly PortraitObserverCameraSettings settings;
    private PortraitObserverPose pose;
    private Vector3 velocity;

    public PortraitObserverCamera(PortraitObserverPose initial = default,
        PortraitObserverCameraSettings? settings = null)
    {
        if (!Finite(initial.Position) || !float.IsFinite(initial.YawRadians) || !float.IsFinite(initial.PitchRadians))
            throw new ArgumentOutOfRangeException(nameof(initial));
        var candidate = settings ?? PortraitObserverCameraSettings.Default;
        if (!float.IsFinite(candidate.MoveSpeed) || candidate.MoveSpeed <= 0 || candidate.MoveSpeed > 1000
            || !ValidResponse(candidate.StartupSeconds) || !ValidResponse(candidate.StopSeconds)
            || !double.IsFinite(candidate.MaximumStepSeconds) || candidate.MaximumStepSeconds <= 0
            || candidate.MaximumStepSeconds > .25)
            throw new ArgumentOutOfRangeException(nameof(settings));
        this.settings = candidate;
        pose = initial with
        {
            YawRadians = WrapYaw(initial.YawRadians),
            PitchRadians = Math.Clamp(initial.PitchRadians, -MaximumPitch, MaximumPitch),
        };
    }

    public PortraitObserverCameraSnapshot Snapshot
    {
        get
        {
            Basis(pose, out var forward, out var right, out var up);
            return new(pose, velocity, forward, right, up);
        }
    }

    /// <summary>
    /// Integrates one owned input interval. A rejected frame immediately clears momentum without moving
    /// or turning. Long frames are capped once; lost time is discarded, never queued for catch-up.
    /// </summary>
    public bool Step(PortraitObserverInput input, double elapsedSeconds)
    {
        if (!input.ViewportFocused || !input.InputCaptured || input.InputBlocked
            || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0 || !Finite(input.MoveLocal)
            || !float.IsFinite(input.LookDeltaRadians.X) || !float.IsFinite(input.LookDeltaRadians.Y))
        {
            ReleaseInput();
            return false;
        }

        var nextPose = pose with
        {
            YawRadians = WrapYaw((double)pose.YawRadians + input.LookDeltaRadians.X),
            PitchRadians = (float)Math.Clamp((double)pose.PitchRadians + input.LookDeltaRadians.Y,
                -MaximumPitch, MaximumPitch),
        };
        Basis(nextPose, out var forward, out var right, out var up);
        var movement = Vector3.Clamp(input.MoveLocal, -Vector3.One, Vector3.One);
        if (movement.LengthSquared() > 1) movement = Vector3.Normalize(movement);
        var target = (right * movement.X + up * movement.Y + forward * movement.Z) * settings.MoveSpeed;
        var tau = movement == Vector3.Zero ? settings.StopSeconds : settings.StartupSeconds;
        var dt = Math.Min(elapsedSeconds, settings.MaximumStepSeconds);
        var interval = dt / tau;
        // For tiny intervals, direct subtraction from exp rounds away the response and can even
        // reverse travel. Taylor coefficients retain the first nonzero displacement term dt²/(2*tau).
        var response = interval < .001
            ? interval * (1 - interval * (.5 - interval * (1d / 6 - interval * (1d / 24 - interval / 120))))
            : 1 - Math.Exp(-interval);
        var travelResponse = interval < .001
            ? dt * interval * (.5 - interval * (1d / 6 - interval * (1d / 24 - interval * (1d / 120 - interval / 720))))
            : dt - tau * response;

        // Integrate dv/dt=(target-v)/tau analytically; Euler position updates would be frame-rate dependent.
        float IntegratePosition(float start, float speed, float desired)
            => (float)(start + speed * dt + (desired - (double)speed) * travelResponse);
        float IntegrateVelocity(float speed, float desired)
            => (float)(speed + (desired - (double)speed) * response);
        var position = new Vector3(
            IntegratePosition(pose.Position.X, velocity.X, target.X),
            IntegratePosition(pose.Position.Y, velocity.Y, target.Y),
            IntegratePosition(pose.Position.Z, velocity.Z, target.Z));
        var nextVelocity = new Vector3(IntegrateVelocity(velocity.X, target.X),
            IntegrateVelocity(velocity.Y, target.Y), IntegrateVelocity(velocity.Z, target.Z));
        if (!Finite(position) || !Finite(nextVelocity))
        {
            ReleaseInput();
            return false;
        }
        pose = nextPose with { Position = position };
        velocity = nextVelocity;
        return true;
    }

    /// <summary>Call immediately on close, lost capture or lifecycle stop; no inertial tail survives.</summary>
    public void ReleaseInput() => velocity = Vector3.Zero;

    private static void Basis(PortraitObserverPose value, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        var sinYaw = MathF.Sin(value.YawRadians);
        var cosYaw = MathF.Cos(value.YawRadians);
        var sinPitch = MathF.Sin(value.PitchRadians);
        var cosPitch = MathF.Cos(value.PitchRadians);
        forward = new(sinYaw * cosPitch, sinPitch, -cosYaw * cosPitch);
        right = new(cosYaw, 0, sinYaw);
        up = new(-sinYaw * sinPitch, cosPitch, cosYaw * sinPitch);
    }

    private static float WrapYaw(double angle)
    {
        angle %= Math.Tau;
        if (angle > Math.PI) angle -= Math.Tau;
        if (angle <= -Math.PI) angle += Math.Tau;
        return (float)angle;
    }

    private static bool ValidResponse(double value) => double.IsFinite(value) && value > 0 && value <= 10;
    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
