using System.Net;
using System.Net.Http.Headers;

namespace FireGaze.RepoAudit;

/// <summary>一个仓库的体检状态。</summary>
public enum RepoStatus
{
    Unknown = 0,
    Ok = 1,
    Empty = 2,
    Invalid = 3,
    Dead = 4,
    Blocked = 5,
    Unreachable = 6,
}

/// <summary>体检结果条目（列表的一行）。</summary>
public sealed class RepoAuditItem
{
    public string Url { get; init; } = string.Empty;

    public bool IsEnabled { get; set; }

    public int Index { get; set; }

    /// <summary>首次被 FireGaze 记录到的时间（yyyy-MM-dd HH:mm）；null = 安装本插件前就存在，无记录。</summary>
    public string? FirstSeen { get; set; }

    public RepoStatus Status { get; set; } = RepoStatus.Unknown;

    public int HttpStatus { get; set; }

    public string? Note { get; set; }

    public int PluginCount { get; set; }

    public int DroppedCount { get; set; }

    /// <summary>命中的线路（直连 / 镜像）。</summary>
    public string? Channel { get; set; }

    public DateTime CheckedUtc { get; set; }

    /// <summary>值得处理的问题（会被默认勾选）。</summary>
    public bool IsProblem => this.Status
        is RepoStatus.Dead or RepoStatus.Invalid or RepoStatus.Blocked or RepoStatus.Unreachable;

    public bool IsSelectable => this.Status != RepoStatus.Unknown;

    public string StatusText => this.Status switch
    {
        RepoStatus.Ok => "可用",
        RepoStatus.Empty => "空仓库",
        RepoStatus.Invalid => "内容不合规",
        RepoStatus.Dead => "死链",
        RepoStatus.Blocked => "拒绝访问",
        RepoStatus.Unreachable => "连接失败",
        _ => "未检查",
    };
}

/// <summary>扫描进度。</summary>
public readonly record struct ScanProgress(int Done, int Total, int Ok, int Dead, int Invalid, int Unreachable);

/// <summary>
/// 用卫月同款方式体检第三方仓库：
///   HTTP GET（Accept: application/json、no-cache、超时） → 状态码 → 内容契约校验。
/// GitHub 系地址会同时向可用镜像竞速（直连被墙的环境下也能得到正确结论）。
/// </summary>
public static class RepoScanner
{
    private const int Concurrency = 16;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(18);

    private sealed record FetchResult(string Url, int Status, string? Error, string? Text, bool Success);

    private sealed record FetchOutcome(FetchResult? Success, List<FetchResult> Failures);

    /// <summary>
    /// 扫描全部条目（后台线程调用）。每完成一条调用一次 <paramref name="onItemDone"/>，
    /// 进度计数通过 <paramref name="onProgress"/> 上报。
    /// </summary>
    public static async Task ScanAsync(
        IReadOnlyList<RepoAuditItem> items,
        Action<RepoAuditItem>? onItemDone,
        Action<ScanProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 32,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var done = 0;
        var ok = 0;
        var dead = 0;
        var invalid = 0;
        var unreachable = 0;
        var gate = new object();

        var semaphore = new SemaphoreSlim(Concurrency, Concurrency);
        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CheckOneAsync(client, item, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }

            int d, o, de, iv, un;
            lock (gate)
            {
                done++;
                switch (item.Status)
                {
                    case RepoStatus.Ok:
                    case RepoStatus.Empty:
                        ok++;
                        break;
                    case RepoStatus.Dead:
                        dead++;
                        break;
                    case RepoStatus.Blocked:
                    case RepoStatus.Invalid:
                        invalid++;
                        break;
                    default:
                        unreachable++;
                        break;
                }

                d = done;
                o = ok;
                de = dead;
                iv = invalid;
                un = unreachable;
            }

            onItemDone?.Invoke(item);
            onProgress?.Invoke(new ScanProgress(d, items.Count, o, de, iv, un));
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task CheckOneAsync(HttpClient client, RepoAuditItem item, CancellationToken cancellationToken)
    {
        item.CheckedUtc = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(item.Url))
        {
            item.Status = RepoStatus.Invalid;
            item.Note = "空的仓库地址";
            return;
        }

        var outcome = await FetchBestAsync(client, item.Url, cancellationToken).ConfigureAwait(false);

        if (outcome.Success is { } success)
        {
            item.HttpStatus = success.Status;
            item.Channel = DescribeChannel(item.Url, success.Url);

            var check = ManifestCheck.Check(success.Text ?? string.Empty);
            if (!check.CheckerAvailable)
            {
                item.Status = RepoStatus.Unreachable;
                item.Note = "内容校验不可用：" + check.Error;
                return;
            }

            if (!check.Ok)
            {
                item.Status = RepoStatus.Invalid;
                item.Note = "内容不是合法仓库 JSON：" + check.Error;
                return;
            }

            item.PluginCount = check.Count;
            item.DroppedCount = check.Dropped;
            item.Status = check.Count == 0 ? RepoStatus.Empty : RepoStatus.Ok;
            item.Note = check.Count == 0
                ? "仓库里没有任何插件条目"
                : $"{check.Count} 个插件" + (check.Dropped > 0 ? $"，{check.Dropped} 条会被卫月丢弃" : string.Empty);
            return;
        }

        Classify(item, outcome.Failures);
    }

    private static void Classify(RepoAuditItem item, List<FetchResult> failures)
    {
        var statuses = failures.Select(f => f.Status).Where(s => s > 0).ToList();

        if (statuses.Any(s => s is 404 or 410))
        {
            item.Status = RepoStatus.Dead;
            item.HttpStatus = statuses.First(s => s is 404 or 410);
            item.Note = $"HTTP {item.HttpStatus}（链接已失效）";
            return;
        }

        if (statuses.Any(s => s is 401 or 403 or 429))
        {
            item.Status = RepoStatus.Blocked;
            item.HttpStatus = statuses.First(s => s is 401 or 403 or 429);
            item.Note = $"HTTP {item.HttpStatus}（拒绝访问，可能是私有仓库或限流）";
            return;
        }

        if (statuses.Count > 0)
        {
            item.Status = RepoStatus.Unreachable;
            item.HttpStatus = statuses[0];
            item.Note = $"HTTP {item.HttpStatus}";
            return;
        }

        item.Status = RepoStatus.Unreachable;
        var error = failures.FirstOrDefault(f => !string.IsNullOrEmpty(f.Error))?.Error;
        item.Note = string.IsNullOrEmpty(error) ? "连接失败 / 超时" : Shorten(error);
    }

    private static string Shorten(string text)
    {
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return text.Length <= 140 ? text : text[..140] + "…";
    }

    private static string DescribeChannel(string original, string used)
        => string.Equals(original, used, StringComparison.OrdinalIgnoreCase) ? "直连" : "镜像";

    /// <summary>直连 + 镜像一起竞速，取第一个成功的结果。</summary>
    private static async Task<FetchOutcome> FetchBestAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        var channels = BuildChannels(url);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var failures = new List<FetchResult>(3);
        var tasks = channels.Select(c => FetchOneAsync(client, c, linked.Token)).ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);

            FetchResult result;
            try
            {
                result = await finished.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            if (result.Success)
            {
                try
                {
                    linked.Cancel();
                }
                catch
                {
                    // ignore
                }

                return new FetchOutcome(result, []);
            }

            failures.Add(result);
        }

        return new FetchOutcome(null, failures);
    }

    private static async Task<FetchResult> FetchOneAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            var text = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
            return new FetchResult(url, (int)response.StatusCode, null, text, response.IsSuccessStatusCode);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new FetchResult(url, 0, "请求超时", null, false);
        }
        catch (Exception e)
        {
            return new FetchResult(url, 0, e.Message, null, false);
        }
    }

    /// <summary>为 GitHub 系地址追加镜像线路（gh.atmoomen.top 只吃 raw 域名；github.com 走 gh-proxy.org）。</summary>
    private static List<string> BuildChannels(string url)
    {
        var list = new List<string> { url };

        if (url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
        {
            list.Add("https://gh.atmoomen.top/" + url["https://".Length..]);
            list.Add("https://gh-proxy.org/" + url);
        }
        else if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            list.Add("https://gh-proxy.org/" + url);
        }

        return list;
    }
}
