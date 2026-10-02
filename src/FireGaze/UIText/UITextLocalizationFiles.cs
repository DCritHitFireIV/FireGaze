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
    ///     基线口径与 DLL 补丁一致：盘上是我们的补丁时从**原始备份**重新写一遍（改了译文再打一次才会真的更新，
    ///     直接读当前文件会把自己的旧译文当成上游译文而永远不覆盖）；否则以当前文件为基线。
    ///     <paramref name="previousFiles" /> 是上一次打补丁的记录（用来认出「盘上是我们的补丁」）。
    ///     <paramref name="error" /> 是第一个出错原因（尽力而为，能写的文件照样写）。
    /// </summary>
    public static bool TryWrite(
        string pluginDirectory,
        UITextPack pack,
        IReadOnlyList<UITextLocalizationPreviousFile>? previousFiles,
        out List<UITextFileWrite> writes,
        out string? error)
    {
        writes = [];
        error = null;
        if (string.IsNullOrEmpty(pluginDirectory) || !Directory.Exists(pluginDirectory))
        {
            return false;
        }

        var previousByPath = new Dictionary<string, UITextLocalizationPreviousFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var previous in previousFiles ?? [])
        {
            if (!string.IsNullOrEmpty(previous.Path))
            {
                previousByPath[previous.Path] = previous;
            }
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

            if (!File.Exists(target))
            {
                error ??= $"找不到 {Path.GetFileName(target)}（扫描之后被删了？）";
                continue;
            }

            string currentText;
            try
            {
                currentText = File.ReadAllText(target, Encoding.UTF8);
            }
            catch (Exception e)
            {
                error ??= $"读不动 {Path.GetFileName(target)}：{e.Message}";
                continue;
            }

            var currentHash = UITextHash.OfFile(target);
            previousByPath.TryGetValue(target, out var previous);
            var isOurs = IsOurPatchedFile(previous, currentHash);

            string baselineText;
            try
            {
                // 盘上是我们的补丁 → 回到原始内容再重新写；否则当前文件就是原始基线
                baselineText = isOurs
                    ? File.ReadAllText(previous!.BackupPath!, Encoding.UTF8)
                    : currentText;
            }
            catch (Exception e)
            {
                error ??= $"读不动 {Path.GetFileName(target)} 的原始备份：{e.Message}";
                continue;
            }

            JsonNode root;
            try
            {
                root = JsonNode.Parse(baselineText) ?? new JsonObject();
            }
            catch (Exception e)
            {
                error ??= $"读不动 {Path.GetFileName(target)}：{e.Message}";
                continue;
            }

            // 英文侧（配对规则找得到才用）：陈旧指针闸门用（2026-10-03 评审 B-13）
            JsonNode? englishRoot = null;
            var englishFile = FindEnglishCounterpart(pluginDirectory, target);
            if (englishFile is not null)
            {
                try
                {
                    englishRoot = JsonNode.Parse(File.ReadAllText(englishFile, Encoding.UTF8));
                }
                catch (Exception)
                {
                    englishRoot = null; // 读不动英文侧 → 回退旧行为（宁可多写，也别漏写）
                }
            }

            var changed = 0;
            var stale = 0;
            foreach (var item in group)
            {
                // 基线上已有译文（上游填的，或是同一个译文）→ 不覆盖；
                // 指针上不是字符串（中英文两边结构对不上）→ 也不动，别把结构改坏。
                if (!CanWrite(root, item.Key))
                {
                    continue;
                }

                // 陈旧指针闸门：英文侧已经没有这个 key（上游把文案删了）→ 不往中文文件里补孤儿键。
                // 定位不到英文侧（配对规则没覆盖）时保持旧行为。
                if (englishRoot is not null && !EnglishHasKey(englishRoot, item.Key))
                {
                    stale++;
                    continue;
                }

                if (SetString(root, item.Key, item.Translated))
                {
                    changed++;
                }
            }

            if (stale > 0)
            {
                Plugin.Log?.Information($"[内部文本] {Path.GetFileName(target)}：{stale} 条指针在英文侧已不存在，跳过（陈旧条目，不补孤儿键）");
            }

            if (changed == 0)
            {
                continue;
            }

            var content = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            if (string.Equals(content, currentText, StringComparison.Ordinal))
            {
                continue; // 产出与盘上现有内容一致，不必重写
            }

            writes.Add(new UITextFileWrite(
                target,
                group.Key,
                changed,
                content,
                isOurs ? previous!.SourceHash : currentHash,
                NeedsBackup: !isOurs,
                ReuseBackup: isOurs ? previous!.BackupPath : null));
        }

        return true;
    }

    /// <summary>
    ///     盘上的文件是不是我们自己打过的补丁：记录里有原始备份，且当前哈希 = 打完之后的哈希。
    ///     有一条对不上就不能当「是我们的补丁」——绝不能拿旧备份去盖更新后的文件。
    /// </summary>
    public static bool IsOurPatchedFile(UITextLocalizationPreviousFile? previous, string currentHash) =>
        previous is { HasBackup: true }
        && previous.PatchedHash is { Length: > 0 }
        && currentHash.Length > 0
        && string.Equals(currentHash, previous.PatchedHash, StringComparison.OrdinalIgnoreCase);

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

            if (!CanWrite(chinese, pointer))
            {
                continue; // 中文侧已有译文，或这个位置根本不是字符串（结构对不上）
            }

            // 「原生显示」类键（PetRenamer 的 Language.*.Raw）：插件用「以原生语言显示语言名」开关专门读它，
            // 设计上就要显示各语言自己的名字（English / Deutsch / Nederlands…），翻成中文反而让开关失去意义。
            // 两种写法都算：键名本身就是 Language.English.Raw（指针 /Language.English.Raw，以 .Raw 结尾），
            // 或嵌成 {"Language":{"English":{"Raw":…}}}（指针 /Language/English/Raw）。
            if (pointer.EndsWith("/Raw", StringComparison.Ordinal)
                || pointer.EndsWith(".Raw", StringComparison.Ordinal))
            {
                continue;
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

    /// <summary>
    ///     能不能在这个指针上写译文：位置上是空的或者空字符串（当缺）；
    ///     已有非空字符串（别人的译文）或根本不是字符串（两边结构对不上）都不能写。
    /// </summary>
    /// <summary>英文侧是否还留着这个指针（非空字符串）。B-13 的陈旧指针闸门用。</summary>
    private static bool EnglishHasKey(JsonNode? englishRoot, string pointer) =>
        Resolve(englishRoot, pointer) is JsonValue value
        && value.GetValueKind() == JsonValueKind.String
        && value.GetValue<string>() is { Length: > 0 };

    /// <summary>
    ///     找中文文件对应的英文文件（复用扫描时的配对规则）；找不到返回 null。
    /// </summary>
    private static string? FindEnglishCounterpart(string pluginDirectory, string chineseFile)
    {
        try
        {
            foreach (var root in FindRoots(pluginDirectory))
            {
                foreach (var (english, chinese) in FindLanguagePairs(root))
                {
                    foreach (var (englishFile, candidate) in PairFiles(english, chinese))
                    {
                        if (string.Equals(candidate, chineseFile, StringComparison.OrdinalIgnoreCase))
                        {
                            return englishFile;
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // 扫不动就当作找不到：调用方回退到旧行为
        }

        return null;
    }

    private static bool CanWrite(JsonNode? root, string pointer)
    {
        var node = Resolve(root, pointer);
        return node switch
        {
            null => true,
            JsonValue value => value.GetValueKind() != JsonValueKind.String
                               || value.GetValue<string>() is not { Length: > 0 },
            _ => false,
        };
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

/// <summary>
///     上一次打补丁时一个文件的记录（本地化文件写回只需要这四个字段）。
///     单独一份：离线探针只编译本地化模块，不依赖补丁存储那一层。
/// </summary>
internal sealed record UITextLocalizationPreviousFile(string Path, string SourceHash, string? PatchedHash, string? BackupPath)
{
    public bool HasBackup => !string.IsNullOrEmpty(this.BackupPath) && File.Exists(this.BackupPath);
}

/// <summary>
///     一个待写入的本地化文件（内容已在内存里，备份/落盘由补丁管理器做）。
///     <paramref name="SourceHash" /> 是**原始基线**的哈希（不是当前文件的）；
///     <paramref name="NeedsBackup" /> 为 false 时盘上已经是我们的补丁，<paramref name="ReuseBackup" /> 是要沿用的那份原始备份。
/// </summary>
internal sealed record UITextFileWrite(
    string Path,
    string Container,
    int Count,
    string Content,
    string SourceHash,
    bool NeedsBackup,
    string? ReuseBackup);
