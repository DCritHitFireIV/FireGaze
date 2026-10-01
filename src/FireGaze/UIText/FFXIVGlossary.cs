using System.Text;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace FireGaze.UIText;

/// <summary>
///     「英文 → 国服官方中文」术语表：**直接从游戏自己的数据里读**（Lumina 表），不联网、不随插件打包。
/// </summary>
/// <remarks>
///     用途：给大模型翻译当术语表，保证 Palace of the Dead → 死者宫殿 这类专有名词一致；
///     参考 scripts/ffxiv_glossary.py（插件简介的翻译管线走的就是那份 datamining 表，
///     这里换成从正在运行的游戏客户端读，免下载、免缓存，也不会让插件体积变大）。
///     只在大模型通道里用得上：免费接口（Google / MyMemory）没法把术语表塞进请求。
/// </remarks>
internal static class FFXIVGlossary
{
    /// <summary>常见泛词不当专有名词匹配（单个词命中时过滤）。</summary>
    private static readonly HashSet<string> StopSingle = new(StringComparer.OrdinalIgnoreCase)
    {
        "attack", "damage", "target", "player", "party", "enemy", "action", "ready", "start",
        "window", "option", "setting", "setting s", "module", "function", "system", "button", "display",
        "status", "effect", "skill", "spell", "item", "quest", "level", "time", "mode", "list",
        "name", "type", "value", "count", "total", "second", "minute", "hour", "critical", "direct",
    };

    private static readonly object Gate = new();
    private static Dictionary<string, string>? terms;
    private static string? failure;

    /// <summary>术语表是否已就绪。</summary>
    public static bool Ready => terms is not null;

    /// <summary>构建是否失败。失败后不再自动重试，等用户点「重试」或重载插件。</summary>
    public static bool Failed => failure is not null;

    /// <summary>失败原因（给设置页的悬停说明用）。</summary>
    public static string? FailureReason => failure;

    /// <summary>术语表条数（没建好返回 0）。</summary>
    public static int Count => terms?.Count ?? 0;

    /// <summary>测试用：离线塞一份术语表（游戏数据在冒烟测试里读不到）。</summary>
    internal static void SetTermsForDiagnostics(Dictionary<string, string> value)
    {
        lock (Gate)
        {
            terms = value;
            failure = null;
        }
    }

    /// <summary>
    ///     准备术语表（幂等、线程安全）。第一次会读十几张表，可能要一两秒——只在后台线程调。
    ///     构建失败（读不到中文数据等）会记进 <see cref="Failed" /> 并停在那，不会每次调用都重读游戏数据；
    ///     用户点「重试」会先 <see cref="ResetFailure" />。
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

            var built = Build();
            if (built is null)
            {
                // 不再写 terms：界面据 Failed 显示「构建失败 · 重试」，而不是谎报「已就绪 0 条」
                failure = "读游戏数据失败（国际服客户端没有中文数据，国服详见日志）";
                return;
            }

            terms = built;
        }
    }

    /// <summary>清掉失败状态，允许重新构建（设置页的「重试」）。</summary>
    public static void ResetFailure()
    {
        lock (Gate)
        {
            failure = null;
        }
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

    private static Dictionary<string, string>? Build()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var data = Plugin.DataManager;
            if (data is null)
            {
                Plugin.Log?.Warning("[内部文本] 术语表：DataManager 不可用，跳过");
                return null;
            }

            var report = new List<string>(10);
            AddSheet(result, data.GetExcelSheet<PlaceName>(ClientLanguage.English), data.GetExcelSheet<PlaceName>(ClientLanguage.ChineseSimplified), row => row.Name.ExtractText(), "PlaceName", report);
            AddSheet(result, data.GetExcelSheet<TerritoryType>(ClientLanguage.English), data.GetExcelSheet<TerritoryType>(ClientLanguage.ChineseSimplified), row => row.Name.ExtractText(), "TerritoryType", report);
            AddSheet(result, data.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English), data.GetExcelSheet<ContentFinderCondition>(ClientLanguage.ChineseSimplified), row => row.Name.ExtractText(), "ContentFinderCondition", report);
            AddSheet(result, data.GetExcelSheet<ClassJob>(ClientLanguage.English), data.GetExcelSheet<ClassJob>(ClientLanguage.ChineseSimplified), row => row.Name.ExtractText(), "ClassJob", report);
            AddSheet(result, data.GetExcelSheet<Lumina.Excel.Sheets.Action>(ClientLanguage.English), data.GetExcelSheet<Lumina.Excel.Sheets.Action>(ClientLanguage.ChineseSimplified), row => row.Name.ExtractText(), "Action", report);
            AddSheet(result, data.GetExcelSheet<Status>(ClientLanguage.English), data.GetExcelSheet<Status>(ClientLanguage.ChineseSimplified), row => row.Name.ExtractText(), "Status", report);
            AddSheet(result, data.GetExcelSheet<Mount>(ClientLanguage.English), data.GetExcelSheet<Mount>(ClientLanguage.ChineseSimplified), row => row.Singular.ExtractText(), "Mount", report);
            AddSheet(result, data.GetExcelSheet<Emote>(ClientLanguage.English), data.GetExcelSheet<Emote>(ClientLanguage.ChineseSimplified), row => row.Name.ExtractText(), "Emote", report);
            AddSheet(result, data.GetExcelSheet<BNpcName>(ClientLanguage.English), data.GetExcelSheet<BNpcName>(ClientLanguage.ChineseSimplified), row => row.Singular.ExtractText(), "BNpcName", report);
            AddSheet(result, data.GetExcelSheet<Orchestrion>(ClientLanguage.English), data.GetExcelSheet<Orchestrion>(ClientLanguage.ChineseSimplified), row => row.Name.ExtractText(), "Orchestrion", report);

            if (result.Count == 0)
            {
                Plugin.Log?.Warning($"[内部文本] 术语表：没读到任何中文数据（{string.Join(" / ", report)}）");
                return null;
            }

            Plugin.Log?.Information($"[内部文本] 术语表就绪：{result.Count} 条（读自游戏数据，不联网；{string.Join(" / ", report)}）");
            return result;
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 术语表构建失败（不影响翻译）");
            return null;
        }
    }

    private static void AddSheet<T>(
        Dictionary<string, string> result,
        Lumina.Excel.ExcelSheet<T>? english,
        Lumina.Excel.ExcelSheet<T>? chinese,
        Func<T, string> name,
        string sheetName,
        List<string> report)
        where T : struct, Lumina.Excel.IExcelRow<T>
    {
        if (english is null || chinese is null)
        {
            report.Add($"{sheetName}=不可用");
            return;
        }

        var added = 0;
        foreach (var row in english)
        {
            var rowID = row.RowId;
            if (!chinese.HasRow(rowID))
            {
                continue;
            }

            var source = name(row).Trim();
            if (source.Length < 3 || source.Length > 48)
            {
                continue;
            }

            var target = name(chinese.GetRow(rowID)).Trim();
            if (target.Length == 0 || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 英文列里混进中文 / 换行 / 乱码的都跳过
            if (target.Any(ch => char.IsAsciiLetter(ch)) && !target.Any(IsCjk))
            {
                continue;
            }

            if (source.Any(IsCjk) || source.Any(ch => char.IsControl(ch)))
            {
                continue;
            }

            result[source] = target;
            added++;
        }

        report.Add($"{sheetName}={added}");
    }

    private static bool IsCjk(char ch) => ch is >= '\u4e00' and <= '\u9fff' or >= '\u3040' and <= '\u30ff';
}
