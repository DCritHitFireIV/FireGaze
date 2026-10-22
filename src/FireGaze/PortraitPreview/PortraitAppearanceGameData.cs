using Lumina;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System.Buffers.Binary;

namespace FireGaze.PortraitPreview;

internal sealed partial class PortraitAppearanceCatalog
{
    public PortraitAppearanceCatalog(GameData data) : this(ReadProfiles(data)) { }

    /// <summary>All definitions come from this client's native beautician tables; no plugin is required.</summary>
    private static IReadOnlyList<PortraitAppearanceProfile> ReadProfiles(GameData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var hairSheet = data.GetExcelSheet<HairMakeType>()!;
        var hairProfiles = hairSheet.ToDictionary(row => (row.Race.RowId, row.Tribe.RowId, row.Gender));
        var rawHairSheet = data.GetExcelSheet<RawRow>(name: "HairMakeType")!;
        var makeSheet = data.GetExcelSheet<CharaMakeType>()!;
        var customizeSheet = data.GetExcelSheet<CharaMakeCustomize>()!;
        var cmp = data.GetFile("chara/xls/charamake/human.cmp")?.Data;
        // The schema includes 2 global tables, 32 clan/sex tables, and the racial scale data.
        // A changed schema only removes color swatches; it never invents color bytes.
        if (cmp?.Length != 186752) cmp = null;
        var result = new List<PortraitAppearanceProfile>();
        foreach (var make in makeSheet)
        {
            if (!hairProfiles.TryGetValue((make.Race.RowId, make.Tribe.RowId, make.Gender), out var hair) ||
                !rawHairSheet.TryGetRow(hair.RowId, out var rawHair)) continue;
            var faceMenus = make.CharaMakeStruct.Where(menu => menu.Customize == 5).Take(1).ToArray();
            if (faceMenus.Length == 0) continue;
            var faceMenu = faceMenus[0];
            if (faceMenu.SubMenuNum is < 1 or > 8) continue;
            for (var faceIndex = 0; faceIndex < faceMenu.SubMenuNum; faceIndex++)
            {
                // Hrothgar faces are stored as 5..8 in CustomizeData, with four native face menus.
                var face = (byte)(make.Race.RowId == 7 ? faceIndex + 5 : faceIndex + 1);
                var fields = new List<PortraitAppearanceField>();
                for (var menuIndex = 0; menuIndex < hair.CharaMakeStruct.Count; menuIndex++)
                {
                    var menu = hair.CharaMakeStruct[menuIndex];
                    if (menu.Menu.RowId == 0 || menu.SubMenuNum == 0) continue;
                    var label = menu.Menu.Value.Text.ToString();
                    switch (menu.Customize)
                    {
                        case 6:
                        {
                            var options = IconOptions(menu, menuIndex, "发型", true, face);
                            Add("hair", label, 6, options);
                            AddToggle("highlights", "挑染", 7, 7);
                            Add("highlightsColor", "挑染颜色", 11, Colors(10240, 192));
                            break;
                        }
                        case 10:
                            // clan/sex block: 256 skin RGBA, 256 hair RGBA pairs, 256 skin UI, 256 hair UI.
                            var clanSexIndex = ((int)make.Tribe.RowId - 1) * 2 + make.Gender;
                            Add("hairColor", label, 10, Colors(18432 + clanSexIndex * 5120 + 4096, 192));
                            break;
                        case 14:
                            Add("eyebrows", label, 14, Enumerable.Range(0, menu.SubMenuNum).Select(i => new PortraitAppearanceOption(i, $"{label} {i + 1}")).ToArray());
                            break;
                        case 20 when menu.SubMenuType == 2:
                            Add("lipColor", label, 20, TonedColors(2048, 4096));
                            AddToggle("lipstick", "唇彩", 19, 7);
                            break;
                        case 20 when menu.SubMenuType == 1:
                            Add("furPattern", label, 20, IconOptions(menu, menuIndex, label, false, face));
                            break;
                        case 12 when menu.SubMenuType == 4:
                        {
                            // The native five feature menu addresses bits 0..4, the two ornament menu bits 5..6.
                            // Reordering those two menus (Elezen) does not change their byte mapping.
                            var first = menu.SubMenuNum == 2 ? 5 : 0;
                            if (menu.SubMenuNum is not (2 or 5)) break;
                            var feature = make.FacialFeatureOption[faceIndex];
                            int[] icons = [feature.Option1, feature.Option2, feature.Option3, feature.Option4, feature.Option5, feature.Option6, feature.Option7];
                            for (var i = 0; i < menu.SubMenuNum; i++)
                            {
                                var bit = first + i;
                                if (icons[bit] <= 0) continue;
                                AddToggle($"feature{bit + 1}", $"{label} {i + 1}", 12, (byte)bit, (uint)icons[bit]);
                            }
                            break;
                        }
                        case 13:
                            Add("featureColor", label, 13, Colors(12288, 192));
                            break;
                        case 24:
                            var paints = IconOptions(menu, menuIndex, label, false, face);
                            Add("facePaint", label, 24, paints, 127);
                            AddToggle("facePaintReversed", "反转面妆", 24, 7);
                            break;
                        case 25:
                            Add("facePaintColor", label, 25, TonedColors(2560, 4608));
                            break;
                    }
                }
                // Native character creation supplies both iris color bytes. Neither changes eye shape.
                Add("eyeColorRight", "右眼瞳色", 9, Colors(9216, 192));
                Add("eyeColorLeft", "左眼瞳色", 15, Colors(9216, 192));
                result.Add(new(new((byte)make.Race.RowId, (byte)make.Tribe.RowId, (byte)make.Gender, face), fields));

                void Add(string id, string label, byte index, IReadOnlyList<PortraitAppearanceOption> options, byte mask = 255)
                {
                    if (options.Count > 0) fields.Add(new(id, label, index, mask, 0, options));
                }

                void AddToggle(string id, string label, byte index, byte shift, uint icon = 0) =>
                    fields.Add(new(id, label, index, (byte)(1 << shift), shift, [new(0, "关闭", icon), new(1, "开启", icon)]));

                IReadOnlyList<PortraitAppearanceOption> IconOptions(HairMakeType.CharaMakeStructStruct menu,
                    int menuIndex, string name, bool hairstyle, byte currentFace)
                {
                    // HairMakeType currently stores 106 uint entries. The generated SubMenuParam
                    // stops at 100 and names the final six Unknown0; RawRow retains all entries.
                    if (menu.SubMenuNum > 106 || rawHair.ReadUInt8Column(30 + menuIndex) != menu.SubMenuNum) return [];
                    var options = new List<PortraitAppearanceOption>();
                    for (var i = 0; i < menu.SubMenuNum; i++)
                    {
                        var reference = rawHair.ReadUInt32Column(66 + menuIndex + i * 9);
                        if (i < 100 && reference != menu.SubMenuParam[i]) return [];
                        if (reference == uint.MaxValue) continue;
                        if (customizeSheet.TryGetRow(reference, out var entry))
                        {
                            var available = !hairstyle || entry.Unknown0 == 0 || entry.Unknown0 == currentFace;
                            var reason = available ? entry.UnlockLink == 0 ? null : $"仅本地预览；原生解锁条件：{entry.HintItem.Value.Name}" : "此发型不适用于当前脸型";
                            var text = entry.FeatureID == 0 ? "无" : $"{name} {entry.FeatureID}";
                            var unlockItem = entry.HintItem;
                            options.Add(new(entry.FeatureID, text, entry.Icon, Available: available, Reason: reason,
                                UnlockItemName: unlockItem.RowId == 0 ? null : unlockItem.Value.Name.ToString(),
                                UnlockItemID: unlockItem.RowId, UnlockItemIcon: unlockItem.RowId == 0 ? 0u : (uint)unlockItem.Value.Icon));
                        }
                        else if (menu.Customize == 20)
                        {
                            // Direct icon entries (Hrothgar fur patterns) are one-based native values.
                            var value = i + 1;
                            options.Add(new(value, $"{name} {value}", reference));
                        }
                    }
                    return options.DistinctBy(o => o.Value).OrderBy(o => o.Value).ToArray();
                }
            }
        }
        return result;

        PortraitAppearanceOption[] Colors(int byteOffset, int count) => Enumerable.Range(0, count)
            .Select(i => new PortraitAppearanceOption(i, $"颜色 {i + 1}", Color: ColorAt(byteOffset + i * 4))).ToArray();

        PortraitAppearanceOption[] TonedColors(int darkOffset, int lightOffset) => Enumerable.Range(0, 96)
            .Select(i => new PortraitAppearanceOption(i, $"深色 {i + 1}", Color: ColorAt(darkOffset + i * 4)))
            .Concat(Enumerable.Range(0, 96).Select(i => new PortraitAppearanceOption(128 + i, $"浅色 {i + 1}", Color: ColorAt(lightOffset + i * 4)))).ToArray();

        uint? ColorAt(int byteOffset) => cmp is not null && byteOffset >= 0 && byteOffset + 4 <= cmp.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(cmp.AsSpan(byteOffset, 4)) | 0xff000000u : null;
    }
}
