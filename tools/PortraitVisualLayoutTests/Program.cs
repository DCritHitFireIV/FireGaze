#if HAS_PRESENTATION
using FireGaze.PortraitPreview;
#endif

var checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

#if HAS_PRESENTATION
foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
foreach (var available in new[] { 350f, 540f, 668f, 900f, 1600f })
{
    var side = PortraitPreviewLayout.SideWidth(available, scale);
    Check(side > 0 && side <= available * .46f, $"side panel leaves a reachable browser at width {available}, scale {scale}");
    var browser = available - side - 8 * scale;
    var edge = PortraitPreviewLayout.GridEdge(browser, 82 * scale);
    var columns = PortraitPreviewLayout.Columns(browser, edge, 8 * scale);
    Check(columns >= 1 && columns * edge + (columns - 1) * 8 * scale <= browser + .01f,
        $"every grid row fits its browser at width {available}, scale {scale}");
}
Check(PortraitPreviewLayout.FitsInline(500, 350, 140, 10), "toolbar can use exact remaining width without wrapping");
Check(!PortraitPreviewLayout.FitsInline(500, 350, 141, 10), "toolbar wraps the next action instead of clipping its last pixel");
Check(!PortraitPreviewLayout.FitsInline(0, 0, 30, 10), "zero-width content does not request an inline action");

var plusEight = TimeZoneInfo.CreateCustomTimeZone("test-plus-eight", TimeSpan.FromHours(8), "test", "test");
var cache = new PortraitHistoryPresentation();
var source = new CountingHistory([
    new(1, DateTimeOffset.Parse("2026-10-17T15:59:00Z"), "旧动作", false),
    new(3, DateTimeOffset.Parse("2026-10-17T16:01:00Z"), "恢复旧时刻", true),
    new(2, DateTimeOffset.Parse("2026-10-17T16:00:00Z"), "新动作", false),
]);
var days = cache.Get(source, plusEight);
Check(days.Count == 2 && days[0].Date == new DateTime(2026, 10, 18), "history groups by local calendar day rather than UTC date");
Check(days[0].Records.Select(record => record.ID).SequenceEqual(new long[] { 3, 2 }), "history is newest first within a local day");
Check(days[1].Records.Single().ID == 1, "local midnight does not lose the preceding day's record");
Check(days[0].Records[0].IsCurrent, "the current restore event remains visible in the prepared history");
var reads = source.Reads;
Check(Enumerable.Range(0, 1000).All(_ => ReferenceEquals(cache.Get(source, plusEight), days)),
    "a thousand unchanged frames reuse the same prepared history view");
Check(source.Reads == reads, "a thousand frames do not enumerate or sort the unchanged history again");
var changed = source.ToArray();
changed[0] = changed[0] with { IsCurrent = true };
changed[1] = changed[1] with { IsCurrent = false };
var refreshed = cache.Get(changed, plusEight);
Check(!ReferenceEquals(refreshed, days) && refreshed[1].Records.Single().IsCurrent, "a replacement view refreshes current markers even with the same record count");
var utcDays = cache.Get(changed, TimeZoneInfo.Utc);
Check(utcDays.Count == 1, "a timezone change rebuilds calendar boundaries");
Check(cache.Get([], plusEight).Count == 0, "empty history has no invented dates or current event");
#else
Check(false, "portrait presentation must fit narrow scaled grids and cache immutable history views");
#endif
Console.WriteLine($"PortraitVisualLayoutTests: {checks} passed, 0 failed");

#if HAS_PRESENTATION
sealed class CountingHistory(IReadOnlyList<PortraitHistoryListItem> rows) : IReadOnlyList<PortraitHistoryListItem>
{
    public int Reads { get; private set; }
    public int Count => rows.Count;
    public PortraitHistoryListItem this[int index] { get { Reads++; return rows[index]; } }
    public IEnumerator<PortraitHistoryListItem> GetEnumerator()
    {
        foreach (var row in rows) { Reads++; yield return row; }
    }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
#endif
