using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FireGaze.UIText;

/// <summary>
///     插件**自带的本地化文件**（JSON）：AutoDuty 的 <c>Localization/en-US|zh-CN/*.json</c>、
///     PetRenamer 的 <c>I18N/en_UK.json|zh_CN.json</c> 这类。
/// </summary>
/// <remarks>
///     这些字符串不在 DLL 里，DLL 补丁碰不到（2026-10-02 用户报 AutoDuty「Waits on the specified plugins…」没翻）。
///     做法与资源型本地化同构：**翻值、不翻键**——英文侧有、中文侧缺（或为空）的键进候选，
///     译文写回中文侧文件；中文侧已经有别的译文的键一律不覆盖（上游译得更好）。
///     候选与译文都挂在包的 <c>resources</c> 段上（容器名 <c>file:&lt;相对路径&gt;</c>、键是 JSON 指针），
///     这样编辑器 / 翻译通道 / 备份还原都不用新增一套概念。
///     范围：只处理**已经自带中文变体**的插件；没有中文文件的（如 Aetherphone）不替它发明语言。
/// </remarks>
internal static class UITextLocalizationFiles
{
    /// <summary>资源容器前缀：<c>file:</c> + 相对插件目录的路径（正斜杠）。</summary>
    public const string Prefix = "file:";

    private static readonly string[] RootNames =
    [
        "Localization", "Locales", "LocalizationFiles", "Translations", "I18N", "lang", "Languages",
    ];

    private static readonly string[] ChineseNames = ["zh-CN", "zh_CN", "zh-Hans", "zh"];

    public static bool IsFileContainer(string container) =>
        container.StartsWith(Prefix, StringComparison.Ordinal);

    public static string RelativeOf(string container) => container[Prefix.Length..];

    /// <summary>
    ///     扫一个插件目录：英文侧有、中文侧缺的键 → 资源条目（候选）。
    /// </summary>
    public static List<UITextResourceItem> Scan(string pluginDirectory)
    {
        var result = new List<UITextResourceItem>();
        if (string.IsNullOrEmpty(pluginDirectory) || !Directory.Exists(pluginDirectory))
        {
            return result;
        }

        foreach (var root in FindRoots(pluginDirectory))
        {
            foreach (var (english, chinese) in FindLanguagePairs(root))
            {
                foreach (var (englishFile, chineseFile) in PairFiles(english, chinese))
                {
                    try
                    {
                        CollectMissing(englishFile, chineseFile, pluginDirectory, result);
                    }
                    catch (Exception)
                    {
                        // 单个文件读不动就跳过（尽力而为）；整根扫描失败也不影响 DLL 抽取
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     把包里的本地化文件译文写回中文侧文件。返回要写的文件内容（还没落盘，备份与写入由补丁管理器统一做）。
    ///     <paramref name="error" /> 是第一个出错原因（尽力而为，能写的文件照样写）。
    /// </summary>
    public static bool TryWrite(string pluginDirectory, UITextPack pack, out List<UITextFileWrite> writes, out string? error)
    {
        writes = [];
        error = null;
        if (string.IsNullOrEmpty(pluginDirectory) || !Directory.Exists(pluginDirectory))
        {
            return false;
        }

        foreach (var group in pack.Resources
                     .Where(r => IsFileContainer(r.Container) && r.HasTranslation && !pack.IsResourceSkipped(r.Container, r.Key))
                     .GroupBy(r => r.Container, StringComparer.OrdinalIgnoreCase))
        {
            var target = ResolveTarget(pluginDirectory, group.Key);
            if (target is null)
            {
                error ??= $"本地化文件的路径跑到插件目录外面了：{group.Key}";
                continue;
            }

            JsonNode root;
            try
            {
                root = File.Exists(target)
                    ? JsonNode.Parse(File.ReadAllText(target, Encoding.UTF8)) ?? new JsonObject()
                    : new JsonObject();
            }
            catch (Exception e)
            {
                error ??= $"读不动 {Path.GetFileName(target)}：{e.Message}";
                continue;
            }

            var changed = 0;
            foreach (var item in group)
            {
                // 中文侧已有别的译文（多半是上游填的）→ 不覆盖，只跳过
                if (ReadString(root, item.Key) is { Length: > 0 } current
                    && !string.Equals(current, item.Translated, StringComparison.Ordinal))
                {
                    continue;
                }

                if (SetString(root, item.Key, item.Translated))
                {
                    changed++;
                }
            }

            if (changed == 0)
            {
                continue;
            }

            writes.Add(new UITextFileWrite(
                target,
                group.Key,
                changed,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine));
        }

        return true;
    }

    // ── 发现语言文件 ─────────────────────────────────────────────────────

    private static IEnumerable<string> FindRoots(string pluginDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(pluginDirectory))
        {
            var name = Path.GetFileName(directory);
            if (RootNames.Any(root => string.Equals(root, name, StringComparison.OrdinalIgnoreCase)))
            {
                yield return directory;
            }
        }

        // 少数插件把语言文件直接放插件根目录（en.json / zh_CN.json）
        yield return pluginDirectory;
    }

    /// <summary>找一对（英文侧, 中文侧）——可能是语言子目录，也可能是单文件。找不到中文侧就整根跳过。</summary>
    private static IEnumerable<(string English, string Chinese)> FindLanguagePairs(string root)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(root);
        }
        catch (Exception)
        {
            yield break;
        }

        var directories = entries.Where(Directory.Exists).Select(Path.GetFileName).Where(n => n is { Length: > 0 }).ToList();
        if (directories.Count > 0)
        {
            var english = PickEnglish(directories);
            var chinese = PickChinese(directories);
            if (english is not null && chinese is not null)
            {
                yield return (Path.Combine(root, english), Path.Combine(root, chinese));
            }
        }

        var files = entries
            .Where(File.Exists)
            .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();
        if (files.Count > 0)
        {
            var english = PickEnglish(files);
            var chinese = PickChinese(files);
            if (english is not null && chinese is not null)
            {
                yield return (Path.Combine(root, english + ".json"), Path.Combine(root, chinese + ".json"));
            }
        }
    }

    private static string? PickEnglish(IReadOnlyList<string?> names)
    {
        foreach (var name in names)
        {
            if (string.Equals(name, "en", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return names.FirstOrDefault(n => n is not null && n.StartsWith("en", StringComparison.OrdinalIgnoreCase));
    }

    private static string? PickChinese(IReadOnlyList<string?> names)
    {
        // 简中优先；zh-TW / zh-Hant / zh-HK 是繁体，国服客户端一般不用
        foreach (var want in ChineseNames)
        {
            foreach (var name in names)
            {
                if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return names.FirstOrDefault(n => n is not null
                                         && n.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                                         && !n.Contains("TW", StringComparison.OrdinalIgnoreCase)
                                         && !n.Contains("HK", StringComparison.OrdinalIgnoreCase)
                                         && !n.Contains("Hant", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把英文侧与中文侧的文件按相对路径配对；中文侧缺这个文件就跳过（不替它新建文件）。</summary>
    private static IEnumerable<(string English, string Chinese)> PairFiles(string english, string chinese)
    {
        if (File.Exists(english) && File.Exists(chinese))
        {
            yield return (english, chinese);
            yield break;
        }

        if (!Directory.Exists(english) || !Directory.Exists(chinese))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(english, "*.json", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(english, file);
            var target = Path.Combine(chinese, relative);
            if (File.Exists(target))
            {
                yield return (file, target);
            }
        }
    }

    private static void CollectMissing(string englishFile, string chineseFile, string pluginDirectory, List<UITextResourceItem> result)
    {
        var english = JsonNode.Parse(File.ReadAllText(englishFile, Encoding.UTF8));
        if (english is null)
        {
            return;
        }

        JsonNode? chinese = null;
        try
        {
            chinese = JsonNode.Parse(File.ReadAllText(chineseFile, Encoding.UTF8));
        }
        catch (Exception)
        {
            // 中文侧读不动就当空文件：全量都算缺
        }

        var container = Prefix + Path.GetRelativePath(pluginDirectory, chineseFile).Replace('\\', '/');
        foreach (var (pointer, node) in Flatten(english, string.Empty))
        {
            if (node is not JsonValue value
                || value.GetValueKind() != JsonValueKind.String
                || value.GetValue<string>() is not { Length: > 0 } text
                || !UIStringExtractor.LooksTranslatableResourceValue(text))
            {
                continue;
            }

            if (ReadString(chinese, pointer) is { Length: > 0 })
            {
                continue; // 中文侧已有译文
            }

            result.Add(new UITextResourceItem
            {
                Container = container,
                Key = pointer,
                Value = text,
            });
        }
    }

    // ── JSON 指针与遍历 ──────────────────────────────────────────────────

    private static IEnumerable<(string Pointer, JsonNode? Node)> Flatten(JsonNode? node, string prefix)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    var pointer = prefix + "/" + Escape(key);
                    yield return (pointer, child);
                    if (child is JsonObject or JsonArray)
                    {
                        foreach (var item in Flatten(child, pointer))
                        {
                            yield return item;
                        }
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var pointer = prefix + "/" + i;
                    yield return (pointer, array[i]);
                    if (array[i] is JsonObject or JsonArray)
                    {
                        foreach (var item in Flatten(array[i], pointer))
                        {
                            yield return item;
                        }
                    }
                }

                break;
        }
    }

    private static string? ReadString(JsonNode? root, string pointer)
    {
        var node = Resolve(root, pointer);
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;
    }

    private static JsonNode? Resolve(JsonNode? root, string pointer)
    {
        foreach (var segment in Segments(pointer))
        {
            root = root switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null,
            };
            if (root is null)
            {
                return null;
            }
        }

        return root;
    }

    private static bool SetString(JsonNode root, string pointer, string text)
    {
        var segments = Segments(pointer).ToList();
        if (segments.Count == 0)
        {
            return false;
        }

        var current = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var segment = segments[i];
            switch (current)
            {
                case JsonObject obj:
                    if (obj[segment] is { } child)
                    {
                        current = child;
                    }
                    else
                    {
                        var created = new JsonObject();
                        obj[segment] = created;
                        current = created;
                    }

                    break;
                case JsonArray array when int.TryParse(segment, out var index) && index >= 0 && index < array.Count:
                    current = array[index]!;
                    break;
                default:
                    return false;
            }
        }

        var last = segments[^1];
        switch (current)
        {
            case JsonObject obj:
                obj[last] = JsonValue.Create(text);
                return true;
            case JsonArray array when int.TryParse(last, out var index) && index >= 0 && index < array.Count:
                array[index] = JsonValue.Create(text);
                return true;
            default:
                return false;
        }
    }

    private static IEnumerable<string> Segments(string pointer) =>
        pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Unescape);

    private static string Escape(string key) => key.Replace("~", "~0").Replace("/", "~1");

    private static string Unescape(string key) => key.Replace("~1", "/").Replace("~0", "~");

    /// <summary>把 <c>file:相对路径</c> 解析成插件目录内的绝对路径（越界一律拒绝）。</summary>
    private static string? ResolveTarget(string pluginDirectory, string container)
    {
        try
        {
            var relative = RelativeOf(container).Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(pluginDirectory, relative));
            var root = Path.GetFullPath(pluginDirectory) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>一个待写入的本地化文件（内容已在内存里，备份/落盘由补丁管理器做）。</summary>
internal sealed record UITextFileWrite(string Path, string Container, int Count, string Content);
