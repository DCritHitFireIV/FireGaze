using System.Security.Cryptography;
using System.Text;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Detached source for one spatial gesture. Native addresses are opaque equality tokens;
/// appearance arrays are owned copies and are never shared with the drawing thread.
/// </summary>
internal sealed class PortraitLightingSpaceBinding
{
    private readonly PortraitEditorSnapshot snapshot;

    private PortraitLightingSpaceBinding(PortraitIdentity identity, PortraitHistoryKey historyKey,
        PortraitEditorSnapshot snapshot)
    {
        Identity = identity;
        HistoryKey = historyKey;
        this.snapshot = snapshot.Copy();
        StateKey = KeyFor(this.snapshot);
    }

    public PortraitIdentity Identity { get; }
    public PortraitHistoryKey HistoryKey { get; }
    public string StateKey { get; }
    public PortraitEditorSnapshot Snapshot => snapshot.Copy();
    public PortraitComposition Composition => snapshot.Composition;

    public static bool TryCreate(PortraitIdentity identity, PortraitHistoryKey historyKey,
        PortraitEditorSnapshot snapshot, out PortraitLightingSpaceBinding? binding)
    {
        binding = null;
        if (!identity.IsValid || identity.Generation == 0 || !historyKey.IsValid || snapshot?.IsValid != true) return false;
        binding = new(identity, historyKey, snapshot);
        return true;
    }

    public bool Matches(PortraitIdentity identity, PortraitHistoryKey historyKey, PortraitEditorSnapshot current)
        => identity == Identity && historyKey == HistoryKey && MatchesSnapshot(snapshot, current);

    public bool TryPrepare(PortraitIdentity identity, PortraitHistoryKey historyKey, PortraitEditorSnapshot current,
        PortraitComposition desired, out PortraitComposition merged)
    {
        merged = null!;
        if (!Matches(identity, historyKey, current) || !IsValidDesired(desired)) return false;
        merged = MergeInto(current.Composition, desired);
        return true;
    }

    /// <summary>Playback time alone may advance on the same running timeline; every other value is guarded.</summary>
    public static bool MatchesSnapshot(PortraitEditorSnapshot expected, PortraitEditorSnapshot current)
        => expected?.IsValid == true && current?.IsValid == true && KeyFor(expected) == KeyFor(current);

    /// <summary>Only values owned by the spatial interaction are copied; all other live fields survive.</summary>
    public static PortraitComposition MergeInto(PortraitComposition current, PortraitComposition desired)
        => current with
        {
            CameraPosition = desired.CameraPosition,
            CameraYaw = desired.CameraYaw,
            CameraPitch = desired.CameraPitch,
            CameraDistance = desired.CameraDistance,
            DirectionalVerticalAngle = desired.DirectionalVerticalAngle,
            DirectionalHorizontalAngle = desired.DirectionalHorizontalAngle,
        };

    public static bool IsValidDesired(PortraitComposition desired)
        => desired?.IsValid == true && desired.DirectionalVerticalAngle is >= -180 and <= 180
            && desired.DirectionalHorizontalAngle is >= -180 and <= 180;

    private static string KeyFor(PortraitEditorSnapshot value)
    {
        var state = value.Composition.Paused ? value : value with
        {
            Composition = value.Composition with { AnimationProgress = 0 },
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToJSON())));
    }
}
