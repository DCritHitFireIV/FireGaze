namespace FireGaze.PortraitPreview;

/// <summary>One shareable line in the portrait design list.</summary>
internal enum PortraitDesignEntryKind { Preview, Equipped, Appearance }

/// <summary>Portable display data only; no native pointers or mutable game state.</summary>
internal sealed record PortraitDesignEntry(
    string Key,
    string Name,
    string KindLabel,
    int Value,
    string Source,
    uint Icon,
    PortraitDesignEntryKind Kind = PortraitDesignEntryKind.Preview,
    byte Stain0 = 0,
    byte Stain1 = 0,
    uint? Color = null);

internal readonly record struct PortraitDesignEquippedPiece(PortraitSlot Slot, uint ItemID, byte Stain0, byte Stain1);

internal static class PortraitDesignEntrySlotMap
{
    public static IReadOnlyList<PortraitDesignEquippedPiece> ReadEquipped(IEnumerable<OwnedEquipmentEntry> entries, bool completeRead)
    {
        if (!completeRead) return [];
        var pieces = new Dictionary<PortraitSlot, PortraitDesignEquippedPiece>();
        foreach (var slot in Enum.GetValues<PortraitSlot>().Where(slot => slot != PortraitSlot.Facewear))
            pieces[slot] = new(slot, 0, 0, 0);
        foreach (var entry in entries)
        foreach (var variant in entry.Variants)
        {
            if (variant.Source.Kind != OwnedSourceKind.Equipped || variant.Status != OwnedReadStatus.Current
                || variant.RawItemID == 0 || !TryMap(variant.Slot, out var slot)) continue;
            var itemID = variant.GlamourID != 0 ? variant.GlamourID : entry.ItemID;
            pieces[slot] = new(slot, itemID, variant.Stain1, variant.Stain2);
        }
        return Array.AsReadOnly(pieces.Values.OrderBy(piece => piece.Slot).ToArray());
    }

    public static bool IsRendered(PortraitLook look, PortraitSlot slot) => slot switch
    {
        PortraitSlot.Head => look.HeadVisible,
        PortraitSlot.MainHand or PortraitSlot.OffHand => look.WeaponVisible,
        _ => true,
    };

    public static bool TryMap(int nativeSlot, out PortraitSlot slot)
    {
        slot = nativeSlot switch
        {
            0 => PortraitSlot.MainHand, 1 => PortraitSlot.OffHand, 2 => PortraitSlot.Head,
            3 => PortraitSlot.Body, 4 => PortraitSlot.Hands, 6 => PortraitSlot.Legs,
            7 => PortraitSlot.Feet, 8 => PortraitSlot.Ears, 9 => PortraitSlot.Neck,
            10 => PortraitSlot.Wrists, 11 => PortraitSlot.RightRing, 12 => PortraitSlot.LeftRing,
            _ => default,
        };
        return nativeSlot is 0 or 1 or 2 or 3 or 4 or 6 or 7 or 8 or 9 or 10 or 11 or 12;
    }
}

/// <summary>
/// Immutable manifest used by the design list. Hiding a line only changes this view, so
/// sharing a list never changes the current preview or the saved portrait.
/// </summary>
internal sealed record PortraitDesignManifest(IReadOnlyList<PortraitDesignEntry> Items)
{
    public IReadOnlyList<OwnedSourceView> Sources { get; init; } = [];

    public static PortraitDesignManifest Build(IEnumerable<PortraitDesignEntry> preview,
        IEnumerable<PortraitDesignEntry> equipped, IEnumerable<PortraitDesignEntry> appearance,
        IReadOnlyList<OwnedSourceView>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(equipped);
        ArgumentNullException.ThrowIfNull(appearance);
        var rows = preview.Concat(equipped).Concat(appearance)
            .Where(row => !string.IsNullOrWhiteSpace(row.Key) && !string.IsNullOrWhiteSpace(row.Name))
            .Select(row => row with { Key = row.Key.Trim(), Name = row.Name.Trim(), Source = row.Source?.Trim() ?? "" })
            .ToArray();
        return new(Array.AsReadOnly(rows)) { Sources = sources is null ? [] : Array.AsReadOnly(sources.ToArray()) };
    }

    public PortraitDesignManifest Hide(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return this;
        var hidden = key.Trim();
        return this with { Items = Array.AsReadOnly(Items.Where(row => !string.Equals(row.Key, hidden, StringComparison.Ordinal)).ToArray()) };
    }

    public PortraitDesignManifest Differences()
        => this with { Items = Array.AsReadOnly(Items.Where(IsDifference).ToArray()) };

    public PortraitDesignManifest ForShowcase()
        => this with { Items = Array.AsReadOnly(Items.Where(row => row.Kind == PortraitDesignEntryKind.Preview && row.Value != 0
            || row.Kind == PortraitDesignEntryKind.Appearance
                && row.Key.StartsWith("appearance:preview:", StringComparison.Ordinal)).ToArray()) };

    public static IEnumerable<PortraitDesignEntry> AppearanceEntries(
        IReadOnlyList<PortraitAppearanceGroup> groups, string prefix, string kindLabel, string sourcePrefix)
    {
        foreach (var group in groups)
        {
            var option = group.Options.FirstOrDefault(value => value.Value == group.CurrentValue);
            var name = string.IsNullOrWhiteSpace(option?.UnlockItemName) ? option?.Label : option.UnlockItemName;
            var icon = option?.Icon is > 0 ? option.Icon : option?.UnlockItemIcon ?? 0;
            yield return new($"appearance:{prefix}:{group.ID}", $"{group.Label} · {name ?? group.CurrentValue.ToString()}",
                kindLabel, group.CurrentValue, sourcePrefix + " · " + group.Label, icon,
                PortraitDesignEntryKind.Appearance, Color: option?.Color);
        }
    }

    private bool IsDifference(PortraitDesignEntry row)
    {
        PortraitDesignEntry? counterpart;
        if (row.Kind == PortraitDesignEntryKind.Appearance)
        {
            var tokens = row.Key.Split(':', 3);
            if (tokens.Length != 3) return false;
            var opposite = tokens[1] == "preview" ? "actual" : "preview";
            counterpart = Items.FirstOrDefault(other => other.Kind == PortraitDesignEntryKind.Appearance
                && other.Key == $"appearance:{opposite}:{tokens[2]}");
        }
        else
        {
            var slot = row.Key[(row.Key.IndexOf(':') + 1)..];
            var opposite = row.Kind == PortraitDesignEntryKind.Preview ? PortraitDesignEntryKind.Equipped : PortraitDesignEntryKind.Preview;
            counterpart = Items.FirstOrDefault(other => other.Kind == opposite
                && other.Key[(other.Key.IndexOf(':') + 1)..] == slot);
        }
        return counterpart is not null && (counterpart.Value != row.Value
            || counterpart.Stain0 != row.Stain0 || counterpart.Stain1 != row.Stain1);
    }
}
