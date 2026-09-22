using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace FireGaze.Translate;

/// <summary>一对原文 / 译文。</summary>
public sealed class TransPair
{
    public string Original { get; set; } = string.Empty;

    public string Translated { get; set; } = string.Empty;

    /// <summary>
    /// 译文的来源：<c>user</c>（玩家提交，永远优先）/ <c>ai</c>（机器翻译）/ 空（老词表，按机器译对待）。
    /// 机器翻译永不覆盖玩家提交的译文。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>原文改过、译文还没跟着改时由维护脚本写上说明（玩家提交的重合部分会保留，所以这里也可能有内容）。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Review { get; set; }

    /// <summary>是否由玩家提交（大小写不敏感）。</summary>
    [JsonIgnore]
    public bool IsUserSource => string.Equals(this.Source, "user", StringComparison.OrdinalIgnoreCase);

    /// <summary>有没有译文。</summary>
    [JsonIgnore]
    public bool HasTranslation => !string.IsNullOrWhiteSpace(this.Translated);
}

/// <summary>一个插件在词表里的三个字段（Name 允许缺省）。</summary>
public sealed class TransEntry
{
    public TransPair? Name { get; set; }

    public TransPair? Punchline { get; set; }

    public TransPair? Description { get; set; }
}

/// <summary>
/// 简介汉化词表：优先用用户从 GitHub 更新下来的那份（插件配置目录），否则用插件包里自带的。
/// </summary>
public sealed class TranslationTable
{
    private const string RemoteUrl = "https://raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/translations.json";
    private const string MirrorAtmoomen = "https://gh.atmoomen.top/raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/translations.json";
    private const string MirrorGhProxy = "https://gh-proxy.org/https://raw.githubusercontent.com/DCritHitFireIV/FireGaze/main/translations.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string configDirectory;
    private readonly string pluginDirectory;
    private readonly object gate = new();
    private Dictionary<string, TransEntry> table = new(StringComparer.Ordinal);

    /// <summary>词表文件里的元数据键（以下划线开口的键都不当插件条目）。</summary>
    private const string MetaKey = "_meta";

    public TranslationTable(string configDirectory, string pluginDirectory)
    {
        this.configDirectory = configDirectory;
        this.pluginDirectory = pluginDirectory;
    }

    public int Count => this.table.Count;

    /// <summary>有多少条记录里有「原文改过、译文还没跟上」的字段（上游改动提醒用）。</summary>
    public int ReviewCount
    {
        get
        {
            lock (this.gate)
            {
                var count = 0;
                foreach (var entry in this.table.Values)
                {
                    if (entry.Name?.Review is not null ||
                        entry.Punchline?.Review is not null ||
                        entry.Description?.Review is not null)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    /// <summary>按顺序枚举词表（写操作与枚举都走同一把锁，避免后台读取时撞上写入）。</summary>
    public IEnumerable<KeyValuePair<string, TransEntry>> Enumerate()
    {
        lock (this.gate)
        {
            foreach (var pair in this.table)
            {
                yield return pair;
            }
        }
    }

    /// <summary>词表来源文件的路径（加载成功后可用）。</summary>
    public string? LoadedFrom { get; private set; }

    public DateTime? LoadedAt { get; private set; }

    /// <summary>
    /// 词表自身的维护日期（工作流跑的那天，存在文件的 <c>_meta.updatedAt</c> 里），
    /// 没有就是 null（老词表）。
    /// </summary>
    public string? MaintainedAt { get; private set; }

    public bool TryGet(string internalName, out TransEntry entry)
    {
        lock (this.gate)
        {
            return this.table.TryGetValue(internalName, out entry!);
        }
    }

    /// <summary>加载词表；返回是否至少加载到一份非空词表。</summary>
    /// <summary>
    /// 加载词表：配置目录（用户自己存过 / 自动更新下来的）与插件包自带的，
    /// **取「维护日期更新」的那份**；日期相同则优先配置目录（可能有玩家本地改动）。
    ///
    /// 之前的规则是配置目录无脑优先，结果：玩家只要更新过一次词表，
    /// 插件升级时包里带的新词表（比如补了一行简介、新插件）就永远被旧副本压住。
    /// </summary>
    public bool Load(out string? error)
    {
        error = null;

        (string Path, DateTime When, string? MaintainedAt, Dictionary<string, TransEntry> Table) best = default;
        var found = false;

        // 先配置目录、后插件包：日期相同时前面（配置目录）优先
        foreach (var candidate in new[]
                 {
                     Path.Combine(this.configDirectory, "translations.json"),
                     Path.Combine(this.pluginDirectory, "translations.json"),
                 })
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var dict = ParseTable(File.ReadAllText(candidate), out var maintainedAt);
                if (dict is null || dict.Count == 0)
                {
                    continue;
                }

                var when = DateTime.TryParse(maintainedAt, out var maintained)
                    ? maintained
                    : File.GetLastWriteTime(candidate);

                if (!found || when > best.When)
                {
                    best = (candidate, when, maintainedAt, dict);
                    found = true;
                }
            }
            catch (Exception e)
            {
                error = $"词表加载失败（{candidate}）：{e.Message}";
            }
        }

        if (!found || best.Path is null || best.Table is null)
        {
            return false;
        }

        this.table = best.Table;
        this.MaintainedAt = best.MaintainedAt;
        this.LoadedFrom = best.Path;
        this.LoadedAt = File.GetLastWriteTime(best.Path);
        return true;
    }

    /// <summary>
    /// 记下玩家为某个插件提交的译文：写入后该字段的来源变成 <c>user</c>，
    /// 机器翻译从此不再覆盖它（除非上游原文又变了）。
    /// </summary>
    /// <param name="internalName">插件内部名（词表键）。</param>
    /// <param name="field">Name / Punchline / Description。</param>
    /// <param name="original">提交时的原文（与词表里对不上就不写，避免错位）。</param>
    /// <param name="translated">玩家译文（空字符串 = 清除译文）。</param>
    /// <param name="note">可选备注。</param>
    public bool MarkUserTranslation(string internalName, string field, string original, string translated, string? note = null)
    {
        if (string.IsNullOrWhiteSpace(internalName) || string.IsNullOrWhiteSpace(field))
        {
            return false;
        }

        lock (this.gate)
        {
            return this.MarkUserTranslationCore(internalName, field, original, translated, note);
        }
    }

    private bool MarkUserTranslationCore(string internalName, string field, string original, string translated, string? note)
    {
        if (string.IsNullOrWhiteSpace(internalName) || string.IsNullOrWhiteSpace(field))
        {
            return false;
        }

        if (!this.table.TryGetValue(internalName, out var entry))
        {
            entry = new TransEntry();
            this.table[internalName] = entry;
        }

        // 词表里没有这一条时，用提交时带来的原文建一条（第一次贡献的插件）
        var pair = field.ToLowerInvariant() switch
        {
            "name" => entry.Name ??= new TransPair { Original = original },
            "punchline" => entry.Punchline ??= new TransPair { Original = original },
            "description" => entry.Description ??= new TransPair { Original = original },
            _ => null,
        };

        if (pair is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(pair.Original) &&
            !string.IsNullOrWhiteSpace(original) &&
            !string.Equals(pair.Original, original, StringComparison.Ordinal))
        {
            return false;   // 原文对不上：宁可不写，也不要错位
        }

        // 上游本来就空着、玩家自己补的：把原文置空，译文照写
        if (string.IsNullOrWhiteSpace(pair.Original) && string.IsNullOrWhiteSpace(original))
        {
            pair.Original = string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(pair.Original))
        {
            pair.Original = original;
        }

        pair.Translated = translated ?? string.Empty;
        pair.Source = "user";
        pair.Review = null;
        if (!string.IsNullOrEmpty(note))
        {
            pair.Review = note;
        }

        return true;
    }

    /// <summary>读某个字段现在的译文与来源（界面记录「改之前是什么」用）。</summary>
    public (string Translated, string? Source) GetTranslation(string internalName, string field)
    {
        lock (this.gate)
        {
            if (!this.table.TryGetValue(internalName, out var entry))
            {
                return (string.Empty, null);
            }

            var pair = field.ToLowerInvariant() switch
            {
                "name" => entry.Name,
                "punchline" => entry.Punchline,
                _ => entry.Description,
            };

            return pair is null ? (string.Empty, null) : (pair.Translated, pair.Source);
        }
    }

    /// <summary>直接写回一个字段（删掉某条贡献时恢复成改之前的样子）。</summary>
    public bool SetTranslation(string internalName, string field, string translated, string? source)
    {
        lock (this.gate)
        {
            if (!this.table.TryGetValue(internalName, out var entry))
            {
                entry = new TransEntry();
                this.table[internalName] = entry;
            }

            var pair = field.ToLowerInvariant() switch
            {
                "name" => entry.Name ??= new TransPair(),
                "punchline" => entry.Punchline ??= new TransPair(),
                "description" => entry.Description ??= new TransPair(),
                _ => null,
            };

            if (pair is null)
            {
                return false;
            }

            pair.Translated = translated ?? string.Empty;
            pair.Source = string.IsNullOrEmpty(source) ? null : source;
            pair.Review = null;
            return true;
        }
    }

    /// <summary>清掉某个插件的「待复核」标记（原文改过、但玩家已重新提交时用）。</summary>
    public void ClearReview(string internalName, string field)
    {
        lock (this.gate)
        {
            this.ClearReviewCore(internalName, field);
        }
    }

    private void ClearReviewCore(string internalName, string field)
    {
        if (!this.table.TryGetValue(internalName, out var entry))
        {
            return;
        }

        var pair = field.ToLowerInvariant() switch
        {
            "name" => entry.Name,
            "punchline" => entry.Punchline,
            "description" => entry.Description,
            _ => null,
        };

        if (pair is not null)
        {
            pair.Review = null;
        }
    }

    /// <summary>把当前内存里的词表写回配置目录（贡献提交后调用，让改动能落盘）。</summary>
    public bool SaveToConfigDirectory(out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(this.configDirectory);
            var target = Path.Combine(this.configDirectory, "translations.json");
            string json;
            lock (this.gate)
            {
                // 保住 _meta（词表维护日期）：玩家改几条译文不应该抹掉它
                var payload = new Dictionary<string, object?>(this.table.Count + 1, StringComparer.Ordinal);
                if (!string.IsNullOrEmpty(this.MaintainedAt))
                {
                    payload[MetaKey] = new Dictionary<string, string> { ["updatedAt"] = this.MaintainedAt! };
                }

                foreach (var (key, value) in this.table)
                {
                    payload[key] = value;
                }

                json = JsonSerializer.Serialize(
                    payload,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,          // 1 空格缩进与仓库里的 translations.json 一致
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    });
            }

            File.WriteAllText(target, json + Environment.NewLine, System.Text.Encoding.UTF8);
            this.LoadedFrom = target;
            this.LoadedAt = DateTime.Now;
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>解析一份词表：以下划线开口的键走元数据（目前只有 <c>_meta.updatedAt</c>），其余当插件条目。</summary>
    private static Dictionary<string, TransEntry>? ParseTable(string text, out string? maintainedAt)
    {
        maintainedAt = null;
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text, JsonOptions);
        if (raw is null)
        {
            return null;
        }

        var result = new Dictionary<string, TransEntry>(StringComparer.Ordinal);
        foreach (var (key, value) in raw)
        {
            if (key.StartsWith('_'))
            {
                if (key == MetaKey && value.ValueKind == JsonValueKind.Object &&
                    value.TryGetProperty("updatedAt", out var at) && at.ValueKind == JsonValueKind.String)
                {
                    maintainedAt = at.GetString();
                }

                continue;
            }

            var entry = value.Deserialize<TransEntry>(JsonOptions);
            if (entry is not null)
            {
                result[key] = entry;
            }
        }

        return result;
    }

    /// <summary>从 GitHub 拉取最新词表（失败会依次尝试镜像），成功后写入插件配置目录。</summary>
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
                var dict = ParseTable(text, out var maintainedAt);
                if (dict is null || dict.Count == 0)
                {
                    errors.Add($"{url}：内容为空");
                    continue;
                }

                // 防倒退：GitHub 上那份比本机的还旧（比如本地刚跟过一轮新的、还没推上去）
                // 就不要拿它覆盖本机，否则玩家会莫名其妙 “更新” 成更少的条数。
                var remoteDate = DateTime.TryParse(maintainedAt, out var remote) ? remote : (DateTime?)null;
                var localDate = DateTime.TryParse(this.MaintainedAt, out var local) ? local : (DateTime?)null;
                if (localDate is not null && (remoteDate is null || remoteDate < localDate))
                {
                    var remoteLabel = remoteDate is null ? "没有维护日期" : $"{remoteDate:yyyy-MM-dd}";
                    return (false, $"GitHub 上那份词表更旧（本机 {localDate:yyyy-MM-dd} / 远端 {remoteLabel}），没有替换。"
                                    + "要强制用远端那份，删掉配置目录里的 translations.json 再更新。");
                }

                Directory.CreateDirectory(this.configDirectory);
                var target = Path.Combine(this.configDirectory, "translations.json");
                await File.WriteAllTextAsync(target, text, cancellationToken).ConfigureAwait(false);

                this.table = dict;
                this.MaintainedAt = maintainedAt;
                this.LoadedFrom = target;
                this.LoadedAt = DateTime.Now;
                return (true, $"已更新词表：{dict.Count} 条（{url}）");
            }
            catch (Exception e)
            {
                errors.Add($"{url}：{e.Message}");
            }
        }

        var detail = errors.Count > 0 ? errors[0] : "未知原因";
        return (false, $"更新失败：{detail}（共尝试 {errors.Count} 个线路）");
    }
}
