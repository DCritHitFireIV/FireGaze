using System.Net.Http;
using System.Text;
using System.Text.Json;
using FireGaze.UIText;

namespace FireGaze.Translate;

/// <summary>
///     译文投稿的中继（Cloudflare Worker）通道——「插件汉化」页的行按钮与编辑器共用。
/// </summary>
/// <remarks>
///     relay 优先：一步提交、玩家不用 GitHub 账号、客户端不持有任何密钥；
///     失败时调用方负责回退「打开填好的 issue 页」（绝不谎报提交成功）。
///     2026-10-02 从 ContributeWindow 抽出来共用（行上的「一键上传」也要走同一条通道）。
/// </remarks>
internal static class ContributeRelay
{
    /// <summary>中继地址（Cloudflare Worker，部署说明见 scripts/relay/README.md）。</summary>
    public const string URL = "https://firegaze-relay.yuoonmail.workers.dev/";

    /// <summary>正文上限（GitHub issue 上限 65536 的安全余量）。</summary>
    public const int MaxBody = 60000;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    ///     把投稿发给中继；返回 (是否成功, 失败原因或 issue 地址)。
    /// </summary>
    public static async Task<(bool Ok, string Message)> TrySubmitAsync(string title, string body)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { title, body }),
                Encoding.UTF8,
                "application/json");

            using var response = await Client.PostAsync(URL, content).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, DescribeError(text, (int)response.StatusCode));
            }

            using var doc = JsonDocument.Parse(text);
            var ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            if (ok)
            {
                var issue = doc.RootElement.TryGetProperty("issue", out var issueProp)
                    ? issueProp.GetString() ?? string.Empty
                    : string.Empty;
                return (true, issue);
            }

            var error = doc.RootElement.TryGetProperty("error", out var errProp)
                ? errProp.GetString() ?? "unknown"
                : "unknown";
            return (false, error);
        }
        catch (Exception e)
        {
            return (false, e.GetType().Name);
        }
    }

    /// <summary>回退通道：拼一个填好标题与正文的「新建 issue」地址。</summary>
    public static string BuildIssueURL(string title, string body)
    {
        return $"{ContributionsStore.RepoURL}/issues/new"
               + $"?title={Uri.EscapeDataString(title)}"
               + $"&body={Uri.EscapeDataString(body)}";
    }

    /// <summary>把 Worker 的错误 JSON 变成一句人话（拿不到就用 HTTP 码）——旧版只回 HTTP 400，看不出原因。</summary>
    private static string DescribeError(string text, int statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                var message = error.GetString() ?? "unknown";
                if (doc.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                {
                    var value = detail.GetString() ?? string.Empty;
                    if (value.Length > 0)
                    {
                        message += " · " + UITextText.OneLine(value, 120);
                    }
                }

                return $"HTTP {statusCode} · {message}";
            }
        }
        catch (JsonException)
        {
            // 不是 JSON：走下面的兜底
        }

        return $"HTTP {statusCode}";
    }
}
