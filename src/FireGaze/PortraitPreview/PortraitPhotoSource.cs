using System.Text.Json.Serialization;

namespace FireGaze.PortraitPreview;

internal enum PortraitImageScope { Portrait, VisibleAdventurerPlate }

/// <summary>
/// Identifies a logical photo source. A null descriptor denotes legacy, unknown
/// provenance; an unready plate descriptor can retain parameters without a photo.
/// </summary>
internal readonly record struct PortraitPhotoSource(
    [property: JsonRequired] PortraitImageScope Scope, string? VisualKey = null)
{
    [JsonRequired]
    public int Version { get; init; } = 1;

    public static PortraitPhotoSource Portrait => new(PortraitImageScope.Portrait);

    [JsonIgnore]
    public bool IsReady => IsValid && (Scope == PortraitImageScope.Portrait || VisualKey is not null);

    // Capability of the legacy portrait thumbnail adapter only. A Plate visual
    // key remains descriptive; the installed source-aware provider separately
    // qualifies the current visible card before authorizing its own pixels.
    [JsonIgnore]
    public bool CanCapture => IsValid && Scope == PortraitImageScope.Portrait;

    [JsonIgnore]
    public bool IsValid => Version == 1 && (Scope switch
    {
        PortraitImageScope.Portrait => VisualKey is null,
        PortraitImageScope.VisibleAdventurerPlate => VisualKey is null
            || VisualKey.Length == 64 && VisualKey.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F'),
        _ => false,
    });

    [JsonIgnore]
    public string Canonical => Scope == PortraitImageScope.Portrait
        ? "portrait"
        : "plate:" + (VisualKey ?? "unknown");
}
