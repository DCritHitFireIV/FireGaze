using Dalamud.Plugin.Services;
using Lumina;
using Lumina.Excel.Sheets;

namespace FireGaze.PortraitPreview;

internal sealed class PortraitEditorValidation
{
    private readonly Dictionary<uint, PortraitValidationItem> items;
    private readonly HashSet<ushort> timelines;
    private readonly HashSet<byte> expressions;
    private readonly HashSet<ushort> backgrounds;
    private readonly HashSet<ushort> frames;
    private readonly HashSet<ushort> decorations;
    private readonly HashSet<short> classJobs;
    private readonly HashSet<ushort> glasses;
    private readonly HashSet<byte> dyes;

    public PortraitEditorValidation(IDataManager data) : this(data.GameData) { }
    public PortraitEditorValidation(GameData data) : this(ReadData(data)) { }

    public PortraitEditorValidation(PortraitValidationData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        items = data.Items.ToDictionary(item => item.ID);
        timelines = data.Timelines.ToHashSet();
        expressions = data.Expressions.ToHashSet();
        backgrounds = data.Backgrounds.ToHashSet();
        frames = data.Frames.ToHashSet();
        decorations = data.Decorations.ToHashSet();
        classJobs = data.ClassJobs.ToHashSet();
        glasses = data.Glasses.ToHashSet();
        dyes = data.Dyes.ToHashSet();
    }

    /// <summary>
    /// Validate detached history before handing any value to native imports. This deliberately
    /// checks current game data and render compatibility, without checking ownership or unlocks.
    /// </summary>
    public bool IsValid(PortraitEditorSnapshot? snapshot, out string reason)
    {
        reason = "肖像记录不完整或版本不受支持。";
        if (snapshot?.IsValid != true) return false;
        // AgentBannerEditorState.EditorOpenType: Portrait, Gearset, AdventurerPlate.
        if (snapshot.EntryPoint is not (0 or 1 or 2)) { reason = "肖像记录的编辑器类型无效。"; return false; }
        var customize = snapshot.Customize;
        if (customize[0] is < 1 or > 8 || customize[1] > 1)
        { reason = "肖像记录的角色种族或性别无效。"; return false; }
        var composition = snapshot.Composition;
        if (!HalfSafe(composition.CameraPosition) || !HalfSafe(composition.CameraTarget)
            || !HalfSafe(composition.HeadDirection) || !HalfSafe(composition.EyeDirection))
        { reason = "肖像记录的镜头或视线超出原生半精度数据范围。"; return false; }
        if (!timelines.Contains(composition.BannerTimeline)) { reason = "肖像记录的姿势在当前游戏版本中不存在。"; return false; }
        if (!expressions.Contains(composition.Expression)) { reason = "肖像记录的表情在当前游戏版本中不存在。"; return false; }
        if (!backgrounds.Contains(composition.Background)) { reason = "肖像记录的背景在当前游戏版本中不存在。"; return false; }
        if (!frames.Contains(composition.Frame)) { reason = "肖像记录的边框在当前游戏版本中不存在。"; return false; }
        if (!decorations.Contains(composition.Decoration)) { reason = "肖像记录的装饰在当前游戏版本中不存在。"; return false; }
        if (!classJobs.Contains(composition.PoseClassJob)) { reason = "肖像记录的姿势职业在当前游戏版本中不存在。"; return false; }
        for (var slot = 0; slot < 12; slot++)
        {
            var piece = snapshot.Look.Pieces[slot];
            if (piece.ItemID == 0)
            {
                if (piece.Stain0 != 0 || piece.Stain1 != 0) { reason = "空装备部位不能包含染色。"; return false; }
                continue;
            }
            if (!items.TryGetValue(piece.ItemID, out var item) || item.Model == 0 || (item.Slots & (1 << slot)) == 0)
            { reason = "肖像记录包含不存在、无法渲染或部位不符的装备。"; return false; }
            if ((item.RaceMask & (1 << (customize[0] - 1))) == 0 || (item.SexMask & (1 << customize[1])) == 0)
            { reason = "肖像记录的装备不适用于当前角色的种族或性别。"; return false; }
            if (!dyes.Contains(piece.Stain0) || !dyes.Contains(piece.Stain1)
                || item.DyeChannels < 1 && piece.Stain0 != 0 || item.DyeChannels < 2 && piece.Stain1 != 0)
            { reason = "肖像记录包含无效染剂或装备不支持的染色通道。"; return false; }
        }
        var facewear = snapshot.Look.Pieces[12];
        if (facewear.ItemID > ushort.MaxValue || !glasses.Contains((ushort)facewear.ItemID) || facewear.Stain0 != 0 || facewear.Stain1 != 0)
        { reason = "肖像记录包含不存在的眼镜或无效眼镜染色。"; return false; }
        reason = string.Empty;
        return true;
    }

    private static bool HalfSafe(float value) => float.IsFinite(value) && MathF.Abs(value) <= 65504;
    private static bool HalfSafe(PortraitVector4 value) => HalfSafe(value.X) && HalfSafe(value.Y) && HalfSafe(value.Z) && HalfSafe(value.W);
    private static bool HalfSafe(PortraitVector2 value) => HalfSafe(value.X) && HalfSafe(value.Y);

    private static PortraitValidationData ReadData(GameData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var equipment = new List<PortraitValidationItem>();
        foreach (var item in data.GetExcelSheet<Item>()!)
        {
            if (item.RowId == 0 || item.EquipSlotCategory.RowId == 0 || !item.EquipSlotCategory.IsValid) continue;
            var nativeSlot = item.EquipSlotCategory.Value;
            var values = new[] { nativeSlot.MainHand, nativeSlot.OffHand, nativeSlot.Head, nativeSlot.Body, nativeSlot.Gloves,
                nativeSlot.Legs, nativeSlot.Feet, nativeSlot.Ears, nativeSlot.Neck, nativeSlot.Wrists, nativeSlot.FingerR, nativeSlot.FingerL };
            ushort slots = 0;
            for (var i = 0; i < values.Length; i++) if (values[i] > 0) slots |= (ushort)(1 << i);
            byte raceMask = byte.MaxValue, sexMask = 3;
            if (item.EquipRestriction.RowId != 0)
            {
                raceMask = sexMask = 0;
                if (item.EquipRestriction.IsValid)
                {
                    var restriction = item.EquipRestriction.Value;
                    var allowed = new[] { restriction.Hyur, restriction.Elezen, restriction.Lalafell, restriction.Miqote,
                        restriction.Roegadyn, restriction.AuRa, restriction.Hrothgar, restriction.Viera };
                    for (var i = 0; i < allowed.Length; i++) if (allowed[i]) raceMask |= (byte)(1 << i);
                    if (restriction.Male) sexMask |= 1;
                    if (restriction.Female) sexMask |= 2;
                }
            }
            equipment.Add(new(item.RowId, slots, item.ModelMain, item.DyeCount, raceMask, sexMask));
        }
        return new(equipment,
            data.GetExcelSheet<BannerTimeline>()!.Where(row => row.RowId <= ushort.MaxValue).Select(row => (ushort)row.RowId).ToArray(),
            data.GetExcelSheet<BannerFacial>()!.Where(row => row.RowId <= byte.MaxValue).Select(row => (byte)row.RowId).ToArray(),
            data.GetExcelSheet<BannerBg>()!.Where(row => row.RowId <= ushort.MaxValue).Select(row => (ushort)row.RowId).ToArray(),
            data.GetExcelSheet<BannerFrame>()!.Where(row => row.RowId <= ushort.MaxValue).Select(row => (ushort)row.RowId).ToArray(),
            data.GetExcelSheet<BannerDecoration>()!.Where(row => row.RowId <= ushort.MaxValue).Select(row => (ushort)row.RowId).ToArray(),
            data.GetExcelSheet<ClassJob>()!.Where(row => row.RowId <= short.MaxValue).Select(row => (short)row.RowId).ToArray(),
            data.GetExcelSheet<Glasses>()!.Where(row => row.RowId is > 0 and <= ushort.MaxValue && row.Model != 0).Select(row => (ushort)row.RowId).Append((ushort)0).ToArray(),
            data.GetExcelSheet<Stain>()!.Where(row => row.RowId <= byte.MaxValue).Select(row => (byte)row.RowId).Append((byte)0).Distinct().ToArray());
    }
}

internal sealed record PortraitValidationItem(uint ID, ushort Slots, ulong Model, byte DyeChannels,
    byte RaceMask = byte.MaxValue, byte SexMask = 3);
internal sealed record PortraitValidationData(IReadOnlyList<PortraitValidationItem> Items,
    IReadOnlyList<ushort> Timelines, IReadOnlyList<byte> Expressions, IReadOnlyList<ushort> Backgrounds,
    IReadOnlyList<ushort> Frames, IReadOnlyList<ushort> Decorations, IReadOnlyList<short> ClassJobs,
    IReadOnlyList<ushort> Glasses, IReadOnlyList<byte> Dyes);
