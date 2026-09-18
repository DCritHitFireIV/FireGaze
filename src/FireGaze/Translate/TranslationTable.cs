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

    public bool TryGet(string internalName, out TransEntry entry)
    {
        lock (this.gate)
        {
            return this.table.TryGetValue(internalName, out entry!);
        }
    }

    /// <summary>加载词表；返回是否至少加载到一份非空词表。</summary>
    public bool Load(out string? error)
    {
        error = null;

        var candidates = new List<string>
        {
            Path.Combine(this.configDirectory, "translations.json"),
            Path.Combine(this.pluginDirectory, "translations.json"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var dict = JsonSerializer.Deserialize<Dictionary<string, TransEntry>>(
                    File.ReadAllText(candidate),
                    JsonOptions);

                if (dict is null || dict.Count == 0)
                {
                    continue;
                }

                this.table = new Dictionary<string, TransEntry>(dict, StringComparer.Ordinal);
                this.LoadedFrom = candidate;
                this.LoadedAt = File.GetLastWriteTime(candidate);
                return true;            }
            catch (Exception e)
            {
                error = $"词表加载失败（{candidate}）：{e.Message}";
            }
        }

        return false;
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

        pair.Translated = translated ?? string.Empty;
        pair.Source = "user";
        pair.Review = null;
        if (!string.IsNullOrEmpty(note))
        {
            pair.Review = note;
        }

        return true;
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
                json = JsonSerializer.Serialize(
                    this.table,
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
                var dict = JsonSerializer.Deserialize<Dictionary<string, TransEntry>>(text, JsonOptions);
                if (dict is null || dict.Count == 0)
                {
                    errors.Add($"{url}：内容为空");
                    continue;
                }

                Directory.CreateDirectory(this.configDirectory);
                var target = Path.Combine(this.configDirectory, "translations.json");
                await File.WriteAllTextAsync(target, text, cancellationToken).ConfigureAwait(false);

                this.table = new Dictionary<string, TransEntry>(dict, StringComparer.Ordinal);
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
