namespace FireGaze.LocalPreview;

public sealed partial class LocalPreviewFeature
{
    private bool TryDevelopmentIdleFollowCommand(string[] parts)
    {
        if (parts.Length < 2 || parts[1] != "idlefollow") return false;
        if (pluginInterface?.IsDev != true) return true;
        if (parts is [_, _, "status"])
        {
            Log.Debug("[LocalPreview] Development idle-follow: {Snapshot}", companions?.ReadIdleFollowJSON().GetRawText() ?? "unavailable");
            return true;
        }
        bool enabled;
        uint? selected = null;
        if (parts is [_, _, "off"]) enabled = false;
        else if (parts is [_, _, "on"]) enabled = true;
        else if (parts is [_, _, "on", var id] && uint.TryParse(id, out var parsed) && parsed > 0)
        { enabled = true; selected = parsed; }
        else return true;
        if (companions is not null && !companions.SetDevelopmentIdleFollow(enabled, selected, out var reason))
            Log.Debug("[LocalPreview] Development idle-follow refused: {Reason}", reason);
        return true;
    }
}
