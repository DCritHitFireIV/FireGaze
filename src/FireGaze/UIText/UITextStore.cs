using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FireGaze.UIText;

/// <summary>
///     本机内部文本包的存放（<c>&lt;配置目录&gt;/uitrans/&lt;内部名&gt;.json</c>），
///     以及与「桌面翻译工具」JSON 的相互转换（那个格式社区里已经有人在用，导入导出都兼容）。
/// </summary>
internal sealed class UITextStore
{
    private const string ExportFormatName = "FireGaze UIText";

    private readonly object gate = new();
    private readonly string? mirrorDirectory;

    /// <summary>包内容的缓存（按文件写入时间 + 长度失效）：列表每 5 秒、投稿前都会读，不能每次都读盘解析（2026-10-03 B-03/C-08）。</summary>
    private readonly Dictionary<string, (DateTime WriteUtc, long Length, UITextPack Pack)> packCache = new(StringComparer.OrdinalIgnoreCase);

    public UITextStore(string configDirectory, string? mirrorDirectory = null)
    {
        this.DirectoryPath = Path.Combine(configDirectory, "uitrans");
        this.mirrorDirectory = mirrorDirectory;
    }

    /// <summary>
    ///     包目录。
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    ///     某个插件有没有本地包（配置目录里没有、持久镜像里有的话会先恢复回来）。
    /// </summary>
    public bool Exists(string internalName)
    {
        this.EnsureRestored(internalName);
        return File.Exists(this.PathOf(internalName));
    }

    /// <summary>
    ///     列出本地有包的插件内部名（含只存在于持久镜像里的）。
    /// </summary>
    public IReadOnlyList<string> List()
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in new string?[] { this.DirectoryPath, this.mirrorDirectory })
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in Directory.GetFiles(directory, "*.json"))
                {
                    names.Add(Path.GetFileNameWithoutExtension(file));
                }
            }

            return names.Where(name => !string.IsNullOrEmpty(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;
        }
        catch (Exception)
        {
            Plugin.Log?.Warning("[内部文本] 列包目录失败：" + this.DirectoryPath);
            return [];
        }
    }

    /// <summary>
    ///     读一个包；没有 / 读不出来时返回空包（失败会写日志）。
    /// </summary>
    public UITextPack Load(string internalName)
    {
        var path = this.PathOf(internalName);
        try
        {
            this.EnsureRestored(internalName);
            if (!File.Exists(path))
            {
                return new UITextPack { Meta = { Source = "local" } };
            }

            var json = File.ReadAllText(path, Encoding.UTF8);
            var pack = UITextPack.FromJSON(json, out var error);
            if (pack is null)
            {
                Plugin.Log?.Warning($"[内部文本] 包读不出来（{internalName}）：{error}");
                return new UITextPack { Meta = { Source = "local" } };
            }

            pack.Meta.Source ??= "local";
            return pack;
        }
        catch (Exception)
        {
            Plugin.Log?.Warning($"[内部文本] 读包失败：{path}");
            return new UITextPack { Meta = { Source = "local" } };
        }
    }

    /// <summary>
    ///     读一个包；文件没变时直接复用上次解析出的对象（只给只读用途：列表行摘要 / 投稿体检）。
    ///     要改包（编辑器、翻译流程）一律走 <see cref="Load" /> 拿独立副本，避免共享实例被改。
    /// </summary>
    public UITextPack LoadCached(string internalName)
    {
        var path = this.PathOf(internalName);
        try
        {
            this.EnsureRestored(internalName);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new UITextPack { Meta = { Source = "local" } };
            }

            lock (this.packCache)
            {
                if (this.packCache.TryGetValue(internalName, out var cached)
                    && cached.WriteUtc == info.LastWriteTimeUtc
                    && cached.Length == info.Length)
                {
                    return cached.Pack;
                }
            }

            var pack = this.Load(internalName);
            lock (this.packCache)
            {
                this.packCache[internalName] = (info.LastWriteTimeUtc, info.Length, pack);
            }

            return pack;
        }
        catch (Exception)
        {
            return this.Load(internalName);
        }
    }

    /// <summary>
    ///     写一个包（先写临时文件再替换，半截文件不会留在磁盘上）。
    /// </summary>
    public bool Save(string internalName, UITextPack pack, out string? error)
    {
        error = null;
        var path = this.PathOf(internalName);
        try
        {
            lock (this.gate)
            {
                Directory.CreateDirectory(this.DirectoryPath);
                pack.Meta.Source = "local";
                pack.Meta.UpdatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                var temp = path + ".tmp";
                var json = pack.ToJSON();
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
                this.WriteMirror(internalName, json);
            }

            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            Plugin.Log?.Warning($"[内部文本] 写包失败：{path}");
            return false;
        }
    }

    /// <summary>
    ///     删掉一个本地包。
    /// </summary>
    public bool Delete(string internalName, out string? error)
    {
        error = null;
        try
        {
            var path = this.PathOf(internalName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (this.mirrorDirectory is not null)
            {
                var mirror = this.MirrorPathOf(internalName);
                if (File.Exists(mirror))
                {
                    File.Delete(mirror);
                }
            }

            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private string PathOf(string internalName) => Path.Combine(this.DirectoryPath, SanitizeName(internalName) + ".json");

    private string MirrorPathOf(string internalName) => Path.Combine(this.mirrorDirectory!, SanitizeName(internalName) + ".json");

    /// <summary>
    ///     配置目录里的包不见了（被重置 / 清理）时，从持久目录的镜像恢复回工作目录。
    /// </summary>
    public bool EnsureRestored(string internalName)
    {
        try
        {
            var path = this.PathOf(internalName);
            if (File.Exists(path))
            {
                return true;
            }

            if (this.mirrorDirectory is null)
            {
                return false;
            }

            var mirror = this.MirrorPathOf(internalName);
            if (!File.Exists(mirror))
            {
                return false;
            }

            lock (this.gate)
            {
                if (File.Exists(path))
                {
                    return true;
                }

                Directory.CreateDirectory(this.DirectoryPath);
                var temp = path + ".tmp";
                File.Copy(mirror, temp, overwrite: true);
                File.Move(temp, path, overwrite: true);
            }

            Plugin.Log?.Information($"[内部文本] 本地包没了，已从持久镜像恢复：{internalName}");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 从镜像恢复包失败：{Name}", internalName);
            return false;
        }
    }

    /// <summary>包的写入时间（缺失时先从镜像恢复；没有包返回 <see cref="DateTime.MinValue" />）。</summary>
    public DateTime LastWriteTime(string internalName)
    {
        this.EnsureRestored(internalName);
        try
        {
            var path = this.PathOf(internalName);
            return File.Exists(path) ? File.GetLastWriteTime(path) : DateTime.MinValue;
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    ///     往持久目录里镜像一份（JSON 很小，但真丢了就没了；失败只记日志，绝不影响正常保存）。
    /// </summary>
    private void WriteMirror(string internalName, string json)
    {
        if (this.mirrorDirectory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(this.mirrorDirectory);
            var path = this.MirrorPathOf(internalName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 写译文包镜像失败：{Name}", internalName);
        }
    }

    /// <summary>
    ///     内部名一般只有字母数字，但仍然过一遍，避免奇怪的插件名把路径带出目录。
    /// </summary>
    private static string SanitizeName(string internalName)
    {
        var builder = new StringBuilder(internalName.Length);
        foreach (var ch in internalName)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    /// <summary>
    ///     导出成「桌面翻译工具」认识的数组格式：<c>[{Original, Translation, Context}]</c>。
    ///     默认只导出还没翻的条目，方便直接丢给 AI。
    /// </summary>
    public static string BuildAIExport(IEnumerable<UITextPackEntry> entries, bool onlyUntranslated)
    {
        var payload = entries
            .Where(e => !onlyUntranslated || !e.HasTranslation)
            .Select(e => new AIExportItem
            {
                Original = e.Original,
                Translation = e.Translated,
                Context = e.Context ?? string.Empty,
            })
            .ToList();

        return JsonSerializer.Serialize(
                   payload,
                   new JsonSerializerOptions
                   {
                       WriteIndented = true,
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   })
               + Environment.NewLine;
    }

    /// <summary>
    ///     读「桌面翻译工具」格式（<c>Original</c> + <c>Translation</c>/<c>Translated</c> + <c>Context</c>）。
    ///     只认有原文的条目，其余忽略。
    /// </summary>
    public static List<UITextPackEntry> ParseAIExport(string json, out string? error)
    {
        error = null;
        var result = new List<UITextPackEntry>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "顶层不是数组";
                return result;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var original = GetString(item, "Original");
                if (string.IsNullOrEmpty(original))
                {
                    continue;
                }

                result.Add(new UITextPackEntry
                {
                    Original = original,
                    Translated = GetString(item, "Translation") ?? GetString(item, "Translated") ?? string.Empty,
                    Context = GetString(item, "Context"),
                    Source = GetString(item, "Source") ?? "user",
                });
            }
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        return result;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>
    ///     AI 导出用的载荷（字段名与桌面工具一致）。
    /// </summary>
    private sealed class AIExportItem
    {
        [JsonPropertyName("Original")]
        public string Original { get; set; } = string.Empty;

        [JsonPropertyName("Translation")]
        public string Translation { get; set; } = string.Empty;

        [JsonPropertyName("Context")]
        public string Context { get; set; } = string.Empty;
    }

    /// <summary>
    ///     导出文件名里用的格式标记（现在只用于日志/提示，保留常量避免以后改格式忘了版本）。
    /// </summary>
    public static string FormatName => ExportFormatName;
}
