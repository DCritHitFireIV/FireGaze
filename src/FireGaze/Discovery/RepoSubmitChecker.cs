using System.Net;
using System.Net.Http;
using FireGaze.RepoAudit;

namespace FireGaze.Discovery;

/// <summary>投稿检测的结果。</summary>
internal sealed record RepoSubmitCheck(
    bool Ok,
    string Message,
    string? NormalizedURL,
    int PluginCount,
    int Dropped);

/// <summary>
///     投稿库链的本机检测：把地址抓下来，用卫月同款契约（<see cref="ManifestCheck" />）验一遍
///     —— 地址通、是仓库数组、至少有一条能被卫月识别的插件。不合格当场给理由，不往云端发。
/// </summary>
internal static class RepoSubmitChecker
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 4,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static async Task<RepoSubmitCheck> CheckAsync(string url, CancellationToken cancellationToken)
        => await CheckFromClientAsync(Client, url, cancellationToken).ConfigureAwait(false);

    internal static async Task<RepoSubmitCheck> CheckFromClientAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        var text = (url ?? string.Empty).Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return new RepoSubmitCheck(false, "这看起来不是一条 http(s) 地址", null, 0, 0);
        }

        var normalized = InstalledPluginsIndex.NormalizeRepositoryURL(text);

        var body = await RepoScanner.FetchRepositoryBodyAsync(client, text, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return new RepoSubmitCheck(false, "取不到这个地址：可能网络不通、404、或者它需要登录；确认地址能在浏览器打开后重试", normalized, 0, 0);
        }

        var check = ManifestCheck.Check(body);
        if (!check.Ok)
        {
            return new RepoSubmitCheck(
                false,
                "不是卫月能读的仓库文件：" + (check.Error ?? "内容格式不对") + "；需要的是 pluginmaster.json / repo.json 这类仓库文件",
                normalized,
                0,
                0);
        }

        if (check.Count == 0)
        {
            return new RepoSubmitCheck(false, "这个文件是空数组，一条插件都没有；换一条包含插件的仓库文件", normalized, 0, 0);
        }

        var valid = check.Count - check.Dropped;
        if (valid <= 0)
        {
            return new RepoSubmitCheck(
                false,
                $"文件里有 {check.Count} 条，但没有一条能被卫月识别（缺内部名 / 插件名 / 版本号）",
                normalized,
                check.Count,
                check.Dropped);
        }

        var extra = check.Dropped > 0 ? $"，另有 {check.Dropped} 条会被卫月丢弃" : string.Empty;
        return new RepoSubmitCheck(true, $"检测通过：{valid} 条插件{extra}", normalized, check.Count, check.Dropped);
    }

}
