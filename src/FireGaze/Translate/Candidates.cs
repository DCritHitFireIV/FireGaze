using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FireGaze.Translate;

/// <summary>一条候选译文（别人提交、还没定下来的）。来自仓库里的 candidates.json。</summary>
internal sealed class CandidateEntry
{
    public string InternalName { get; set; } = string.Empty;

    /// <summary>Name / Punchline / Description。</summary>
    public string Field { get; set; } = string.Empty;

    public string Original { get; set; } = string.Empty;

    public string Translated { get; set; } = string.Empty;

    /// <summary>👍 数（👎 数不下发，也不显示）。</summary>
    public int Votes { get; set; }

    /// <summary>什么时候进来的（超过一个月没人点赞就会被归档，不再出现）。</summary>
    public string FirstSeen { get; set; } = string.Empty;

    /// <summary>对应的 GitHub issue 号（点「看/投票」打开它）。</summary>
    public int Issue { get; set; }

    public string FieldLabel => this.Field switch
    {
        "Name" => "插件名",
        "Punchline" => "一行简介",
        _ => "插件详情",
    };

    /// <summary>按 issue 号拼出可点的地址。</summary>
    public string IssueUrl => this.Issue > 0 ? $"{ContributionsStore.RepoUrl}/issues/{this.Issue}" : ContributionsStore.RepoUrl;

    public string Key => this.InternalName + ":" + this.Field + ":" + this.Translated;

    public bool IsNew => this.Votes <= 0;
}

/// <summary>
/// 候选译文的本地视图：从仓库拉 candidates.json（每周一由工作流更新），
/// 外加「我更喜欢哪一条」的本地偏好（只存本机，不上传、不改词表）。
/// </summary>
internal sealed class CandidatesStore
{
    private const string RemoteUrl = "https://raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/candidates.json";
    private const string MirrorAtmoomen = "https://gh.atmoomen.top/raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/candidates.json";
    private const string MirrorGhProxy = "https://gh-proxy.org/https://raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/candidates.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string configDirectory;
    private readonly Dictionary<string, string> favorites = new(StringComparer.Ordinal);
    private readonly List<CandidateEntry> entries = [];

    public CandidatesStore(string configDirectory)
    {
        this.configDirectory = configDirectory;
        this.LoadCache();
        this.LoadFavorites();
    }

    /// <summary>candidates.json 的本地缓存（配置目录）。</summary>
    public string CachePath => Path.Combine(this.configDirectory, "candidates.json");

    private string FavoritesPath => Path.Combine(this.configDirectory, "candidates-favorites.json");

    public IReadOnlyList<CandidateEntry> All => this.entries;

    public int Count => this.entries.Count;

    /// <summary>还没人点赞的条数（界面上单独分组、置顶）。</summary>
    public int NewCount => this.entries.Count(x => x.IsNew);

    public int FavoriteCount => this.favorites.Count;

    /// <summary>上次拿到候选清单的时间。</summary>
    public DateTime LastLoadedLocal { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// 排好序的候选：**0 赞的排最前**（用户 2026-09-22 定：优先显示「还没人评」的），
    /// 组内按插件名 + 字段名稳定排列；已投票的按赞数从多到少。
    /// </summary>
    public List<CandidateEntry> Sorted()
    {
        var list = new List<CandidateEntry>(this.entries);
        list.Sort((a, b) =>
        {
            if (a.IsNew != b.IsNew)
            {
                return a.IsNew ? -1 : 1;   // 0 赞优先
            }

            if (a.IsNew)
            {
                var byName = string.Compare(a.InternalName, b.InternalName, StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : string.Compare(a.Field, b.Field, StringComparison.Ordinal);
            }

            var byVotes = b.Votes.CompareTo(a.Votes);
            if (byVotes != 0)
            {
                return byVotes;
            }

            var name = string.Compare(a.InternalName, b.InternalName, StringComparison.OrdinalIgnoreCase);
            return name != 0 ? name : string.Compare(a.Field, b.Field, StringComparison.Ordinal);
        });
        return list;
    }

    /// <summary>我是不是选了这一条（本地偏好）。</summary>
    public bool IsFavorite(CandidateEntry entry)
        => this.favorites.TryGetValue(entry.InternalName + ":" + entry.Field, out var picked) &&
           string.Equals(picked, entry.Translated, StringComparison.Ordinal);

    /// <summary>同一个插件的同一个字段，我选的是哪一条（没有就返回 null）。</summary>
    public string? FavoriteOf(CandidateEntry entry)
        => this.favorites.TryGetValue(entry.InternalName + ":" + entry.Field, out var picked) ? picked : null;

    /// <summary>选 / 取消选（只写本机文件）。</summary>
    public void ToggleFavorite(CandidateEntry entry)
    {
        var key = entry.InternalName + ":" + entry.Field;
        if (this.IsFavorite(entry))
        {
            this.favorites.Remove(key);
        }
        else
        {
            this.favorites[key] = entry.Translated;
        }

        this.SaveFavorites();
    }

    /// <summary>从 GitHub 拉最新候选清单（失败依次试镜像），成功后写进配置目录缓存。</summary>
    public async Task<(bool Ok, string Message)> UpdateFromGitHubAsync(CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var errors = new List<string>();
        foreach (var url in new[] { RemoteUrl, MirrorAtmoomen, MirrorGhProxy })
        {
            try
            {
                var text = await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var list = Parse(text);
                if (list is null)
                {
                    errors.Add($"{url}：内容为空或格式不对");
                    continue;
                }

                Directory.CreateDirectory(this.configDirectory);
                await File.WriteAllTextAsync(this.CachePath, text, cancellationToken).ConfigureAwait(false);

                this.entries.Clear();
                this.entries.AddRange(list);
                this.LastLoadedLocal = DateTime.Now;
                this.LastError = null;
                return (true, $"已更新候选清单：{list.Count} 条（{url}）");
            }
            catch (Exception e)
            {
                errors.Add($"{url}：{e.Message}");
            }
        }

        var detail = errors.Count > 0 ? errors[0] : "未知原因";
        this.LastError = detail;
        return (false, $"候选清单更新失败：{detail}");
    }

    /// <summary>解析 candidates.json（元数据键以下划线开头）。</summary>
    public static List<CandidateEntry>? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("candidates", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<CandidateEntry>();
        foreach (var item in array.EnumerateArray())
        {
            var entry = item.Deserialize<CandidateEntry>(JsonOptions);
            if (entry is null || string.IsNullOrWhiteSpace(entry.InternalName) || string.IsNullOrWhiteSpace(entry.Translated))
            {
                continue;
            }

            list.Add(entry);
        }

        return list;
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(this.CachePath))
            {
                return;
            }

            var list = Parse(File.ReadAllText(this.CachePath));
            if (list is { Count: > 0 })
            {
                this.entries.AddRange(list);
                this.LastLoadedLocal = File.GetLastWriteTime(this.CachePath);
            }
        }
        catch (Exception e)
        {
            this.LastError = e.Message;
        }
    }

    private void LoadFavorites()
    {
        try
        {
            if (!File.Exists(this.FavoritesPath))
            {
                return;
            }

            var payload = JsonSerializer.Deserialize<FavoritesFile>(File.ReadAllText(this.FavoritesPath), JsonOptions);
            foreach (var (key, value) in payload?.Favorites ?? [])
            {
                this.favorites[key] = value;
            }
        }
        catch
        {
            // 读不出来就当没选过，不影响别的功能
        }
    }

    private void SaveFavorites()
    {
        try
        {
            Directory.CreateDirectory(this.configDirectory);
            var payload = new FavoritesFile
            {
                Note = "FireGaze 本地偏好：我在候选译文里更喜欢哪一条（只存本机，不上传、不改词表）。",
                Favorites = new Dictionary<string, string>(this.favorites, StringComparer.Ordinal),
            };

            File.WriteAllText(this.FavoritesPath, JsonSerializer.Serialize(payload, JsonOptions), System.Text.Encoding.UTF8);
        }
        catch
        {
            // ignore
        }
    }

    private sealed class FavoritesFile
    {
        public string? Note { get; set; }

        public Dictionary<string, string> Favorites { get; set; } = new(StringComparer.Ordinal);
    }
}
