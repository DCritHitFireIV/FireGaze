using System.Globalization;

namespace FireGaze.PortraitPreview;

internal sealed record PortraitHistoryListItem(long ID, DateTimeOffset At, string Label, bool IsCurrent);
internal sealed record PortraitHistoryViewDay(DateTime Date, IReadOnlyList<PortraitHistoryListItem> Records);

internal static class PortraitHistoryLabels
{
    public static string DayHeader(DateTime date, int count)
        => $"{date:yyyy-MM-dd} · {count} 条###PortraitHistoryDay{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    public static string RecordLabel(PortraitHistoryListItem record)
        => $"{record.At.ToLocalTime():HH:mm:ss}  {record.Label}{(record.IsCurrent ? "  · 当前" : "")}###PortraitHistoryRecord{record.ID.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>The feature replaces immutable row snapshots; unchanged frames do not sort the full history again.</summary>
internal sealed class PortraitHistoryPresentation
{
    private IReadOnlyList<PortraitHistoryListItem>? source;
    private TimeZoneInfo? timeZone;
    private IReadOnlyList<PortraitHistoryViewDay> days = [];

    public IReadOnlyList<PortraitHistoryViewDay> Get(IReadOnlyList<PortraitHistoryListItem> records, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        if (ReferenceEquals(source, records) && Equals(timeZone, zone)) return days;
        days = records.OrderByDescending(record => record.At)
            .GroupBy(record => TimeZoneInfo.ConvertTime(record.At, zone).Date)
            .Select(group => new PortraitHistoryViewDay(group.Key, group.ToArray())).ToArray();
        source = records;
        timeZone = zone;
        return days;
    }
}

internal static class PortraitPreviewLayout
{
    public static float SideWidth(float available, float scale)
        => Math.Max(1, Math.Min(198 * scale, available * .46f));

    public static float GridEdge(float available, float desired)
        => Math.Max(1, Math.Min(available, desired));

    public static int Columns(float available, float edge, float gap)
        => Math.Max(1, (int)((available + gap) / Math.Max(1, edge + gap)));

    public static bool FitsInline(float right, float previousRight, float width, float gap)
        => previousRight + gap + width <= right;
}
