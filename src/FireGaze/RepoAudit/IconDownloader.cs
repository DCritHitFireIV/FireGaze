using System.Net;

namespace FireGaze.RepoAudit;

/// <summary>
/// 图标下载：自己的 HttpClient + 体检同款的多线路（直连 / 镜像），只取二进制，不做 JSON 校验。
/// </summary>
/// <remarks>
/// 为什么不借卫月的下载器：卫月的图标缓存**不落盘**（重开游戏全部重下），
/// 而且往它的请求队列里撒几百个任务会把渲染线程拖住（2026-09-18 的卡死教训）。
/// 我们自己下、自己存，卫月要用时只拿现成的。
/// </remarks>
internal static class IconDownloader
{
    private static readonly Lazy<HttpClient> Client = new(() => new HttpClient(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        MaxConnectionsPerServer = 8,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    /// <summary>下载结果：<paramref name="Bytes"/> 非空 = 成功。</summary>
    public readonly record struct Result(byte[]? Bytes, string? ContentType, int Status, string? Error);

    /// <summary>下载一张图标（带 15 秒超时；GitHub 地址会自动加镜像竞速）。</summary>
    public static async Task<Result> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var channels = RepoScanner.BuildChannels(url);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var failures = new List<Result>(channels.Count);
        var tasks = channels.Select(channel => FetchOneAsync(channel, linked.Token)).ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);

            Result result;
            try
            {
                result = await finished.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (result.Bytes is { Length: > 0 })
            {
                try
                {
                    linked.Cancel();
                }
                catch
                {
                    // ignore
                }

                return result;
            }

            failures.Add(result);
        }

        return failures.Count > 0
            ? failures.OrderByDescending(x => x.Status).First()
            : new Result(null, null, 0, "全部线路都失败");
    }

    private static async Task<Result> FetchOneAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("image/*"));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await Client.Value
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new Result(null, null, (int)response.StatusCode, null);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(CancellationToken.None).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            return new Result(bytes, contentType, (int)response.StatusCode, null);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new Result(null, null, 0, "请求超时");
        }
        catch (Exception e)
        {
            return new Result(null, null, 0, e.Message);
        }
    }
}
