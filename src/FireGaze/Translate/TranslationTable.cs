using System.Net;
using System.Text.Json;

namespace FireGaze.Translate;

/// <summary>一对原文 / 译文。</summary>
public sealed class TransPair
{
    public string Original { get; set; } = string.Empty;

    public string Translated { get; set; } = string.Empty;
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
    private Dictionary<string, TransEntry> table = new(StringComparer.Ordinal);

    public TranslationTable(string configDirectory, string pluginDirectory)
    {
        this.configDirectory = configDirectory;
        this.pluginDirectory = pluginDirectory;
    }

    public int Count => this.table.Count;

    /// <summary>词表来源文件的路径（加载成功后可用）。</summary>
    public string? LoadedFrom { get; private set; }

    public DateTime? LoadedAt { get; private set; }

    public bool TryGet(string internalName, out TransEntry entry) => this.table.TryGetValue(internalName, out entry!);

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
                return true;
            }
            catch (Exception e)
            {
                error = $"词表加载失败（{candidate}）：{e.Message}";
            }
        }

        return false;
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
