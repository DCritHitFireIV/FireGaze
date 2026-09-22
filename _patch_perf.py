import io

# ============ A. RepoScanner ============
p = r'C:\everyone\FireGaze\src\FireGaze\RepoAudit\RepoScanner.cs'
s = open(p, encoding='utf-8').read()

def rep(old, new, label, count=1):
    global s
    n = s.count(old)
    print(f'  [{label}] hits={n}')
    assert n == count, label
    s = s.replace(old, new)

rep('''    private const int Concurrency = 16;''',
'''    /// <summary>同时在查的库数。1000+ 个库时别再往上加：并发高了会和卫月自己的仓库重载抢网络，游戏会卡。</summary>
    private const int Concurrency = 6;''', 'concurrency')

rep('''    private sealed record FetchResult(string Url, int Status, string? Error, string? Text, bool Success);''',
'''    /// <summary>URL → ETag / Last-Modified：下次带上做条件请求，没变就 304，不用重下整份仓库 JSON。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> EtagCache = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LastModifiedCache = new(StringComparer.Ordinal);

    private sealed record FetchResult(string Url, int Status, string? Error, string? Text, bool Success, bool NotModified = false);''',
'fetchresult')

rep('''            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 32,''',
'''            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,''', 'handler-conns')

# ProbeUrlAsync：复用静态 client + 并发闸，别每个图标地址新建一个 HttpClient
rep('''    public static async Task<UrlProbe> ProbeUrlAsync(string url, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 4,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var outcome = await FetchBestAsync(client, url, cancellationToken).ConfigureAwait(false);
        if (outcome.Success is { } ok)
        {
            return new UrlProbe(true, ok.Status, null);
        }

        var best = outcome.Failures.OrderByDescending(x => x.Status).FirstOrDefault();
        return new UrlProbe(false, best?.Status ?? 0, best?.Error);
    }''',
'''    private static readonly SemaphoreSlim ProbeGate = new(4, 4);

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

    public static async Task<UrlProbe> ProbeUrlAsync(string url, CancellationToken cancellationToken)
    {
        await ProbeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var outcome = await FetchBestAsync(ProbeClient.Value, url, cancellationToken).ConfigureAwait(false);
            if (outcome.Success is { } ok)
            {
                // 304 也算“地址活着”
                return new UrlProbe(true, ok.Status == 304 ? 200 : ok.Status, null);
            }

            var best = outcome.Failures.OrderByDescending(x => x.Status).FirstOrDefault();
            return new UrlProbe(false, best?.Status ?? 0, best?.Error);
        }
        finally
        {
            ProbeGate.Release();
        }
    }''', 'probe-client')

# FetchOneAsync：去掉 NoCache，改成条件请求
rep('''            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);

            var text = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
            return new FetchResult(url, (int)response.StatusCode, null, text, response.IsSuccessStatusCode);''',
'''            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // 条件请求：带上上次的 ETag / Last-Modified，没变就是 304（省流量、也省时间）
            if (EtagCache.TryGetValue(url, out var etag))
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            if (LastModifiedCache.TryGetValue(url, out var lastModified))
            {
                request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
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

            if (response.Headers.ETag is { } newEtag)
            {
                EtagCache[url] = newEtag.ToString();
            }

            if (response.Content.Headers.LastModified is { } modified)
            {
                LastModifiedCache[url] = modified.ToString("R");
            }

            var text = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
            return new FetchResult(url, (int)response.StatusCode, null, text, response.IsSuccessStatusCode);''',
'conditional-get')

# CheckOneAsync：304 就保留上次结论
rep('''        if (outcome.Success is { } success)
        {
            item.HttpStatus = success.Status;
            item.Channel = DescribeChannel(item.Url, success.Url);

            var check = ManifestCheck.Check(success.Text ?? string.Empty);''',
'''        if (outcome.Success is { } success)
        {
            item.HttpStatus = success.Status;
            item.Channel = DescribeChannel(item.Url, success.Url);

            if (success.NotModified)
            {
                // 仓库内容没变：保留上次的结论，不做重复校验
                return;
            }

            var check = ManifestCheck.Check(success.Text ?? string.Empty);''',
'304-skip')

open(p, 'w', encoding='utf-8', newline='\n').write(s)
print('RepoScanner patched')

# ============ B. RepoAuditTab：行裁剪 + 结果缓存 ============
p = r'C:\everyone\FireGaze\src\FireGaze\UI\RepoAuditTab.cs'
s = open(p, encoding='utf-8').read()

rep('''    // ---------------- 图标检查 / 下载（先查，再由用户决定下不下） ----------------''',
'''    // ---------------- 每帧缓存（1000+ 个库时别每帧重算） ----------------
    private List<RepoAuditItem> snapshotCache = [];
    private bool snapshotDirty = true;
    private DateTime snapshotNextAllowed = DateTime.MinValue;
    private bool installedCountsDirty = true;

    // ---------------- 图标检查 / 下载（先查，再由用户决定下不下） ----------------''',
'tab-cache-fields')

# Filtered：加缓存
rep('''    private List<RepoAuditItem> Filtered()
    {
        IEnumerable<RepoAuditItem> query = this.filter switch''',
'''    private List<RepoAuditItem> Filtered()
    {
        // 缓存：筛选 / 排序 / 数据没变就不重算（体检进行中每 250ms 最多重算一次）
        var now = DateTime.Now;
        if (!this.snapshotDirty && now < this.snapshotNextAllowed)
        {
            return this.snapshotCache;
        }

        IEnumerable<RepoAuditItem> query = this.filter switch''',
'filtered-head')

rep('''        return this.SortItems(query);
    }''',
'''        var result = this.SortItems(query);
        this.snapshotCache = result;
        this.snapshotDirty = false;
        this.snapshotNextAllowed = now.AddMilliseconds(this.scanning ? 250 : 0);
        return result;
    }''', 'filtered-tail')

# 筛选切换 / 搜索变化 / 排序变化 → 置脏
rep('''    private void FilterRadio(string key, string label)
    {
        ImGui.SameLine();
        if (ImGui.RadioButton($"{label}###Filter-{key}", this.filter == key))
        {
            this.filter = key;
        }
    }''',
'''    private void FilterRadio(string key, string label)
    {
        ImGui.SameLine();
        if (ImGui.RadioButton($"{label}###Filter-{key}", this.filter == key))
        {
            this.filter = key;
            this.snapshotDirty = true;
        }
    }''', 'filter-dirty')

rep('''        ImGui.InputTextWithHint("###RepoSearch", "搜索仓库地址…", ref this.search, 128);''',
'''        if (ImGui.InputTextWithHint("###RepoSearch", "搜索仓库地址…", ref this.search, 128))
        {
            this.snapshotDirty = true;
        }''', 'search-dirty')

rep('''                    this.sortDescending = spec.SortDirection == ImGuiSortDirection.Descending;''',
'''                    this.sortDescending = spec.SortDirection == ImGuiSortDirection.Descending;
                    this.snapshotDirty = true;''', 'sort-dirty')

# 列表 / 扫描变化 → 置脏
rep('''        this.RecomputeCounters();
        this.listBuilt = true;''',
'''        this.RecomputeCounters();
        this.listBuilt = true;
        this.snapshotDirty = true;
        this.installedCountsDirty = true;''', 'ensurelist-dirty')

rep('''            this.listBuilt = true;
        }''',
'''            this.listBuilt = true;
        }

        this.snapshotDirty = true;
        this.installedCountsDirty = true;''', 'startscan-dirty')

rep('''        this.installedIndex = InstalledPluginsIndex.Build();
        this.installedIndexStale = false;''',
'''        this.installedIndex = InstalledPluginsIndex.Build();
        this.installedIndexStale = false;
        this.installedCountsDirty = true;
        this.snapshotDirty = true;''', 'index-dirty')

# FillInstalledCounts：只在脏时算（原来每帧对 1000+ 个 URL 做归一化）
rep('''    private void FillInstalledCounts()
    {
        var index = this.installedIndex;''',
'''    private void FillInstalledCounts()
    {
        if (!this.installedCountsDirty)
        {
            return;
        }

        this.installedCountsDirty = false;
        var index = this.installedIndex;''', 'fill-guard')

rep('''                    item.InstalledPlugins = [];
                    item.InstalledCount = -1;
                }
            }
        }
    }''',
'''                    item.InstalledPlugins = [];
                    item.InstalledCount = -1;
                }
            }
        }

        this.snapshotDirty = true;
    }''', 'fill-tail')

# 表格：加行裁剪
rep('''            foreach (var item in snapshot)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();''',
'''            // 行裁剪：1000+ 个库时只画看得见的那几行（否则每帧几千个 ImGui 项，必卡）
            var clipper = new ImGuiListClipper();
            clipper.Begin(snapshot.Count);

            while (clipper.Step())
            {
                for (var rowIndex = clipper.DisplayStart; rowIndex < clipper.DisplayEnd; rowIndex++)
                {
                    var item = snapshot[rowIndex];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();''', 'clipper-head')

open(p, 'w', encoding='utf-8', newline='\n').write(s)
print('RepoAuditTab patched (part 1)')
