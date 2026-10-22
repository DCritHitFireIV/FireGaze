namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitAppearanceCatalog
{
    public const int CustomizeLength = 26;
    private readonly Dictionary<PortraitAppearanceKey, PortraitAppearanceField[]> profiles = [];

    public PortraitAppearanceCatalog(IEnumerable<PortraitAppearanceProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        foreach (var profile in profiles)
        {
            if (profile.Key.Race is < 1 or > 8 || profile.Key.Tribe is < 1 or > 16 || profile.Key.Sex > 1 || profile.Key.Face is < 1 or > 8)
                throw new ArgumentException("Invalid appearance profile.", nameof(profiles));
            var fields = new List<PortraitAppearanceField>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var usedBits = new byte[CustomizeLength];
            foreach (var field in profile.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.ID) || string.IsNullOrWhiteSpace(field.Label) || !names.Add(field.ID) ||
                    !Allowed(field) || (usedBits[field.ByteIndex] & field.Mask) != 0 || field.Options.Count == 0)
                    throw new ArgumentException("Invalid or overlapping appearance field.", nameof(profiles));
                var values = new HashSet<int>();
                foreach (var option in field.Options)
                    if (option.Value < 0 || option.Value > (field.Mask >> field.Shift) || !values.Add(option.Value) || string.IsNullOrWhiteSpace(option.Label))
                        throw new ArgumentException("Invalid appearance option.", nameof(profiles));
                usedBits[field.ByteIndex] |= field.Mask;
                fields.Add(field with { Options = Array.AsReadOnly(field.Options.ToArray()) });
            }
            if (!this.profiles.TryAdd(profile.Key, fields.ToArray()))
                throw new ArgumentException("Duplicate appearance profile.", nameof(profiles));
        }
    }

    public IReadOnlyList<PortraitAppearanceGroup> Build(ReadOnlySpan<byte> current)
    {
        if (!TryProfile(current, out var fields)) return [];
        var result = new PortraitAppearanceGroup[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            result[i] = new(field.ID, field.Label, (current[field.ByteIndex] & field.Mask) >> field.Shift,
                field.Options, field.Description);
        }
        return Array.AsReadOnly(result);
    }

    public bool TryChange(ReadOnlySpan<byte> current, string groupID, int value,
        out byte[] changed, out string reason)
    {
        changed = [];
        reason = "当前角色的美容选项尚未读取或不适用于此模型";
        if (!TryProfile(current, out var fields)) return false;
        var field = fields.FirstOrDefault(f => string.Equals(f.ID, groupID, StringComparison.Ordinal));
        if (field is null) { reason = "当前角色不支持此美容选项"; return false; }
        var option = field.Options.FirstOrDefault(o => o.Value == value);
        if (option is null) { reason = "此选项不在当前角色的有效范围内"; return false; }
        if (!option.Available) { reason = option.Reason ?? "此选项无法应用于当前角色"; return false; }
        changed = current.ToArray();
        changed[field.ByteIndex] = (byte)((changed[field.ByteIndex] & ~field.Mask) | ((value << field.Shift) & field.Mask));
        reason = string.Empty;
        return true;
    }

    /// <summary>Reject any non-beautician byte change before a native redraw.</summary>
    public bool IsValidChange(ReadOnlySpan<byte> original, ReadOnlySpan<byte> changed, out string reason)
    {
        reason = "美容数据不完整或角色身份不一致";
        if (original.Length != CustomizeLength || changed.Length != CustomizeLength ||
            !TryProfile(original, out var fields) || Key(original) != Key(changed)) return false;
        var allowed = new byte[CustomizeLength];
        foreach (var field in fields)
        {
            allowed[field.ByteIndex] |= field.Mask;
            if ((original[field.ByteIndex] & field.Mask) == (changed[field.ByteIndex] & field.Mask)) continue;
            var value = (changed[field.ByteIndex] & field.Mask) >> field.Shift;
            var option = field.Options.FirstOrDefault(o => o.Value == value && o.Available);
            if (option is null) { reason = $"{field.Label}不在当前角色的有效范围内"; return false; }
        }
        for (var i = 0; i < CustomizeLength; i++)
            if (((original[i] ^ changed[i]) & ~allowed[i]) != 0)
            { reason = "不能修改当前角色美容师范围外的外观数据"; return false; }
        reason = string.Empty;
        return true;
    }

    private bool TryProfile(ReadOnlySpan<byte> current, out PortraitAppearanceField[] fields)
    {
        fields = [];
        return current.Length == CustomizeLength && profiles.TryGetValue(Key(current), out fields!);
    }

    private static PortraitAppearanceKey Key(ReadOnlySpan<byte> data) => new(data[0], data[4], data[1], data[5]);

    private static bool Allowed(PortraitAppearanceField field) => field.ByteIndex switch
    {
        6 or 9 or 10 or 11 or 13 or 14 or 15 or 20 or 25 => field.Mask == byte.MaxValue && field.Shift == 0,
        7 or 19 => field.Mask == 128 && field.Shift == 7,
        12 => field.Shift < 7 && field.Mask == (1 << field.Shift),
        24 => (field.Mask == 127 && field.Shift == 0) || (field.Mask == 128 && field.Shift == 7),
        _ => false,
    };
}
