namespace FireGaze.LocalPreview.Interaction;

public enum TrialCatalogAction { Mounts, Pets, Accessories, Close, Owned, Summoned, Smaller, Larger, Regular, Special }

/// <summary>One immutable presentation shared by the actual buttons and development commands.</summary>
public sealed record TrialCatalogControls
{
    public CollectionPanelState Panels { get; init; } = new(false, false);
    public float IconSize { get; init; } = 56f;
    public MinionTrialView PetView { get; init; } = MinionTrialView.Owned;
    public MountCatalogView MountView { get; init; } = MountCatalogView.Regular;

    public TrialCatalogControls Apply(TrialCatalogAction action) => action switch
    {
        TrialCatalogAction.Mounts => this with { Panels = Panels.ToggleMount() },
        TrialCatalogAction.Pets => this with { Panels = Panels.TogglePet() },
        TrialCatalogAction.Accessories => this with { Panels = Panels.ToggleAccessory() },
        TrialCatalogAction.Close => this with { Panels = new(false, false) },
        TrialCatalogAction.Owned => this with { PetView = MinionTrialView.Owned },
        TrialCatalogAction.Summoned => this with { PetView = MinionTrialView.Summoned },
        TrialCatalogAction.Regular => this with { MountView = MountCatalogView.Regular },
        TrialCatalogAction.Special => this with { MountView = MountCatalogView.Special },
        TrialCatalogAction.Smaller => this with { IconSize = MathF.Max(40, IconSize - 8) },
        TrialCatalogAction.Larger => this with { IconSize = MathF.Min(112, IconSize + 8) },
        _ => this,
    };

    public static TrialCatalogControls Update(ref TrialCatalogControls current, TrialCatalogAction action)
    {
        while (true)
        {
            var before = Volatile.Read(ref current);
            var after = before.Apply(action);
            if (ReferenceEquals(Interlocked.CompareExchange(ref current, after, before), before)) return after;
        }
    }

    public static bool TryParse(string[]? parts, out TrialCatalogAction action)
    {
        action = default;
        if (parts is not { Length: 2 } || !string.Equals(parts[0], "catalog", StringComparison.OrdinalIgnoreCase))
            return false;
        var parsed = parts[1]?.ToLowerInvariant() switch
        {
            "mounts" => TrialCatalogAction.Mounts,
            "pets" => TrialCatalogAction.Pets,
            "accessories" or "accessory" => TrialCatalogAction.Accessories,
            "close" => TrialCatalogAction.Close,
            "owned" => TrialCatalogAction.Owned,
            "summoned" => TrialCatalogAction.Summoned,
            "regular" => TrialCatalogAction.Regular,
            "special" => TrialCatalogAction.Special,
            "smaller" => TrialCatalogAction.Smaller,
            "larger" => TrialCatalogAction.Larger,
            _ => (TrialCatalogAction?)null,
        };
        if (parsed is null) return false;
        action = parsed.Value;
        return true;
    }
}
