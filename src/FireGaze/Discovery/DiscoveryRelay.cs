using System.Net.Http;
using System.Text;
using System.Text.Json;
using FireGaze.Translate;

namespace FireGaze.Discovery;

/// <summary>
///     插件发现的中继统计：本周点赞 / 总点赞 / 推荐（从云端加进自己库的次数）。
///     打开页签时拉一次（10 分钟节流），失败就用上次缓存的这份。
/// </summary>
internal sealed class DiscoveryStats
{
    public string Week { get; set; } = string.Empty;

    public DateTime FetchedUTC { get; set; }

    public Dictionary<string, int> WeeklyLikes { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> TotalLikes { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> Recommends { get; set; } = new(StringComparer.Ordinal);

    public int WeeklyOf(string internalName) => WeeklyLikes.TryGetValue(internalName, out var value) ? value : 0;

    public int TotalOf(string internalName) => TotalLikes.TryGetValue(internalName, out var value) ? value : 0;

    public int RecommendsOf(string internalName) => Recommends.TryGetValue(internalName, out var value) ? value : 0;
}

/// <summary>插件发现的中继通道（点赞 / 加库上报 / 库链投稿 / 统计拉取）。</summary>
internal static class DiscoveryRelay
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string StatsEndpoint => ContributeRelay.URL + "plugin-stats";

    private static string LikeEndpoint => ContributeRelay.URL + "plugin-like";

    private static string AddEndpoint => ContributeRelay.URL + "plugin-add";

    private static string SubmitEndpoint => ContributeRelay.URL + "repo-submit";

    /// <summary>拉统计；失败返回 null（调用方用缓存兜底）。</summary>
    public static async Task<DiscoveryStats?> FetchStatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Client.GetAsync(StatsEndpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            var week = root.TryGetProperty("week", out var weekProp) ? weekProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(week))
            {
                // 中继还是旧版（没有 /plugin-stats，只会返回版本探测）——按不可用处理
                return null;
            }

            var stats = new DiscoveryStats
            {
                Week = week,
                FetchedUTC = DateTime.UtcNow,
            };
            ReadCounts(root, "weekly", stats.WeeklyLikes);
            ReadCounts(root, "total", stats.TotalLikes);
            ReadCounts(root, "adds", stats.Recommends);
            return stats;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>点一个赞（按插件、按周）；返回云端的最新计数。</summary>
    public static async Task<(bool Ok, int Total, int Weekly, string? Error)> LikeAsync(
        string internalName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { plugin = internalName }),
                Encoding.UTF8,
                "application/json");
            using var response = await Client.PostAsync(LikeEndpoint, content, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, 0, 0, $"HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            if (!ok)
            {
                return (false, 0, 0, root.TryGetProperty("error", out var err) ? err.GetString() : "unknown");
            }

            var total = root.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
            var weekly = root.TryGetProperty("weekly", out var w) ? w.GetInt32() : 0;
            return (true, total, weekly, null);
        }
        catch (Exception e)
        {
            return (false, 0, 0, e.GetType().Name);
        }
    }

    /// <summary>上报一批「从云端加进自己库」的插件（按插件计数）。</summary>
    public static async Task<bool> ReportAddsAsync(
        IReadOnlyList<string> internalNames,
        CancellationToken cancellationToken)
    {
        if (internalNames.Count == 0)
        {
            return true;
        }

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { plugins = internalNames }),
                Encoding.UTF8,
                "application/json");
            using var response = await Client.PostAsync(AddEndpoint, content, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>投稿一条仓库地址（进云端语料）；返回 (是否受理, 说明)。</summary>
    public static async Task<(bool Ok, string Message)> SubmitRepoAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { url }),
                Encoding.UTF8,
                "application/json");
            using var response = await Client.PostAsync(SubmitEndpoint, content, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"中继不可用（HTTP {(int)response.StatusCode}）");
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            if (ok)
            {
                return (true, "已投稿，等云端收录");
            }

            return (false, root.TryGetProperty("error", out var err) ? err.GetString() ?? "unknown" : "unknown");
        }
        catch (Exception e)
        {
            return (false, "中继不可达：" + e.GetType().Name);
        }
    }

    private static void ReadCounts(JsonElement root, string property, Dictionary<string, int> target)
    {
        if (!root.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var item in node.EnumerateObject())
        {
            if (item.Value.ValueKind == JsonValueKind.Number)
            {
                target[item.Name] = item.Value.GetInt32();
            }
        }
    }
}
