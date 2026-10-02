using System.IO.Compression;
using System.Text;

namespace FireGaze.UIText;

/// <summary>
///     「英文 → 国服官方中文」术语表：**随插件打包**，不联网、不读游戏数据。
/// </summary>
/// <remarks>
///     用途：给大模型翻译当术语表，保证 Palace of the Dead → 死者宫殿 这类专有名词一致；
///     数据由 <c>scripts/ffxiv_glossary.py</c> 从公开 datamining 的官方文本里生成
///     （EN 用 xivapi/ffxiv-datamining，中文用 thewakingsands/ffxiv-datamining-cn，按行 key 对齐），
///     生成物是 <c>ffxiv-glossary.tsv.gz</c>。
///     **为什么不用正在运行的游戏数据**：国服客户端的 exd 只有中文（没有英文原文），
///     而且这版 Lumina 会把请求语言覆盖成默认语言（ExcelModule.GetRawSheetCore 里 `language = Language;`），
///     所以「从客户端读英文」在 CN/国际服都拿不到英文→中文对照（2026-10-02 实测）。
///     只在大模型通道里用得上：免费接口（Google / MyMemory / 彩云小译）没法把术语表塞进请求。
/// </remarks>
internal static class FFXIVGlossary
{
    /// <summary>术语表文件名（放在插件目录里，随插件一起分发）。</summary>
    internal const string FileName = "ffxiv-glossary.tsv";

    /// <summary>术语表所在的插件目录；Plugin 构造时配置。</summary>
    public static string? PluginDirectory { get; set; }

    /// <summary>常见泛词不当专有名词匹配（单个词命中时过滤）。</summary>
    /// <remarks>
    ///     2026-10-03：单词条必须非常保守——游戏数据里大量「地名 / 表情 / 技能」撞上普通英语词，一律
    ///     「必须采用」会把界面翻出笑话（实测：disable→封技、unknown→不明物体、refresh→醒神、
    ///     warning→倒计时、source→始源湖、content→表情：幸福、threshold→回退预备、rotation→转向…）。
    ///     与 <c>scripts/ffxiv_glossary.py</c> 的 STOP_SINGLE 保持同一份名单。
    /// </remarks>
    private static readonly HashSet<string> StopSingle = new(StringComparer.OrdinalIgnoreCase)
    {
        "attack", "damage", "target", "player", "party", "enemy", "action", "ready", "start",
        "window", "option", "setting", "setting s", "module", "function", "system", "button", "display",
        "status", "effect", "skill", "spell", "item", "quest", "level", "time", "mode", "list",
        "name", "type", "value", "count", "total", "second", "minute", "hour", "critical", "direct",
        // "general" 在成就分类里是「整体」，在插件界面里通常是「常规」——语境不一，不提示。
        "general",
        // 2026-10-03 实测受损词（同一拼写既是游戏术语、又是界面高频普通词）
        "disable", "disabled", "convert", "unknown", "refresh", "generate", "breaking", "warning",
        "threshold", "resolve", "tankbuster", "rotation", "survival", "starburst", "lodestone",
        "reverse", "content", "source", "minimum", "release", "protect", "destroy", "patience",
    };

    private static readonly object Gate = new();
    // volatile：EnsureBuilt 是双重检查锁，第一次检查在锁外读这个字段（CA1508 报「恒 false」是分析器
    // 不理解多线程的双检锁，属误报）；加 volatile 保证发布可见性。
    private static volatile Dictionary<string, string>? terms;
    private static string? failure;

    /// <summary>术语表是否已就绪。</summary>
    public static bool Ready => terms is not null;

    /// <summary>加载是否失败。失败后不再自动重试，等用户点「重试」或重载插件。</summary>
    public static bool Failed => failure is not null;

    /// <summary>失败原因（给设置页的悬停说明用）。</summary>
    public static string? FailureReason => failure;

    /// <summary>术语表条数（没就绪返回 0）。</summary>
    public static int Count => terms?.Count ?? 0;

    /// <summary>测试用：离线塞一份术语表（冒烟测试不加载真实文件时用）。</summary>
    internal static void SetTermsForDiagnostics(Dictionary<string, string> value)
    {
        lock (Gate)
        {
            terms = value;
            failure = null;
        }
    }

    /// <summary>
    ///     准备术语表（幂等、线程安全）。从插件目录读 <see cref="FileName" />，一次读盘、之后常驻内存。
    ///     失败会记进 <see cref="Failed" /> 并停在那；用户点「重试」会先 <see cref="ResetFailure" />。
    /// </summary>
    public static void EnsureBuilt()
    {
        if (terms is not null || failure is not null)
        {
            return;
        }

        lock (Gate)
        {
            if (terms is not null || failure is not null)
            {
                return;
            }

            var directory = PluginDirectory;
            var path = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, FileName);
            if (path is null || !File.Exists(path))
            {
                failure = $"没找到术语表文件 {FileName}（插件包不完整？）";
                Plugin.Log?.Warning($"[内部文本] 术语表：{failure}");
                return;
            }

            if (!LoadFromFile(path))
            {
                failure = "术语表文件读取失败（文件损坏？）";
                return;
            }
        }
    }

    /// <summary>清掉失败状态，允许重新加载（设置页的「重试」）。</summary>
    public static void ResetFailure()
    {
        lock (Gate)
        {
            failure = null;
        }
    }

    /// <summary>
    ///     从指定文件加载术语表（TSV，两列：英文、中文；.gz 自动解压）。
    ///     加载成功会替换现有内存表；失败返回 false 并保留原有状态。
    /// </summary>
    public static bool LoadFromFile(string path)
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var file = File.OpenRead(path);
            var gzipMagic = file.ReadByte() == 0x1F && file.ReadByte() == 0x8B;
            file.Position = 0;
            using Stream stream = gzipMagic ? new GZipStream(file, CompressionMode.Decompress) : file;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var tab = line.IndexOf('\t');
                if (tab <= 0 || tab >= line.Length - 1)
                {
                    continue;
                }

                table[line[..tab]] = line[(tab + 1)..];
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 术语表读取失败");
            return false;
        }

        if (table.Count == 0)
        {
            Plugin.Log?.Warning("[内部文本] 术语表：文件里一条也没有");
            return false;
        }

        lock (Gate)
        {
            terms = table;
            failure = null;
        }

        Plugin.Log?.Information($"[内部文本] 术语表就绪：{table.Count} 条（随插件打包的官方译名数据）");
        return true;
    }

    /// <summary>
    ///     这条机器译文要不要「按术语表重译」：原文命中术语表、且译文里找不到该术语的官方译名。
    /// </summary>
    /// <remarks>
    ///     用途（2026-10-02）：术语表后来补齐了，但机器翻译不会覆盖已有译文（「Grand Company Expert
    ///     Delivery → 部队理符交付」这种历史译名会永远留着）——编辑器拿这个判定挑出需要重译的条目，
    ///     玩家的手改（user）永不被动（调用方自己先过滤）。
    /// </remarks>
    public static bool NeedsGlossaryRepair(string original, string translated)
    {
        if (!Ready)
        {
            return false;
        }

        var terms = FindTerms([original]);
        if (terms.Count == 0)
        {
            return false;
        }

        var value = translated ?? string.Empty;
        foreach (var (_, chinese) in terms)
        {
            if (!value.Contains(chinese, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     从一批文本里找出命中的术语（最多 <paramref name="max" /> 条），给模型当「必须采用」的译名表。
    /// </summary>
    public static List<(string English, string Chinese)> FindTerms(IReadOnlyList<string> texts, int max = 30)
    {
        var found = new List<(string, string)>();
        var dictionary = terms;
        if (dictionary is null || dictionary.Count == 0 || max <= 0)
        {
            return found;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var words = Tokenize(text);
            for (var start = 0; start < words.Count; start++)
            {
                var limit = Math.Min(4, words.Count - start);
                for (var length = limit; length >= 1; length--)
                {
                    if (length == 1 && words[start].Length < 4)
                    {
                        continue;
                    }

                    var phrase = string.Join(' ', words.Skip(start).Take(length));
                    if (length == 1 && StopSingle.Contains(phrase))
                    {
                        continue;
                    }

                    if (!dictionary.TryGetValue(phrase, out var chinese) || !seen.Add(phrase))
                    {
                        continue;
                    }

                    found.Add((phrase, chinese));
                    if (found.Count >= max)
                    {
                        return found;
                    }
                }
            }
        }

        return found;
    }

    private static List<string> Tokenize(string text)
    {
        var words = new List<string>(8);
        var current = new StringBuilder(16);
        foreach (var ch in text)
        {
            if (char.IsAsciiLetter(ch) || char.IsAsciiDigit(ch) || ch is '\'' or '’' or '-' or '.')
            {
                current.Append(ch is '’' ? '\'' : ch);
                continue;
            }

            if (current.Length > 0)
            {
                words.Add(current.ToString().Trim('.', '-', '\''));
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString().Trim('.', '-', '\''));
        }

        return words;
    }
}
