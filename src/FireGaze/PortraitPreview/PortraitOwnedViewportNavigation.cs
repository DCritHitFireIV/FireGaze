using System.Numerics;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Applies an explicitly owned observer input sample to the detached native camera.
/// The class does not read keyboard state or install an input hook. A caller must provide
/// a real viewport capture and a neutral arm sample before movement is accepted.
/// </summary>
internal sealed class PortraitOwnedViewportNavigation
{
    private static readonly PortraitObserverCameraSettings Settings = new(1f, .15, .10, .05);
    private PortraitObserverCamera? camera;
    private PortraitOwnedSceneObserverCameraSnapshot published;
    private long attempt;
    private bool hasPublished;
    private bool armed;
    private bool closed;

    internal bool Step(long currentAttempt, PortraitOwnedSceneObserverCameraSnapshot current,
        PortraitObserverInput input, double elapsedSeconds,
        out PortraitOwnedSceneObserverCameraSnapshot result)
    {
        result = current;
        if (currentAttempt <= 0 || !current.IsNativeSupported)
        {
            Reset();
            return false;
        }

        if (!hasPublished || currentAttempt != attempt || !Same(current, published))
        {
            attempt = currentAttempt;
            published = current;
            hasPublished = true;
            armed = false;
            closed = false;
            camera = Create(current);
        }

        if (closed || !input.ViewportFocused || !input.InputCaptured || input.InputBlocked)
        {
            camera?.ReleaseInput();
            armed = false;
            published = current;
            result = current;
            return false;
        }

        var neutral = input.MoveLocal == Vector3.Zero && input.LookDeltaRadians == Vector2.Zero;
        if (!armed)
        {
            if (!neutral)
            {
                camera?.ReleaseInput();
                result = current;
                return false;
            }

            armed = true;
            result = current;
            return false;
        }

        var previousPosition = camera?.Snapshot.Pose.Position ?? current.Position;
        if (camera is null || !camera.Step(input, elapsedSeconds))
        {
            camera?.ReleaseInput();
            armed = false;
            result = current;
            return false;
        }

        var managed = camera.Snapshot;
        if (!TryTranslate(current, previousPosition, managed, out var next, out var clipped))
        {
            camera.ReleaseInput();
            armed = false;
            result = current;
            return false;
        }

        published = next;
        result = next;
        if (clipped)
        {
            // Rebase at the exact native boundary. The managed integrator must not retain
            // an outward velocity which would repeatedly revoke the same rendered image.
            camera = Create(next);
            camera.ReleaseInput();
        }
        return !Same(next, current);
    }

    /// <summary>Stops momentum and requires a new neutral arm sample on this attempt.</summary>
    internal void ReleaseInput()
    {
        camera?.ReleaseInput();
        armed = false;
        closed = true;
    }

    private static PortraitObserverCamera Create(PortraitOwnedSceneObserverCameraSnapshot value)
        => new(new(value.Position, -value.Yaw, value.Pitch), Settings);

    private static bool TryTranslate(PortraitOwnedSceneObserverCameraSnapshot source, Vector3 previousPosition,
        PortraitObserverCameraSnapshot managed, out PortraitOwnedSceneObserverCameraSnapshot result,
        out bool clipped)
    {
        result = default;
        clipped = false;
        if (!float.IsFinite(managed.Pose.YawRadians) || !float.IsFinite(managed.Pose.PitchRadians)
            || !Finite(managed.Pose.Position)) return false;

        // Native look changes orbit Position around Target without moving the detached
        // integrator. Only this step's managed displacement is a target translation.
        var translation = managed.Pose.Position - previousPosition;
        var target = source.Target + translation;
        var bounded = new Vector3(
            Math.Clamp(target.X, -1.6f, 1.6f),
            Math.Clamp(target.Y, -1f, .5f),
            Math.Clamp(target.Z, -.5f, .5f));
        clipped = bounded != target;

        var yaw = -managed.Pose.YawRadians;
        // Keep the native representation close to the submitted snapshot. +π and -π
        // are the same orbit, but stable representation avoids needless controller reloads.
        var yawDelta = MathF.IEEERemainder(yaw - source.Yaw, MathF.Tau);
        yaw = MathF.Abs(yawDelta) < .00001f ? source.Yaw : source.Yaw + yawDelta;
        var pitch = Math.Clamp(managed.Pose.PitchRadians, -89f * MathF.PI / 180, 30f * MathF.PI / 180);
        clipped |= pitch != managed.Pose.PitchRadians;
        var offset = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch),
            -MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch)) * source.Distance;
        result = new(bounded + offset, bounded, yaw, pitch, source.Distance);
        return result.IsNativeSupported;
    }

    private void Reset()
    {
        camera = null;
        attempt = 0;
        hasPublished = false;
        armed = false;
        closed = false;
        published = default;
    }

    private static bool Same(PortraitOwnedSceneObserverCameraSnapshot left,
        PortraitOwnedSceneObserverCameraSnapshot right)
        => left.Position == right.Position && left.Target == right.Target
            && left.Yaw == right.Yaw && left.Pitch == right.Pitch && left.Distance == right.Distance;
    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
