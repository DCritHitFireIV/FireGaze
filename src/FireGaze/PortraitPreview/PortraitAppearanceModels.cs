namespace FireGaze.PortraitPreview;

internal sealed record PortraitAppearanceOption(int Value, string Label, uint Icon = 0,
    uint? Color = null, bool Available = true, string? Reason = null);

internal sealed record PortraitAppearanceGroup(string ID, string Label, int CurrentValue,
    IReadOnlyList<PortraitAppearanceOption> Options, string? Description = null);

internal readonly record struct PortraitAppearanceKey(byte Race, byte Tribe, byte Sex, byte Face);

/// <summary>Native byte/bit encoding stays inside the catalogue, never in UI callbacks.</summary>
internal sealed record PortraitAppearanceField(string ID, string Label, byte ByteIndex, byte Mask,
    byte Shift, IReadOnlyList<PortraitAppearanceOption> Options, string? Description = null);

internal sealed record PortraitAppearanceProfile(PortraitAppearanceKey Key,
    IReadOnlyList<PortraitAppearanceField> Fields);
