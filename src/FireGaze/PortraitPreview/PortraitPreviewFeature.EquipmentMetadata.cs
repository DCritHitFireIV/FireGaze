using Lumina.Excel.Sheets;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    public PortraitBrowserItem? GetItem(PortraitSlot slot, uint id)
    {
        if (slot == PortraitSlot.Facewear)
            return glasses.TryGetValue(id, out var face) ? new(id, face.Name.ToString(), (uint)Math.Max(0, face.Icon), 0, true, "") : null;
        // A template or native editor action can publish a new wardrobe between
        // inventory samples. Its display metadata comes from the static item table;
        // ownership still belongs to the separately observed inventory catalog.
        return metadata.TryGetValue(id, out var cached) ? cached
            : equipment.TryGetValue(id, out var row) ? BrowserItem(row) : null;
    }

    private PortraitBrowserItem BrowserItem(Item row)
    {
        // Draw may ask about a newly observed template item. Only read the local
        // customization already published by Framework, never PlayerState here.
        var customize = actualCustomize;
        var restriction = row.EquipRestriction.Value;
        var reason = row.ModelMain == 0 ? "该装备没有可用外观模型" : "";
        if (customize is { Length: >= 2 } && row.EquipRestriction.RowId != 0)
        {
            var raceAllowed = customize[0] switch
            {
                1 => restriction.Hyur, 2 => restriction.Elezen, 3 => restriction.Lalafell, 4 => restriction.Miqote,
                5 => restriction.Roegadyn, 6 => restriction.AuRa, 7 => restriction.Hrothgar, 8 => restriction.Viera, _ => false,
            };
            if (!raceAllowed || (customize[1] == 0 ? !restriction.Male : !restriction.Female)) reason = "该外观不支持当前角色的种族或性别";
        }
        return new(row.RowId, row.Name.ToString(), row.Icon, row.DyeCount, reason.Length == 0, reason,
            Convert.ToUInt32(row.LevelItem.RowId), Convert.ToUInt32(row.LevelEquip));
    }
}
