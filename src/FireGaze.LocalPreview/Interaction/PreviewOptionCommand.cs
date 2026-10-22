namespace FireGaze.LocalPreview.Interaction;

public enum PreviewOption { Mount, Minion, Accessory, Emote, Items }

/// <summary>A validated preference change; only explicit names and on/off are accepted.</summary>
public readonly record struct PreviewOptionCommand(PreviewOption Option, bool Enabled)
{
    public static bool TryParse(string[]? parts, out PreviewOptionCommand command)
    {
        command = default;
        if (parts is not { Length: 3 } || !string.Equals(parts[0], "option", StringComparison.OrdinalIgnoreCase))
            return false;
        var option = parts[1]?.ToLowerInvariant() switch
        {
            "mount" => PreviewOption.Mount,
            "minion" => PreviewOption.Minion,
            "accessory" => PreviewOption.Accessory,
            "emote" => PreviewOption.Emote,
            "items" => PreviewOption.Items,
            _ => (PreviewOption?)null,
        };
        var enabled = parts[2]?.ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => (bool?)null,
        };
        if (option is null || enabled is null) return false;
        command = new(option.Value, enabled.Value);
        return true;
    }
}
