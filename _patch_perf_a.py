import io

# ============ A. RepoScanner：并发下调 + ETag 条件请求 ============
p = r'C:\everyone\FireGaze\src\FireGaze\RepoAudit\RepoScanner.cs'
s = open(p, encoding='utf-8').read()

def rep(old, new, label, count=1):
    global s
    n = s.count(old)
    print(f'  [{label}] hits={n}')
    assert n == count, label
    s = s.replace(old, new)

rep('''    private const int Concurrency = 16;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(18);

    private sealed record FetchResult(string Url, int Status, string? Error, string? Text, bool Success);''',
'''    /// <summary>同时在查的库数。1000+ 个库时别再往上加：并发高了会跟卫月自己的仓库重载抢网络，游戏会卡。</summary>
    private const int Concurrency = 6;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(18);

    /// <summary>URL → ETag / Last-Modified，用于「没变就跳过内容校验」（304），避免每次全量重下。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> EtagCache = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LastModifiedCache = new(StringComparer.Ordinal);

    private sealed record FetchResult(string Url, int Status, string? Error, string? Text, bool Success, bool NotModified = false);''',
'scanner-fields')

rep('''            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 32,''',
'''            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,''',
'scanner-handler')
