using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Detached equality tokens and the exact expected snapshot for one narrow RGB write.
/// Identity addresses are opaque tokens here; this pure model never dereferences them.
/// SurfaceGenerationKey must describe the retained texture generation and its crop/UV.
/// </summary>
internal sealed class PortraitLightingFitBinding
{
    private readonly PortraitEditorSnapshot snapshot;

    private PortraitLightingFitBinding(PortraitIdentity identity, PortraitHistoryKey historyKey,
        PortraitEditorSnapshot snapshot, string surfaceGenerationKey, PortraitLightingFitState state)
    {
        Identity = identity;
        HistoryKey = historyKey;
        this.snapshot = snapshot;
        SurfaceGenerationKey = surfaceGenerationKey;
        State = state;
    }

    public PortraitIdentity Identity { get; }
    public PortraitHistoryKey HistoryKey { get; }
    public string SurfaceGenerationKey { get; }
    public PortraitLightingFitState State { get; }
    public PortraitEditorSnapshot Snapshot => snapshot.Copy();

    public static bool TryCreate(PortraitIdentity identity, PortraitHistoryKey historyKey,
        PortraitEditorSnapshot snapshot, string surfaceGenerationKey, int width, int height,
        IReadOnlyList<PortraitNativeLightRange> ranges, out PortraitLightingFitBinding? binding)
    {
        binding = null;
        if (!identity.IsValid || identity.Generation == 0 || !historyKey.IsValid || snapshot?.IsValid != true
            || string.IsNullOrWhiteSpace(surfaceGenerationKey) || width <= 0 || height <= 0
            || (long)width * height > 1_048_576 || ranges is not { Count: 6 }) return false;

        // Arrays supplied by a native adapter or history observer cannot remain shared
        // with an asynchronous sampling transaction.
        var captured = snapshot.Copy();
        var composition = captured.Composition;
        int[] parameters = [composition.DirectionalRed, composition.DirectionalGreen, composition.DirectionalBlue,
            composition.AmbientRed, composition.AmbientGreen, composition.AmbientBlue];
        var limits = ranges.ToArray();
        if (Enumerable.Range(0, 6).Any(axis => !limits[axis].Contains(parameters[axis]))) return false;

        var owner = Hash("portrait-fit-owner-v1:", new
        {
            Editor = identity.Editor.ToInt64(), State = identity.State.ToInt64(), Model = identity.Model.ToInt64(),
            identity.Generation, identity.Character,
            historyKey.ContentID, historyKey.EntryPoint, historyKey.SourceID, historyKey.CompatToken,
        });
        // Preserve the complete snapshot, including live animation time. History's
        // playback deduplication deliberately is not a sampling-state comparison.
        var fixedSnapshot = captured with
        {
            Composition = composition with
            {
                DirectionalRed = 0, DirectionalGreen = 0, DirectionalBlue = 0,
                AmbientRed = 0, AmbientGreen = 0, AmbientBlue = 0,
            },
        };
        var fixedState = Hash("portrait-fit-state-v1:", new
        {
            Owner = owner, Snapshot = fixedSnapshot, SurfaceGenerationKey = surfaceGenerationKey,
            Width = width, Height = height, Ranges = limits,
        });
        var state = new PortraitLightingFitState(owner, fixedState, width, height,
            Array.AsReadOnly(parameters), Array.AsReadOnly(limits));
        binding = new(identity, historyKey, captured, surfaceGenerationKey, state);
        return true;
    }

    private static string Hash<T>(string prefix, T value)
        => prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
}
