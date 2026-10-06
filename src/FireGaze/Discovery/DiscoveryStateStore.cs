using System.Text.Json;
using System.Text.Json.Serialization;
using FireGaze.Internal;

namespace FireGaze.Discovery;

/// <summary>
///     一条待重试的云端上报（点赞 / 加库）。网络失败时先记在这里，下次开页签或定时重试，
///     绝不假装成功（2026-10-06 用户定：不能像旧「喜欢」那样发完即忘）。
/// </summary>
internal sealed class DiscoveryPendingAction
{
    /// <summary><c>like</c> = 点赞；<c>add</c> = 加库推荐。</summary>
    [JsonPropertyName("Type")]
    public string Type { get; set; } = "like";

    [JsonPropertyName("Plugin")]
    public string Plugin { get; set; } = string.Empty;

    /// <summary>点赞那一周（ISO 周标签，如 2026-W41）；add 不用。</summary>
    [JsonPropertyName("Week")]
    public string Week { get; set; } = string.Empty;

    [JsonPropertyName("AtUTC")]
    public long AtUTC { get; set; }
}

/// <summary>
///     插件发现的本机状态：本周点过哪些插件、还没同步成功的上报、投稿过的地址、上次拉到的统计缓存。
///     独立文件（`discovery-state.json`），不进主配置，避免 2000 条规模把配置写胖。
/// </summary>
internal sealed class DiscoveryStateStore
{
    private readonly string path;
    private readonly object gate = new();

    private Dictionary<string, string> likedWeeks = new(StringComparer.Ordinal);
    private List<DiscoveryPendingAction> pending = [];
    private Dictionary<string, long> submittedRepos = new(StringComparer.Ordinal);

    public DiscoveryStateStore(string configDirectory)
    {
        path = Path.Combine(configDirectory, "discovery-state.json");
        Load();
    }

    /// <summary>本机在某一周点过的插件（内部名 → ISO 周标签）。</summary>
    public IReadOnlyDictionary<string, string> LikedWeeks
    {
        get
        {
            lock (gate)
            {
                return new Dictionary<string, string>(likedWeeks, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>本机投过的库链（归一化地址 → 投稿时间）。</summary>
    public IReadOnlyDictionary<string, long> SubmittedRepos
    {
        get
        {
            lock (gate)
            {
                return new Dictionary<string, long>(submittedRepos, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>上次拉到的中继统计（可空；用于离线兜底）。</summary>
    public DiscoveryStats? CachedStats { get; private set; }

    public bool LikedThisWeek(string internalName, string week)
    {
        lock (gate)
        {
            return likedWeeks.TryGetValue(internalName, out var likedWeek)
                   && string.Equals(likedWeek, week, StringComparison.Ordinal);
        }
    }

    /// <summary>记下本机的点赞，并排一条待上报（成功后才移除）。</summary>
    public void MarkLiked(string internalName, string week)
    {
        lock (gate)
        {
            likedWeeks[internalName] = week;
            if (!pending.Any(x => x.Type == "like" && string.Equals(x.Plugin, internalName, StringComparison.Ordinal)))
            {
                pending.Add(new DiscoveryPendingAction
                {
                    Type = "like",
                    Plugin = internalName,
                    Week = week,
                    AtUTC = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
            }

            SaveLocked();
        }
    }

    /// <summary>排一批加库推荐上报（按插件）。</summary>
    public void MarkAdds(IEnumerable<string> internalNames)
    {
        lock (gate)
        {
            var changed = false;
            foreach (var name in internalNames)
            {
                if (string.IsNullOrWhiteSpace(name)
                    || pending.Any(x => x.Type == "add" && string.Equals(x.Plugin, name, StringComparison.Ordinal)))
                {
                    continue;
                }

                pending.Add(new DiscoveryPendingAction
                {
                    Type = "add",
                    Plugin = name,
                    AtUTC = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
                changed = true;
            }

            if (changed)
            {
                SaveLocked();
            }
        }
    }

    /// <summary>取一条待重试（按时间先后）；没有返回 null。</summary>
    public DiscoveryPendingAction? PeekPending()
    {
        lock (gate)
        {
            PruneLocked();
            return pending.Count == 0 ? null : pending[0];
        }
    }

    /// <summary>上报成功后移除（按内容匹配，避免误删新加的）。</summary>
    public void CompletePending(DiscoveryPendingAction action)
    {
        lock (gate)
        {
            pending.RemoveAll(x => x.Type == action.Type
                                   && string.Equals(x.Plugin, action.Plugin, StringComparison.Ordinal));
            SaveLocked();
        }
    }

    /// <summary>有没有这个插件还没同步成功的上报（点赞 / 加库）——界面上标「未同步」。</summary>
    public bool HasPending(string internalName)
    {
        lock (gate)
        {
            return pending.Any(x => string.Equals(x.Plugin, internalName, StringComparison.Ordinal));
        }
    }

    /// <summary>点赞上报成功后移除（按插件；不碰加库那条）。</summary>
    public void CompleteLike(string internalName)
    {
        lock (gate)
        {
            pending.RemoveAll(x => x.Type == "like" && string.Equals(x.Plugin, internalName, StringComparison.Ordinal));
            SaveLocked();
        }
    }

    /// <summary>加库上报成功后移除。</summary>
    public void CompleteAdds(IEnumerable<string> internalNames)
    {
        var names = new HashSet<string>(internalNames, StringComparer.Ordinal);
        lock (gate)
        {
            pending.RemoveAll(x => x.Type == "add" && names.Contains(x.Plugin));
            SaveLocked();
        }
    }

    public bool IsRepoSubmitted(string normalizedURL)
    {
        lock (gate)
        {
            return !string.IsNullOrEmpty(normalizedURL) && submittedRepos.ContainsKey(normalizedURL);
        }
    }

    public void MarkRepoSubmitted(string normalizedURL)
    {
        if (string.IsNullOrEmpty(normalizedURL))
        {
            return;
        }

        lock (gate)
        {
            submittedRepos[normalizedURL] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveLocked();
        }
    }

    public void SaveStats(DiscoveryStats stats)
    {
        lock (gate)
        {
            CachedStats = stats;
            SaveLocked();
        }
    }

    private void PruneLocked()
    {
        // 超过 30 天的陈旧上报没有重试意义（周榜早就翻篇了），直接丢
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
        pending.RemoveAll(x => x.AtUTC < cutoff);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var doc = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(path));
            if (doc is null)
            {
                return;
            }

            likedWeeks = doc.LikedWeeks is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(doc.LikedWeeks, StringComparer.Ordinal);
            pending = doc.Pending ?? [];
            submittedRepos = doc.SubmittedRepos is null
                ? new Dictionary<string, long>(StringComparer.Ordinal)
                : new Dictionary<string, long>(doc.SubmittedRepos, StringComparer.Ordinal);
            if (doc.Stats is not null)
            {
                CachedStats = doc.Stats;
            }
        }
        catch
        {
            // 坏文件当没有：不因此让页签不可用
            likedWeeks = new Dictionary<string, string>(StringComparer.Ordinal);
            pending = [];
            submittedRepos = new Dictionary<string, long>(StringComparer.Ordinal);
        }
    }

    private void SaveLocked()
    {
        try
        {
            var doc = new PersistedState
            {
                LikedWeeks = likedWeeks,
                Pending = pending,
                SubmittedRepos = submittedRepos,
                Stats = CachedStats,
            };
            var json = JsonSerializer.Serialize(doc, JSONOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, json + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch
        {
            // 存不下不影响本次使用；下次再试
        }
    }

    private static readonly JsonSerializerOptions JSONOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class PersistedState
    {
        [JsonPropertyName("LikedWeeks")]
        public Dictionary<string, string>? LikedWeeks { get; set; }

        [JsonPropertyName("Pending")]
        public List<DiscoveryPendingAction>? Pending { get; set; }

        [JsonPropertyName("SubmittedRepos")]
        public Dictionary<string, long>? SubmittedRepos { get; set; }

        [JsonPropertyName("Stats")]
        public DiscoveryStats? Stats { get; set; }
    }
}
