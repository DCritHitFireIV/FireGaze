namespace FireGaze.PortraitPreview;

internal static class PortraitEquipmentSourceSummary
{
    internal static IReadOnlyList<string> Build(OwnedEquipmentView view, OwnedRetainerRoster? roster)
    {
        if (view.Character is null) return [];
        var lines = view.Sources.Where(source => OwnedSourcePolicy.IsSupported(source.Source.Kind)
                && source.Source.Kind != OwnedSourceKind.RetainerInventory)
            .OrderBy(source => source.Source.Kind).Select(source =>
            {
                var state = source.Status switch { OwnedReadStatus.Current => "已读取", OwnedReadStatus.PossiblyStale => "记录可能过期", _ => "尚未读取" };
                var readAt = source.ReadAt is { } time ? $" · {time.ToLocalTime():MM-dd HH:mm}" : "";
                return $"{Label(source.Source.Kind)}：{state}（{source.OwnedItemCount}）{readAt} · {source.Reason}";
            }).ToList();
        var retainers = PortraitEquipmentFilterSources.Build(view.Sources, view.Character, roster)
            .Single(choice => choice.Kind == OwnedSourceKind.RetainerInventory);
        lines.Add("雇员背包：" + retainers.StatusText);
        return lines;
    }

    internal static string Label(OwnedSourceKind kind) => kind switch
    {
        OwnedSourceKind.Equipped => "身上装备", OwnedSourceKind.Inventory => "背包", OwnedSourceKind.Armoury => "兵装库",
        OwnedSourceKind.Saddlebag => "陆行鸟鞍囊", OwnedSourceKind.PremiumSaddlebag => "额外鞍囊", OwnedSourceKind.GlamourDresser => "投影台",
        OwnedSourceKind.Armoire => "收藏柜", OwnedSourceKind.RetainerInventory => "雇员背包", _ => "其他位置",
    };
}
