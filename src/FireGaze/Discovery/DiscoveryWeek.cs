namespace FireGaze.Discovery;

/// <summary>
///     北京时间（UTC+8）的 ISO 周标签，如 2026-W41；周界 = 周一 00:00。
///     和中继 Worker 的 <c>beijingISOWeek</c> 同一口径——点赞「一周一次」的本地判定用它，
///     这样即使统计拉不到（中继还没部署 v8）按钮也能正确变灰（2026-10-06）。
/// </summary>
internal static class DiscoveryWeek
{
    /// <summary>把 UTC 时刻换算成北京时间的 ISO 周标签。</summary>
    public static string Label(DateTimeOffset nowUTC)
    {
        var shifted = nowUTC.ToUniversalTime().AddHours(8);
        var date = shifted.Date;
        var day = date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
        var thursday = date.AddDays(4 - day);   // 所在 ISO 周的周四决定年份与周数
        var yearStart = new DateTime(thursday.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var week = ((thursday - yearStart).Days / 7) + 1;
        return $"{thursday.Year}-W{week:00}";
    }

    public static string Current() => Label(DateTimeOffset.UtcNow);
}
