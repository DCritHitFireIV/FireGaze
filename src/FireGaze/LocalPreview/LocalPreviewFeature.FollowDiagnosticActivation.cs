using System.Globalization;
using FireGaze.LocalPreview.Core;

namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    // OnCommand dispatches this consumer to Framework before parsing/resolution.
    private bool TryDevelopmentFollowLeaseDiagnosticsCommand(string[] parts)
    {
        if (parts.Length < 3 || parts[1] != "follow" || parts[2] != "leases") return false;
        if (pluginInterface?.IsDev != true || companions is null || parts.Length is < 5 or > 12) return true;
        var context = parts[3].Split(':');
        if (context.Length != 3
            || !long.TryParse(context[0], NumberStyles.None, CultureInfo.InvariantCulture, out var scene) || scene <= 0
            || !ulong.TryParse(context[1], NumberStyles.None, CultureInfo.InvariantCulture, out var source) || source == 0
            || !uint.TryParse(context[2], NumberStyles.None, CultureInfo.InvariantCulture, out var generation) || generation == 0)
            return true;
        var identities = new (long LeaseId, long Generation)[parts.Length - 4];
        for (var i = 0; i < identities.Length; i++)
        {
            var pair = parts[i + 4].Split(':');
            if (pair.Length != 2
                || !long.TryParse(pair[0], NumberStyles.None, CultureInfo.InvariantCulture, out var lease) || lease <= 0
                || !long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out var leaseGeneration) || leaseGeneration <= 0)
                return true;
            identities[i] = (lease, leaseGeneration);
        }
        if (!companions.SetFollowDiagnosticsForLeaseSelection(new(scene, source, generation), identities, out var reason))
            Log.Debug("[LocalPreview] Exact follow diagnostics unavailable: {Reason}", reason);
        return true;
    }
}
