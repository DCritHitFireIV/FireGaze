using FireGaze.PortraitPreview;

var checks = 0;
var failures = new List<string>();
void Check(bool condition, string message)
{
    checks++;
    if (!condition) failures.Add(message);
}

#if HAS_FILTER
var items = new[]
{
    new PortraitBrowserItem(30, "Cedar", 300, 2, false, "Unavailable", 600, 90, [OwnedSourceKind.Inventory]),
    new PortraitBrowserItem(20, "amber", 200, 0, true, "", 100, 30, [OwnedSourceKind.GlamourDresser]),
    new PortraitBrowserItem(10, "Birch", 100, 1, true, "", 200, 50, [OwnedSourceKind.Armoire]),
};
Check(PortraitEquipmentFilter.Apply(items).Select(item => item.ID).SequenceEqual(new uint[] { 20, 10, 30 }),
    "default query lists available and unavailable items in name order");

var catalog = items.Concat(new[]
{
    new PortraitBrowserItem(40, "Drift", 400, 2, true, "", 200, 50, [OwnedSourceKind.Inventory, OwnedSourceKind.RetainerInventory]),
    new PortraitBrowserItem(50, "Ember", 500, 0, true, ""),
}).ToArray();
void CheckIds(PortraitEquipmentQuery? query, uint[] expected, string message) =>
    Check(PortraitEquipmentFilter.Apply(catalog, query).Select(item => item.ID).SequenceEqual(expected), message);

CheckIds(new(Sources: [OwnedSourceKind.Inventory]), [30, 40], "source filtering includes either location of a multi-source item");
CheckIds(new(Sources: [OwnedSourceKind.Inventory, OwnedSourceKind.GlamourDresser]), [20, 30, 40], "selected source categories are combined with OR");
CheckIds(new(Sources: []), [20, 10, 30, 40, 50], "no selected sources means all sources including unknown source metadata");
CheckIds(new(DyeChannels: [0, 2]), [20, 30, 40, 50], "non-dyeable and double-dyeable selections are combined with OR");
CheckIds(new(DyeChannels: [1]), [10], "single-dyeable filtering excludes zero and two channels");
CheckIds(new(DyeChannels: []), [20, 10, 30, 40, 50], "no selected dye categories means all dye channel counts");
CheckIds(new(Sources: [OwnedSourceKind.Inventory, OwnedSourceKind.Armoire], DyeChannels: [0, 2]), [30, 40], "source and dye groups are combined with AND");
CheckIds(new(Search: "  ceD  "), [30], "name search trims whitespace and ignores case");
CheckIds(new(Search: "  3  "), [30], "identifier search matches numeric text");
CheckIds(new(Search: " #30 "), [30], "identifier search accepts an optional hash prefix");
CheckIds(new(Search: "Cedar", Sources: [OwnedSourceKind.Armoire]), [], "search and selected source must both match");
CheckIds(new(Search: "Cedar", DyeChannels: [1]), [], "search and selected dye category must both match");
CheckIds(new(Search: "   "), [20, 10, 30, 40, 50], "whitespace-only search does not hide items");
CheckIds(new(Search: "missing"), [], "unmatched search has no results");
CheckIds(new(Sort: PortraitEquipmentSort.ItemLevelAscending), [50, 20, 10, 40, 30], "item level ascending orders equipment by item level, then name");
CheckIds(new(Sort: PortraitEquipmentSort.ItemLevelDescending), [30, 10, 40, 20, 50], "item level descending reverses level while name tie order stays readable");
CheckIds(new(Sort: PortraitEquipmentSort.IDAscending), [10, 20, 30, 40, 50], "identifier ascending sorts identifiers numerically");
CheckIds(new(Sort: PortraitEquipmentSort.IDDescending), [50, 40, 30, 20, 10], "identifier descending sorts identifiers numerically");
CheckIds(new(Sources: [OwnedSourceKind.Inventory], DyeChannels: [2], Sort: PortraitEquipmentSort.IDDescending), [40, 30], "sorting applies after all selected filter groups");

var first = new PortraitBrowserItem(77, "Same", 770, 2, false, "First reason", 123, 45, [OwnedSourceKind.Inventory]);
var duplicates = new[]
{
    first,
    new PortraitBrowserItem(77, "Wrong duplicate metadata", 999, 1, true, "", 999, 99, [OwnedSourceKind.GlamourDresser, OwnedSourceKind.Inventory]),
    new PortraitBrowserItem(88, "same", 880, 0, true, "", 123, 1, [OwnedSourceKind.Armoire]),
};
var deduplicated = PortraitEquipmentFilter.Apply(duplicates);
Check(deduplicated.Select(item => item.ID).SequenceEqual(new uint[] { 77, 88 }), "duplicate equipment IDs appear once");
Check(deduplicated[0].Name == "Same" && deduplicated[0].Icon == 770 && deduplicated[0].DyeChannels == 2
    && !deduplicated[0].Available && deduplicated[0].Reason == "First reason" && deduplicated[0].ItemLevel == 123
    && deduplicated[0].RequiredLevel == 45, "stable deduplication keeps first item metadata and restriction reason");
Check(deduplicated[0].Sources.SequenceEqual(new[] { OwnedSourceKind.Inventory, OwnedSourceKind.GlamourDresser }),
    "duplicate equipment merges unique source categories in their observed order");
Check(PortraitEquipmentFilter.Apply(duplicates, new(Sources: [OwnedSourceKind.GlamourDresser], DyeChannels: [2]))
    .Select(item => item.ID).SequenceEqual(new uint[] { 77 }), "duplicate source merging occurs before filtering and retains first dye metadata");
Check(first.Sources.SequenceEqual(new[] { OwnedSourceKind.Inventory }), "source merging never changes input records or arrays");

var sameNames = new[]
{
    new PortraitBrowserItem(9, "same", 0, 0, true, "", 100),
    new PortraitBrowserItem(2, "Same", 0, 0, true, "", 100),
};
Check(PortraitEquipmentFilter.Apply(sameNames).Select(item => item.ID).SequenceEqual(new uint[] { 2, 9 }),
    "name ties use identifier order so the browser order is reproducible");
Check(PortraitEquipmentFilter.Apply(sameNames, new(Sort: PortraitEquipmentSort.ItemLevelDescending))
    .Select(item => item.ID).SequenceEqual(new uint[] { 2, 9 }), "equal item levels and names use identifier order");

Check(PortraitEquipmentFilter.Apply(Array.Empty<PortraitBrowserItem>(), new(Sources: [OwnedSourceKind.Inventory], DyeChannels: [0, 2]))
    .Count == 0, "empty input remains empty with selected filters");
CheckIds(null, [20, 10, 30, 40, 50], "a null query retains the legacy unfiltered browser");
CheckIds(new(Sources: [OwnedSourceKind.Inventory, OwnedSourceKind.Inventory], DyeChannels: [2, 2]), [30, 40],
    "repeated selections do not repeat results");
CheckIds(new(Sources: [OwnedSourceKind.RetainerEquipped]), [], "a selected source with no matching equipment is empty");
CheckIds(new(Search: "#"), [], "a bare hash search does not match every identifier");
CheckIds(new(Sort: (PortraitEquipmentSort)999), [20, 10, 30, 40, 50], "unknown persisted sort values safely fall back to name order");

var everySource = new[]
{
    new PortraitBrowserItem(1, "01", 0, 0, true, "", Sources: [OwnedSourceKind.Equipped]),
    new PortraitBrowserItem(2, "02", 0, 0, true, "", Sources: [OwnedSourceKind.Inventory]),
    new PortraitBrowserItem(3, "03", 0, 0, true, "", Sources: [OwnedSourceKind.Armoury]),
    new PortraitBrowserItem(4, "04", 0, 0, true, "", Sources: [OwnedSourceKind.Saddlebag]),
    new PortraitBrowserItem(5, "05", 0, 0, true, "", Sources: [OwnedSourceKind.PremiumSaddlebag]),
    new PortraitBrowserItem(6, "06", 0, 0, true, "", Sources: [OwnedSourceKind.GlamourDresser]),
    new PortraitBrowserItem(7, "07", 0, 0, true, "", Sources: [OwnedSourceKind.Armoire]),
    new PortraitBrowserItem(8, "08", 0, 0, true, "", Sources: [OwnedSourceKind.RetainerInventory]),
    new PortraitBrowserItem(9, "09", 0, 0, true, "", Sources: [OwnedSourceKind.RetainerEquipped]),
};
var sourceCases = new (OwnedSourceKind Source, uint Expected)[]
{
    (OwnedSourceKind.Equipped, 1), (OwnedSourceKind.Inventory, 2), (OwnedSourceKind.Armoury, 3),
    (OwnedSourceKind.Saddlebag, 4), (OwnedSourceKind.PremiumSaddlebag, 5), (OwnedSourceKind.GlamourDresser, 6),
    (OwnedSourceKind.Armoire, 7), (OwnedSourceKind.RetainerInventory, 8), (OwnedSourceKind.RetainerEquipped, 9),
};
foreach (var (source, expected) in sourceCases)
    Check(PortraitEquipmentFilter.Apply(everySource, new(Sources: [source])).Select(item => item.ID)
        .SequenceEqual(new[] { expected }), $"source category {source} is selectable without leaking other categories");

var largeIDs = new[]
{
    new PortraitBrowserItem(uint.MaxValue, "Last", 0, 0, true, "", uint.MaxValue),
    new PortraitBrowserItem(2_147_483_648, "Middle", 0, 0, true, "", 2_147_483_648),
    new PortraitBrowserItem(9, "First", 0, 0, true, "", 9),
};
Check(PortraitEquipmentFilter.Apply(largeIDs, new(Sort: PortraitEquipmentSort.IDAscending)).Select(item => item.ID)
    .SequenceEqual(new uint[] { 9, 2_147_483_648, uint.MaxValue }), "identifier sorting does not overflow signed integers");
Check(PortraitEquipmentFilter.Apply(largeIDs, new(Sort: PortraitEquipmentSort.ItemLevelDescending)).Select(item => item.ID)
    .SequenceEqual(new uint[] { uint.MaxValue, 2_147_483_648, 9 }), "item level sorting does not overflow signed integers");
Check(PortraitEquipmentFilter.Apply(new[] { new PortraitBrowserItem(99, "古董提灯", 0, 0, true, "") }, new(Search: "提灯"))
    .Select(item => item.ID).SequenceEqual(new uint[] { 99 }), "name search handles non-Latin equipment names");

// Equip-required level is independent of item level. Reusing the item-level branch would reverse these fixtures.
var equipmentLevelItems = new[]
{
    new PortraitBrowserItem(1, "Alpha", 0, 0, true, "", 700, 1),
    new PortraitBrowserItem(2, "Beta", 0, 0, true, "", 100, 100),
    new PortraitBrowserItem(3, "Gamma", 0, 0, true, "", 200, 50),
};
Check(Enum.TryParse<PortraitEquipmentSort>("RequiredLevelAscending", out var requiredAscending),
    "equipment required-level ascending is a separate selectable sort");
Check(Enum.TryParse<PortraitEquipmentSort>("RequiredLevelDescending", out var requiredDescending),
    "equipment required-level descending is a separate selectable sort");
if (requiredAscending != PortraitEquipmentSort.Name)
    Check(PortraitEquipmentFilter.Apply(equipmentLevelItems, new(Sort: requiredAscending)).Select(item => item.ID)
        .SequenceEqual(new uint[] { 1, 3, 2 }), "required-level ascending does not use item level");
if (requiredDescending != PortraitEquipmentSort.Name)
    Check(PortraitEquipmentFilter.Apply(equipmentLevelItems, new(Sort: requiredDescending)).Select(item => item.ID)
        .SequenceEqual(new uint[] { 2, 3, 1 }), "required-level descending does not use item level");

#if HAS_SOURCE_CHOICES
var readAt = DateTimeOffset.Parse("2026-10-19T09:00:00Z");
var observations = new[]
{
    new OwnedSourceView(new(OwnedSourceKind.Inventory), OwnedReadStatus.Current, readAt, 2, ""),
    new OwnedSourceView(new(OwnedSourceKind.Armoire), OwnedReadStatus.Current, readAt, 0, ""),
    new OwnedSourceView(new(OwnedSourceKind.GlamourDresser), OwnedReadStatus.PossiblyStale, readAt, 8, "Closed"),
    new OwnedSourceView(new(OwnedSourceKind.RetainerInventory, 100), OwnedReadStatus.Current, readAt, 3, ""),
    new OwnedSourceView(new(OwnedSourceKind.RetainerInventory, 200), OwnedReadStatus.PossiblyStale, readAt.AddDays(-1), 4, ""),
    new OwnedSourceView(new(OwnedSourceKind.RetainerInventory, 300), OwnedReadStatus.Unread, null, 0, ""),
};
var choices = PortraitEquipmentFilterSources.Build(observations);
var choice = choices.Single(source => source.Kind == OwnedSourceKind.Saddlebag);
Check(!choice.CanSelect && choice.HasUnread && choice.OwnedItemCount == 0,
    "missing sources appear as unread and cannot be selected");
choice = choices.Single(source => source.Kind == OwnedSourceKind.Armoire);
Check(choice.CanSelect && !choice.HasUnread && !choice.HasCached && choice.OwnedItemCount == 0,
    "a successfully read empty container remains selectable");
choice = choices.Single(source => source.Kind == OwnedSourceKind.GlamourDresser);
Check(choice.CanSelect && choice.HasCached && !choice.HasUnread && choice.OwnedItemCount == 8,
    "cached closed sources remain selectable and carry a cached status");
choice = choices.Single(source => source.Kind == OwnedSourceKind.RetainerInventory);
Check(choice.CanSelect && choice.HasUnread && choice.HasCached && choice.OwnedItemCount == 7,
    "retainer categories merge read records without pretending unread retainers were read");
Check(choices.Select(source => source.Kind).Distinct().Count() == 9 && choices.Count == 9,
    "every supported storage category appears once even when no data exists");
Check(PortraitEquipmentFilterSources.Build([]).All(source => !source.CanSelect && source.HasUnread),
    "a new character has no selectable storage records");
Check(observations[0].OwnedItemCount == 2 && observations[2].Status == OwnedReadStatus.PossiblyStale,
    "source choice aggregation leaves live catalog snapshots unchanged");
#else
Check(false, "source choices must preserve unread, read-empty, and cached distinctions");
#endif
#else
Check(false, "equipment filtering must provide a default name-ordered query");
#endif

foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
Console.WriteLine($"Portrait equipment filter: {checks - failures.Count}/{checks} checks passed.");
return failures.Count == 0 ? 0 : 1;
