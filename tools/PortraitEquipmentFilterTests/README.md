# Portrait equipment filtering

Run the pure, offline query contract:

```powershell
dotnet run --project tools/PortraitEquipmentFilterTests/PortraitEquipmentFilterTests.csproj -c Release
```

The suite compiles the production filter, source-choice aggregation and existing owned-source models directly. It has no game, Dalamud or UI dependencies.

## Query contract

- No source selection means all sources, including legacy entries without source metadata.
- No dye selection means all dye channel counts.
- Selected sources use OR; selected dye channel counts use OR. Source, dye and search groups use AND.
- Search trims surrounding whitespace and matches a case-insensitive name substring or numeric ID substring. A leading `#` is accepted for ID search.
- Default order is a case-insensitive ordinal name order. Equal names use numeric ID order.
- Item-level and equipment-required-level ascending/descending use name and numeric ID order to resolve ties. These are independent fields. ID sorting is numeric.
- Repeated item IDs keep the first item's metadata and merge unique sources in observed order before filtering. Inputs are unchanged.
- Legacy six-argument `PortraitBrowserItem` construction remains valid. Its optional item level/required level are zero and sources are empty.

## Test-first evidence

Observed runs during implementation:

| Step | Red result | Green result |
| --- | --- | --- |
| Default query absent | 0 / 1 | 1 / 1 |
| Source/dye/search behavior absent | 4 / 15 | 15 / 15 |
| Sort modes and deduplication absent | 17 / 27 | 27 / 27 |
| Additional category, numeric and empty-input boundaries | — | 45 / 45 |
| Required-level sorts and source-read distinctions absent | 45 / 48 | 56 / 56 |

The added boundary cases include each of the nine owned-source kinds, missing source metadata, repeated selections, Chinese names, unknown persisted sort values, bare `#`, empty input and IDs/item levels beyond signed 32-bit range.

The source-choice tests also cover a fresh character, successfully read empty containers, cached closed containers and a retainer category with both unread and cached retainers. Cached records remain selectable; unknown sources cannot become filter selections. Counts and state summaries do not mutate the catalog observations.

## Host integration

Remove the old six-field `PortraitBrowserItem` declaration from `PortraitPreviewWindow.cs`; the record now lives with this query model. Pass item-sheet level data and all cached owned-source kinds through the new optional arguments. The main project includes new C# files automatically. Add this standalone suite to the host's validation runner when integrating.

The window should cache query results until the selected filters or catalog change, rather than allocating a fresh grouped and sorted result for every item or frame.

For facewear, omit source and dye groups from the active query without clearing their stored selections: glasses come from unlocked collections. The slot-switch UI explains this difference.
