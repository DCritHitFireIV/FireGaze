using System.Numerics;

namespace FireGaze.PortraitPreview;

internal enum PortraitSpaceView { Perspective, Front, Side, Top }

/// <summary>A UI-only observer. Moving it never changes the game's portrait camera or character.</summary>
internal sealed class PortraitSpaceObserver
{
    private float yaw = .65f;
    private float pitch = .32f;
    private float distance = 6f;

    public void SetView(PortraitSpaceView view)
    {
        (yaw, pitch) = view switch
        {
            PortraitSpaceView.Front => (0f, 0f),
            PortraitSpaceView.Side => (MathF.PI / 2, 0f),
            PortraitSpaceView.Top => (0f, 1.52f),
            _ => (.65f, .32f),
        };
        distance = 6;
    }

    public void Orbit(Vector2 delta, float height)
    {
        if (!Valid(new(height, height)) || !float.IsFinite(delta.X) || !float.IsFinite(delta.Y)) return;
        yaw = (yaw + delta.X * MathF.PI / height) % (2 * MathF.PI);
        pitch = Math.Clamp(pitch - delta.Y * MathF.PI / height, -1.52f, 1.52f);
    }

    public void Dolly(float wheel)
    {
        if (float.IsFinite(wheel)) distance = Math.Clamp(distance - wheel * .3f, 4, 12);
    }

    public PortraitSpaceProjection Project(Vector3 world, Vector2 size)
    {
        if (!Valid(size) || !Finite(world)) return new(default, 0, false);
        Basis(out var origin, out var forward, out var right, out var up);
        var delta = world - origin;
        var depth = Vector3.Dot(delta, forward);
        if (!float.IsFinite(depth) || depth <= .05f) return new(default, depth, false);
        var scale = Focal(size) / depth;
        var screen = size * .5f + new Vector2(Vector3.Dot(delta, right), -Vector3.Dot(delta, up)) * scale;
        return new(screen, depth, float.IsFinite(screen.X) && float.IsFinite(screen.Y));
    }

    public bool TrySphereDirection(Vector2 screen, Vector2 size, float radius, Vector3 preferred, out Vector3 direction)
    {
        direction = default;
        if (!Valid(size) || !float.IsFinite(screen.X) || !float.IsFinite(screen.Y)
            || !float.IsFinite(radius) || radius <= 0 || !Finite(preferred)) return false;
        Basis(out var origin, out var forward, out var right, out var up);
        var delta = (screen - size * .5f) / Focal(size);
        var ray = Vector3.Normalize(forward + right * delta.X - up * delta.Y);
        var along = Vector3.Dot(origin, ray);
        var discriminant = along * along - (origin.LengthSquared() - radius * radius);
        Vector3 point;
        if (discriminant >= 0)
        {
            var root = MathF.Sqrt(discriminant);
            var near = origin + ray * (-along - root);
            var far = origin + ray * (-along + root);
            point = Vector3.DistanceSquared(near, preferred * radius) <= Vector3.DistanceSquared(far, preferred * radius)
                ? near : far;
        }
        else point = origin + ray * MathF.Max(0, -along);
        if (!Finite(point) || point.LengthSquared() <= 1e-8f) return false;
        direction = Vector3.Normalize(point);
        return true;
    }

    private void Basis(out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        origin = new(MathF.Sin(yaw) * MathF.Cos(pitch) * distance, MathF.Sin(pitch) * distance,
            MathF.Cos(yaw) * MathF.Cos(pitch) * distance);
        forward = Vector3.Normalize(-origin);
        right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        up = Vector3.Normalize(Vector3.Cross(right, forward));
    }

    private static float Focal(Vector2 size) => MathF.Min(size.X, size.Y) * .85f;
    private static bool Valid(Vector2 size) => float.IsFinite(size.X) && float.IsFinite(size.Y) && size.X > 0 && size.Y > 0;
    private static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
