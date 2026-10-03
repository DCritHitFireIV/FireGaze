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

    /// <summary>直传端点：界面文字投稿整个 payload 直接上传，不再走 issue 粘贴（2026-10-04 新流程）。</summary>
    public const string SubmitEndpoint = URL + "uit-submit";

    /// <summary>公共库下载计数端点（发完即忘）。</summary>
    private const string LibraryDownloadEndpoint = URL + "library-download";

    /// <summary>公共库点👍端点（按包计数）。</summary>
    private const string LibraryLikeEndpoint = URL + "library-like";

    /// <summary>正文上限（GitHub issue 上限 65536 的安全余量）。</summary>
    public const int MaxBody = 60000;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    ///     把投稿发给中继；返回 (是否成功, 失败原因或 issue 地址)。
    ///     <paramref name="type" />：稿子类型（<c>feedback</c> = 用户反馈；留空 = 译文投稿，兼容旧版 Worker）。
    /// </summary>
    public static async Task<(bool Ok, string Message)> TrySubmitAsync(string title, string body, string? type = null)
    {
        try
        {
            var payload = type is null
                ? (object)new { title, body }
                : new { title, body, type };
            using var content = new StringContent(
                JsonSerializer.Serialize(payload),
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

    /// <summary>
    ///     直传界面文字投稿：整个 payload 交给中继提交进仓库（docs/contributions/inbox/uit-direct-…），
    ///     仓库工作流接手并入 uit-packs。返回（是否成功，云端文件链接或失败原因）；
    ///     失败时调用方回退到现有「中继建 issue」流程——绝不谎报成功。
    /// </summary>
    public static async Task<(bool Ok, string Message)> TrySubmitPayloadAsync(string payloadJSON)
    {
        try
        {
            using var content = new StringContent(payloadJSON, Encoding.UTF8, "application/json");
            using var response = await Client.PostAsync(SubmitEndpoint, content).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, DescribeError(text, (int)response.StatusCode));
            }

            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                var file = doc.RootElement.TryGetProperty("file", out var fileProp)
                    ? fileProp.GetString() ?? string.Empty
                    : string.Empty;
                return (true, file);
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

    /// <summary>
    ///     上报一次公共库下载（发完即忘）：包本体仍从 raw/镜像下载，这里只多报一条计数，
    ///     由中继写进 KV，定时工作流再写回 uit-packs/index.json（玩家能看到「下载数」）。
    ///     中继没配计数 / 网络不通一律静默——计数绝不能影响玩家。
    /// </summary>
    public static void ReportLibraryDownload(string internalName, string packID = "library") =>
        PostLibraryCounter(LibraryDownloadEndpoint, "下载计数", internalName, packID);

    /// <summary>给一个译文包点👍（发完即忘；与下载数一样按包统计，写回索引后人人可见）。</summary>
    public static void ReportLibraryLike(string internalName, string packID = "library") =>
        PostLibraryCounter(LibraryLikeEndpoint, "点赞", internalName, packID);

    private static void PostLibraryCounter(string endpoint, string what, string internalName, string packID)
    {
        if (string.IsNullOrWhiteSpace(internalName))
        {
            return;
        }

        var pack = string.IsNullOrWhiteSpace(packID) ? "library" : packID;
        _ = Task.Run(async () =>
        {
            try
            {
                using var content = new StringContent(
                    JsonSerializer.Serialize(new { plugin = internalName, pack }),
                    Encoding.UTF8,
                    "application/json");
                using var response = await Client.PostAsync(endpoint, content).ConfigureAwait(false);
                Plugin.Log?.Verbose($"[内部文本] {what}上报：{internalName}@{pack} → HTTP {(int)response.StatusCode}");
            }
            catch (Exception)
            {
                // 计数/点赞都是尽力而为，绝不打扰玩家
            }
        });
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
