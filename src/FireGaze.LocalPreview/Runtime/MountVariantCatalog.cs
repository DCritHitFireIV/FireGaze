using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace FireGaze.LocalPreview.Runtime;

/// <summary>A proposed merge rejected because the native bar must also retain its flight control.</summary>
public sealed record MountVariantOverflow(ushort RepresentativeId, IReadOnlyList<ushort> SourceIds, int NativeActionCount);

/// <summary>
/// Keeps content identities intact while sharing one visible representative and its local action sources.
/// ModelChara row numbers and sound banks are not visual identity; unresolved presentation fields are.
/// </summary>
public sealed class MountVariantCatalog
{
    private readonly Dictionary<uint, uint> representatives;
    private readonly Dictionary<ushort, IReadOnlyList<ushort>> actionSources;
    private readonly Dictionary<uint, bool> admittedSpecialActions;
    public IReadOnlyList<MountVariantOverflow> OverflowGroups { get; }

    public MountVariantCatalog(ExcelModule excel) : this(excel.GetSheet<Mount>()) { }

    public MountVariantCatalog(IEnumerable<Mount> mounts)
    {
        var rows = mounts.Where(row => row.RowId is > 0 and <= ushort.MaxValue && row.ModelChara.RowId > 0)
            .GroupBy(row => row.RowId).Select(group => group.First()).ToDictionary(row => row.RowId);
        admittedSpecialActions = rows.Values.ToDictionary(row => row.RowId,
            row => NativeVisuals(row).Distinct(StringComparer.Ordinal).Any());
        representatives = rows.Keys.ToDictionary(id => id, id => id);
        foreach (var group in rows.Values.Where(row => row.RowId is not (433 or 439)
                     && row.ModelChara.IsValid && row.MountCustomize.IsValid).GroupBy(row => Appearance(row)))
        {
            var chosen = group.OrderBy(row => row.Singular.IsEmpty || row.Icon == 0)
                .ThenBy(row => row.Singular.IsEmpty).ThenBy(row => row.Icon == 0).ThenBy(row => row.RowId).First().RowId;
            foreach (var row in group) representatives[row.RowId] = chosen;
        }

        // These reviewed pairs share actual resources and rider fit. Their sole
        // presentation difference is Unknown4, plus the separately excluded sound
        // bank. Limit this exception to the reviewed pairs and recheck all other
        // fields so a later client update cannot merge changed appearances.
        MergeReviewed(368, 364, allowUnknown4: true);
        MergeReviewed(404, 445, allowUnknown4: true);
        MergeReviewed(423, 415, allowUnknown4: true);
        MergeReviewed(440, 441, allowUnknown4: false);

        void MergeReviewed(uint source, uint target, bool allowUnknown4)
        {
            if (!rows.TryGetValue(source, out var from) || !rows.TryGetValue(target, out var to)
                || !from.ModelChara.IsValid || !to.ModelChara.IsValid
                || !from.MountCustomize.IsValid || !to.MountCustomize.IsValid) return;
            if (Equals(Appearance(from, allowUnknown4), Appearance(to, allowUnknown4))) MergeInto(source, target);
        }

        var overflow = new List<MountVariantOverflow>();
        foreach (var group in representatives.GroupBy(pair => pair.Value).ToArray())
        {
            var ids = group.Select(pair => (ushort)pair.Key).Order().ToArray();
            if (ids.Length < 2) continue;
            var count = ids.SelectMany(id => NativeVisuals(rows[id])).Distinct(StringComparer.Ordinal).Count();
            if (count <= 5) continue;
            overflow.Add(new((ushort)group.Key, Array.AsReadOnly(ids), count));
            // Retain every donor as an independent selectable mount instead of
            // hiding actions that cannot fit alongside the flight control.
            foreach (var id in ids) representatives[id] = id;
        }
        OverflowGroups = overflow.AsReadOnly();
        actionSources = representatives.GroupBy(pair => pair.Value).ToDictionary(group => (ushort)group.Key,
            group => (IReadOnlyList<ushort>)Array.AsReadOnly(group.Select(pair => (ushort)pair.Key)
                .OrderBy(id => id != group.Key).ThenBy(id => id).ToArray()));
    }

    public uint GetRepresentative(uint id) => representatives.GetValueOrDefault(id, id);

    /// <summary>Whether the row contributes an actual local mount special action.</summary>
    public bool HasAdmittedSpecialAction(uint id) => admittedSpecialActions.GetValueOrDefault(id);

    public IReadOnlyList<ushort> GetActionSources(ushort representativeId)
        => actionSources.TryGetValue(representativeId, out var sources)
            ? sources : Array.AsReadOnly(new[] { representativeId });

    private void MergeInto(uint source, uint target)
    {
        if (!representatives.TryGetValue(source, out var sourceRepresentative) || !representatives.ContainsKey(target)) return;
        var targetRepresentative = representatives[target];
        foreach (var id in representatives.Where(pair => pair.Value == sourceRepresentative || pair.Value == targetRepresentative)
                     .Select(pair => pair.Key).ToArray())
            representatives[id] = target;
    }

    private static IEnumerable<string> NativeVisuals(Mount row)
    {
        if (row.MountAction.RowId == 0 || !row.MountAction.IsValid) yield break;
        var actions = row.MountAction.Value.Action;
        // Match the existing local action resolver's six physical source slots.
        for (var i = 0; i < Math.Min(6, actions.Count); i++)
        {
            var action = actions[i];
            if (action.RowId == 0 || !action.IsValid) continue;
            var timeline = action.Value.AnimationEnd;
            if (timeline.RowId is 0 or > ushort.MaxValue || !timeline.IsValid) continue;
            var key = timeline.Value.Key.ExtractText();
            if (key.StartsWith("mount_sp/", StringComparison.Ordinal)) yield return key;
        }
    }

    private static object Appearance(Mount row, bool ignoreUnknown4 = false)
    {
        var model = row.ModelChara.Value;
        var customize = row.MountCustomize.Value;
        // Compare every current CN visual/presentation field, including unknowns.
        // RowId/RowOffset locate the data and SEPack selects sound, so those alone
        // do not create another visible mount. Equipment and rider fit still do.
        return (
            Model: (model.Type, model.Model, model.Base, model.Variant, model.AnimationVariant, model.PapVariation,
                model.Unknown_70, model.Unknown0, model.Unknown1, model.Unknown3, model.Unknown4, model.Unknown5,
                model.Unknown6, model.Unknown7, model.Unknown8, model.Unknown9, model.Unknown10, model.Unknown11,
                model.Unknown12, model.Unknown13, model.Unknown14, model.Unknown15),
            Equipment: (row.EquipHead, row.EquipBody, row.EquipLeg, row.EquipFoot),
            Customize: (customize.AuRaFemaleCameraHeight, customize.AuRaFemaleScale, customize.AuRaMaleCameraHeight, customize.AuRaMaleScale,
                customize.ElezenFemaleCameraHeight, customize.ElezenFemaleScale, customize.ElezenMaleCameraHeight, customize.ElezenMaleScale,
                customize.HrothgarFemaleCameraHeight, customize.HrothgarFemaleScale, customize.HrothgarMaleCameraHeight, customize.HrothgarMaleScale,
                customize.HyurHighlanderFemaleCameraHeight, customize.HyurHighlanderFemaleScale, customize.HyurHighlanderMaleCameraHeight, customize.HyurHighlanderMaleScale,
                customize.HyurMidlanderFemaleCameraHeight, customize.HyurMidlanderFemaleScale, customize.HyurMidlanderMaleCameraHeight, customize.HyurMidlanderMaleScale,
                customize.LalaFemaleCameraHeight, customize.LalaFemaleScale, customize.LalaMaleCameraHeight, customize.LalaMaleScale,
                customize.MiqoFemaleCameraHeight, customize.MiqoFemaleScale, customize.MiqoMaleCameraHeight, customize.MiqoMaleScale,
                customize.RoeFemaleCameraHeight, customize.RoeFemaleScale, customize.RoeMaleCameraHeight, customize.RoeMaleScale,
                customize.VieraFemaleCameraHeight, customize.VieraFemaleScale, customize.VieraMaleCameraHeight, customize.VieraMaleScale,
                customize.Unknown_70_1, customize.Unknown_70_2, customize.Unknown0, customize.Unknown1, customize.Unknown2,
                customize.Unknown3, !ignoreUnknown4 && customize.Unknown4));
    }
}
