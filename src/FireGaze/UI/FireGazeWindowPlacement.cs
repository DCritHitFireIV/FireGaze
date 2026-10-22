using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>Places a newly appearing child beside its actual owner, without moving an open window.</summary>
internal static class FireGazeWindowPlacement
{
    private sealed record RememberedWindow(NearbyWindowBounds Bounds, uint ViewportID, int DrawFrame);
    private static readonly Dictionary<string, RememberedWindow> Windows = new(StringComparer.Ordinal);
    private static uint preparedViewportID;

    internal static void Remember(string id) => Windows[id] = new(
        new(ImGui.GetWindowPos(), ImGui.GetWindowSize()), ImGui.GetWindowViewport().ID, ImGui.GetFrameCount());

    internal static bool Begin(string title, ref bool open, string id)
    {
        var visible = ImGui.Begin(title, ref open);
        Remember(id); // A collapsed window remains open and may be moved between viewports.
        return visible;
    }

    internal static NearbyWindowBounds Prepare(string id, Vector2 preferredSize, float scale, string? ownerID = null,
        NearbyWindowBounds? anchor = null)
    {
        var viewport = ImGui.GetMainViewport();
        var remembered = Windows.TryGetValue(id, out var previous);
        var continuous = remembered && (previous!.DrawFrame == ImGui.GetFrameCount() - 1 || previous.DrawFrame == ImGui.GetFrameCount());
        if (anchor is null)
        {
            var candidate = ownerID is not null && Windows.TryGetValue(ownerID, out var source)
                ? ImGui.FindViewportByID(source.ViewportID) : ImGui.GetWindowViewport();
            if (!candidate.IsNull) viewport = candidate;
        }
        if (continuous)
        {
            var actual = ImGui.FindViewportByID(previous!.ViewportID);
            if (!actual.IsNull) viewport = actual;
        }
        var owner = anchor ?? (ownerID is null
            ? new NearbyWindowBounds(ImGui.GetWindowPos(), ImGui.GetWindowSize())
            : Windows.TryGetValue(ownerID, out var known) ? known.Bounds
            : new NearbyWindowBounds(viewport.WorkPos + viewport.WorkSize * .5f, Vector2.One));
        var desired = remembered ? previous!.Bounds.Size : preferredSize;
        var placement = NearbyWindowLayout.Place(viewport.WorkPos, viewport.WorkSize,
            owner.Position, owner.Size, desired, scale);
        preparedViewportID = viewport.ID;
        if (!continuous) ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowPos(placement.Position, ImGuiCond.Appearing);
        ImGui.SetNextWindowSize(placement.Size, ImGuiCond.Appearing);
        return placement;
    }

    internal static void PrepareModal(Vector2 preferredSize)
    {
        var viewport = ImGui.GetWindowViewport();
        if (viewport.IsNull) viewport = ImGui.GetMainViewport();
        var scale = ImGui.GetFontSize() / 17f;
        var owner = new NearbyWindowBounds(ImGui.GetWindowPos(), ImGui.GetWindowSize());
        var desired = Vector2.Min(preferredSize, Vector2.Max(Vector2.One, viewport.WorkSize - new Vector2(24 * scale)));
        var center = owner.Position + owner.Size * .5f;
        var half = desired * .5f;
        var lower = viewport.WorkPos + half;
        var upper = Vector2.Max(lower, viewport.WorkPos + viewport.WorkSize - half);
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowPos(Vector2.Clamp(center, lower, upper), ImGuiCond.Appearing, new(.5f));
    }

    internal static Vector2 PreparedWorkSize
    {
        get
        {
            var viewport = ImGui.FindViewportByID(preparedViewportID);
            return viewport.IsNull ? ImGui.GetMainViewport().WorkSize : viewport.WorkSize;
        }
    }

    internal static void Constrain(Vector2 minimum, Vector2 maximum, float scale)
    {
        var cap = Vector2.Max(Vector2.One, PreparedWorkSize - new Vector2(24 * scale));
        var upper = Vector2.Min(cap, maximum);
        ImGui.SetNextWindowSizeConstraints(Vector2.Min(minimum, upper), upper);
    }
}
