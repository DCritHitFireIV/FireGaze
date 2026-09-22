using System.Net;
using System.Net.Http.Headers;

namespace FireGaze.RepoAudit;

/// <summary>
///     一个仓库的体检状态。
/// </summary>
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

/// <summary>
///     体检结果条目（列表的一行）。
/// </summary>
public sealed class RepoAuditItem
{
    public string URL { get; init; } = string.Empty;

    /// <summary>
    ///     归一化后的仓库地址（只算一次，供「已安装」匹配用，避免每帧对上千个 URL 重复解析）。
    /// </summary>
    public string NormalizedURL { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public int Index { get; set; }

    /// <summary>
    ///     首次被 FireGaze 记录到的时间（yyyy-MM-dd HH:mm）；null = 安装本插件前就存在，无记录。
    /// </summary>
    public string? FirstSeen { get; set; }

    public RepoStatus Status { get; set; } = RepoStatus.Unknown;

    public int HTTPStatus { get; set; }

    public string? Note { get; set; }

    public int PluginCount { get; set; }

    /// <summary>
    ///     本机从这条库链装的插件数；<b>-1 = 本机插件数据不可用</b>（界面必须显示 `—`，不能当 0）。
    /// </summary>
    internal int InstalledCount { get; set; } = -1;

    /// <summary>
    ///     本机从这条库链装的插件（按名字排序；无可用时为空）。
    /// </summary>
    internal List<InstalledPluginEntry> InstalledPlugins { get; set; } = [];

    public int DroppedCount { get; set; }

    /// <summary>
    ///     命中的线路（直连 / 镜像）。
    /// </summary>
    public string? Channel { get; set; }

    public DateTime CheckedUTC { get; set; }

    /// <summary>
    ///     本次扫描中该仓库命中 304（内容未变，结论沿用上次）——只用于统计与日志。
    /// </summary>
    public bool NotModifiedThisRun { get; set; }

    /// <summary>
    ///     值得处理的问题（会出现在「有问题的」筛选里）。
    /// </summary>
    public bool IsProblem => Status
        is RepoStatus.Dead or RepoStatus.Invalid or RepoStatus.Blocked or RepoStatus.Unreachable;

    /// <summary>
    ///     会被默认勾选的项：只勾「死链 + 内容不合规」——连接失败可能是网络插曲，不默认纳入。
    /// </summary>
    public bool IsAutoSelected => Status is RepoStatus.Dead or RepoStatus.Invalid;

    public bool IsSelectable => Status != RepoStatus.Unknown;

    public string StatusText => Status switch
    {
        RepoStatus.Ok => "可用",
        RepoStatus.Empty => "空仓库",
        RepoStatus.Invalid => "链接不合规",
        RepoStatus.Dead => "死链",
        RepoStatus.Blocked => "拒绝访问",
        RepoStatus.Unreachable => "连接失败",
        _ => "未检查",
    };
}

/// <summary>
///     扫描进度。
/// </summary>
public readonly record struct ScanProgress(
    int Done,
    int Total,
    int Ok,
    int Dead,
    int Invalid,
    int Blocked,
    int Unreachable,
    int NotModified = 0);

/// <summary>
///     用卫月同款方式体检第三方仓库：
///       HTTP GET（Accept: application/json、no-cache、超时） → 状态码 → 内容契约校验。
///     GitHub 系地址会同时向可用镜像竞速（直连被墙的环境下也能得到正确结论）。
/// </summary>
public static class RepoScanner
{
    /// <summary>
    ///     同时在查的库数。1000+ 个库时别再往上加：并发高了会和卫月自己的仓库重载抢网络，游戏会卡。
    /// </summary>
    private const int Concurrency = 6;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(18);

    /// <summary>
    ///     URL → 上一次的结论（连同 ETag / Last-Modified）。
    ///     条件请求拿到 304 时用它**恢复上次状态**——否则重新体检时「内容没变」会被当成「未检查」（2026-09-18 用户报的「状态全变未检查」）。
    /// </summary>
    private sealed record CachedOutcome(
        string? ETag,
        string? LastModified,
        RepoStatus Status,
        string? Note,
        int HTTPStatus,
        int PluginCount,
        int DroppedCount);

    /// <summary>
    ///     URL → ETag / Last-Modified / 上次结论：下次带上做条件请求，没变就 304，不用重下整份仓库 JSON。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedOutcome> OutcomeCache = new(StringComparer.Ordinal);

    private sealed record FetchResult(
        string URL,
        int Status,
        string? Error,
        string? Text,
        bool Success,
        bool NotModified = false,
        string? ETag = null,
        string? LastModified = null);

    private sealed record FetchOutcome(FetchResult? Success, List<FetchResult> Failures);

    /// <summary>
    ///     图标体检用的探测结果：<paramref name="Status"/> 0 = 网络层失败。
    /// </summary>
    public readonly record struct URLProbe(bool Ok, int Status, string? Error);

    /// <summary>
    ///     探一次外部地址（图标体检用）：复用体检的多线路竞速与超时策略，只关心“通 / 不通 / 状态码”。
    /// </summary>
    private static readonly SemaphoreSlim ProbeGate = new(4, 4);

    private static readonly Lazy<HttpClient> ProbeClient = new(() => new HttpClient(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 4,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    public static async Task<URLProbe> ProbeURLAsync(string url, CancellationToken cancellationToken)
    {
        await ProbeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await FetchBestAsync(ProbeClient.Value, url, cancellationToken).ConfigureAwait(false);
            if (outcome.Success is { } ok)
            {
                // 304 也算“地址活着”
                return new URLProbe(true, ok.Status == 304 ? 200 : ok.Status, null);
            }

            var best = outcome.Failures.OrderByDescending(x => x.Status).FirstOrDefault();
            return new URLProbe(false, best?.Status ?? 0, best?.Error);
        }
        finally
        {
            ProbeGate.Release();
        }
    }

    /// <summary>
    ///     扫描全部条目（后台线程调用）。每完成一条调用一次 <paramref name="onItemDone"/>，
    ///     进度计数通过 <paramref name="onProgress"/> 上报。
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
            MaxConnectionsPerServer = 8,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var done = 0;
        var ok = 0;
        var dead = 0;
        var invalid = 0;
        var blocked = 0;
        var unreachable = 0;
        var notModified = 0;
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

            int d, o, de, iv, bl, un, nm;
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
                        blocked++;
                        break;
                    case RepoStatus.Invalid:
                        invalid++;
                        break;
                    case RepoStatus.Unknown:
                        break;   // 理论上不会发生：304 会恢复上次结论（见 CachedOutcome）
                    default:
                        unreachable++;
                        break;
                }

                if (item.NotModifiedThisRun)
                {
                    notModified++;
                }

                d = done;
                o = ok;
                de = dead;
                iv = invalid;
                bl = blocked;
                un = unreachable;
                nm = notModified;
            }

            onItemDone?.Invoke(item);
            onProgress?.Invoke(new ScanProgress(d, items.Count, o, de, iv, bl, un, nm));
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task CheckOneAsync(HttpClient client, RepoAuditItem item, CancellationToken cancellationToken)
    {
        item.CheckedUTC = DateTime.UtcNow;
        item.NotModifiedThisRun = false;

        if (string.IsNullOrWhiteSpace(item.URL))
        {
            item.Status = RepoStatus.Invalid;
            item.Note = "空的仓库地址";
            return;
        }

        var outcome = await FetchBestAsync(client, item.URL, cancellationToken).ConfigureAwait(false);

        if (outcome.Success is { } success)
        {
            item.HTTPStatus = success.Status;
            item.Channel = DescribeChannel(item.URL, success.URL);

            if (success.NotModified)
            {
                // 内容没变：恢复上次的结论（状态 / 备注 / 插件数），不要留下「未检查」
                item.NotModifiedThisRun = true;
                if (OutcomeCache.TryGetValue(success.URL, out var cached))
                {
                    item.Status = cached.Status;
                    item.Note = cached.Note;
                    item.HTTPStatus = cached.HTTPStatus;
                    item.PluginCount = cached.PluginCount;
                    item.DroppedCount = cached.DroppedCount;
                }
                else
                {
                    item.Status = RepoStatus.Ok;
                    item.Note = "内容未变（304）";
                }

                return;
            }

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
                Remember(success, item);
                return;
            }

            item.PluginCount = check.Count;
            item.DroppedCount = check.Dropped;
            item.Status = check.Count == 0 ? RepoStatus.Empty : RepoStatus.Ok;
            item.Note = check.Count == 0
                ? "仓库里没有任何插件条目"
                : $"{check.Count} 个插件" + (check.Dropped > 0 ? $"，{check.Dropped} 条会被卫月丢弃" : string.Empty);
            Remember(success, item);
            return;
        }

        Classify(item, outcome.Failures);

        // 记下这次结论：条件请求下次带上 ETag，拿到 304 就能原样恢复
        static void Remember(FetchResult success, RepoAuditItem item)
        {
            if (string.IsNullOrEmpty(success.ETag) && string.IsNullOrEmpty(success.LastModified))
            {
                return;
            }

            OutcomeCache[success.URL] = new CachedOutcome(
                success.ETag,
                success.LastModified,
                item.Status,
                item.Note,
                item.HTTPStatus,
                item.PluginCount,
                item.DroppedCount);
        }
    }

    private static void Classify(RepoAuditItem item, List<FetchResult> failures)
    {
        var statuses = failures.Select(f => f.Status).Where(s => s > 0).ToList();

        if (statuses.Any(s => s is 404 or 410))
        {
            item.Status = RepoStatus.Dead;
            item.HTTPStatus = statuses.First(s => s is 404 or 410);
            item.Note = $"HTTP {item.HTTPStatus}（链接已失效）";
            return;
        }

        if (statuses.Any(s => s is 401 or 403 or 429))
        {
            item.Status = RepoStatus.Blocked;
            item.HTTPStatus = statuses.First(s => s is 401 or 403 or 429);
            item.Note = $"HTTP {item.HTTPStatus}（拒绝访问，可能是私有仓库或限流）";
            return;
        }

        if (statuses.Count > 0)
        {
            item.Status = RepoStatus.Unreachable;
            item.HTTPStatus = statuses[0];
            item.Note = $"HTTP {item.HTTPStatus}";
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

    /// <summary>
    ///     直连 + 镜像一起竞速，取第一个成功的结果。
    /// </summary>
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

            // 条件请求：带上上次的 ETag / Last-Modified，没变就是 304（省流量、也省时间）
            if (OutcomeCache.TryGetValue(url, out var cached))
            {
                if (!string.IsNullOrEmpty(cached.ETag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
                }

                if (!string.IsNullOrEmpty(cached.LastModified))
                {
                    request.Headers.TryAddWithoutValidation("If-Modified-Since", cached.LastModified);
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new FetchResult(url, 304, null, null, true, true);
            }

            var text = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
            return new FetchResult(
                url,
                (int)response.StatusCode,
                null,
                text,
                response.IsSuccessStatusCode,
                false,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R"));
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

    /// <summary>
    ///     为 GitHub 系地址追加镜像线路（gh.atmoomen.top 只吃 raw 域名；github.com 走 gh-proxy.org）。
    /// </summary>
    internal static List<string> BuildChannels(string url)
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
