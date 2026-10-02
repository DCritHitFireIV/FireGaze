using System.Text;
using System.Text.RegularExpressions;

namespace FireGaze.UIText;

/// <summary>
///     文本层面的小规则：ImGui 的 <c>###</c> ID 拆分、占位符校验、翻译输入输出清洗。
///     打补丁（后面那步）和翻译通道都走这里，避免两处口径不一致。
/// </summary>
internal static class UITextText
{
    /// <summary>
    ///     ImGui 的 ID 分隔符：显示的部分在它前面，ID 在它后面。
    /// </summary>
    public const string IDSeparator = "###";

    /// <summary>
    ///     取「玩家能看见的部分」——<c>###</c> 之后是控件 ID，不参与翻译。
    /// </summary>
    public static string ForDisplay(string literal)
    {
        var index = literal.IndexOf(IDSeparator, StringComparison.Ordinal);
        return index >= 0 ? literal[..index] : literal;
    }

    /// <summary>
    ///     取原文里 <c>###</c> 之后的 ID 部分（没有就返回空串）。
    /// </summary>
    public static string IDPart(string literal)
    {
        var index = literal.IndexOf(IDSeparator, StringComparison.Ordinal);
        return index >= 0 ? literal[index..] : string.Empty;
    }

    /// <summary>
    ///     按原文的 ID 结构拼出补丁里要写的新字面量：
    ///     原文自带 ID（<c>文本###id</c>）时只换显示部分；否则需要保 ID 就补 <c>###原文</c>。
    /// </summary>
    public static string BuildPatched(string original, string translated, bool preserveID)
    {
        var display = translated.Trim();
        var id = IDPart(original);
        if (id.Length > 0)
        {
            return display + id;
        }

        return preserveID ? display + IDSeparator + original : display;
    }

    private static readonly Regex PlaceholderPattern = new(
        @"\{[^{}]*\}|%[sdif]|\\n|\\t|\\r",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     抽出原文里的占位符（<c>{0}</c> / <c>{name}</c> / <c>%s</c> / <c>\n</c>）。
    /// </summary>
    public static List<string> Placeholders(string text)
    {
        var result = new List<string>();
        foreach (Match match in PlaceholderPattern.Matches(text))
        {
            result.Add(match.Value);
        }

        return result;
    }

    /// <summary>
    ///     译文里的占位符必须和原文一一对上（顺序不要求，数量要够）。
    ///     对不上返回人话原因；没问题返回 null。
    /// </summary>
    public static string? CheckPlaceholders(string original, string translated)
    {
        var source = Placeholders(ForDisplay(original));
        if (source.Count == 0)
        {
            return null;
        }

        var target = Placeholders(translated);
        var missing = new List<string>();
        foreach (var token in source.Distinct(StringComparer.Ordinal))
        {
            var need = source.Count(x => string.Equals(x, token, StringComparison.Ordinal));
            var have = target.Count(x => string.Equals(x, token, StringComparison.Ordinal));
            if (have < need)
            {
                missing.Add(token);
            }
        }

        return missing.Count == 0 ? null : "占位符对不上：" + string.Join(" ", missing);
    }

    /// <summary>
    ///     翻译看的是「显示部分」；顺便把首尾空白压掉。
    /// </summary>
    public static string ForTranslation(string original) => ForDisplay(original).Trim();

    /// <summary>
    ///     修一修大模型偶发的 JSON 噪声：deepseek-flash 实测会吐 <c>"i":1"</c>（数字后多一个引号）这种坏法；
    ///     再顺手去掉尾随逗号。修不动就原样返回，交给上层报错。
    /// </summary>
    public static string RepairJSON(string json)
    {
        var repaired = json;
        try
        {
            // "i":1"  → "i":1（数字后面的多引号）
            repaired = Regex.Replace(repaired, "(\"i\"\\s*:\\s*)(\\d+)\"", "$1$2", RegexOptions.CultureInvariant);
            // 尾随逗号：,} / ,] → } / ]（可带空白）
            repaired = Regex.Replace(repaired, ",\\s*([}\\]])", "$1", RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            // 正则出问题也不能让翻译挂掉：返回已经修过的部分
        }

        return repaired;
    }

    /// <summary>
    ///     从模型输出里抠出 JSON（容忍 ```json 围栏和前后废话），失败返回 null。
    /// </summary>
    public static string? ExtractJSON(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOfAny(['{', '[']);
        if (start < 0)
        {
            return null;
        }

        var open = text[start];
        var close = open == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == open)
            {
                depth++;
            }
            else if (ch == close)
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     判断一段文本是不是「已经是中文」——有汉字、又没有假名。
    /// </summary>
    /// <remarks>
    ///     与简介词表同一口径（2026-09-22 定）：日语要翻，所以带假名的不算「已是中文」。
    ///     混在中文里的英文名字 / 命令（AutoHunt、Lifestream、/vnav）不改变判定——
    ///     中文插件常见「中文句子 + 英文专名」，这类本来就不需要翻译（2026-10-02 修）。
    /// </remarks>
    public static bool IsAlreadyChinese(string text)
    {
        var cjk = 0;
        var kana = 0;
        foreach (var ch in text)
        {
            if (ch is >= '\u4e00' and <= '\u9fff')
            {
                cjk++;
            }
            else if (ch is >= '\u3040' and <= '\u30ff' or >= '\u31f0' and <= '\u31ff' or >= '\uff66' and <= '\uff9d')
            {
                kana++;
            }
        }

        return cjk > 0 && kana == 0;
    }

    /// <summary>
    ///     像「界面句子」的查表 key：插件把英文界面文本当键传给 <c>Tr()</c> / <c>Translate()</c>
    ///     （「词典式汉化」：命中词典显中文，未命中就把键本身显出来）。这类 key 翻了以后查表 miss，
    ///     会直接显示译文本身——**多半安全且有效**；但「翻了到底会不会影响别处」静态看不出来，
    ///     调用方按**灰名单**处理（默认不翻、用户确认后翻），不放行也不丢弃。
    /// </summary>
    /// <remarks>
    ///     判据保守（2026-10-02，Allagan Tools - CN 实测 165 条）：至少 3 个词、长度 ≥ 20、
    ///     无下划线、无占位符/运算符（<c>{ } % &lt; &gt; =</c>）、至少含一个小写字母。
    ///     反例：<c>no_matches.title</c>（点分标识符）、<c>Save</c>（单词）、<c>1st Person Camera</c>（太短）。
    /// </remarks>
    public static bool LooksLikeSentenceKey(string text)
    {
        if (text.Length < 20)
        {
            return false;
        }

        if (text.Contains('_') || text.Contains('{') || text.Contains('}') || text.Contains('%')
            || text.Contains('<') || text.Contains('>') || text.Contains('='))
        {
            return false;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3)
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (char.IsAsciiLetterLower(ch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     压一压模型输出里的常见噪声：整段包引号、多出来的换行。
    /// </summary>
    public static string CleanTranslated(string text)
    {
        var value = text.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
        }

        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    /// <summary>
    ///     给日志/界面用：把文本压成一行。
    /// </summary>
    public static string OneLine(string text, int max = 60)
    {
        var builder = new StringBuilder(Math.Min(text.Length, max));
        foreach (var ch in text)
        {
            if (builder.Length >= max)
            {
                builder.Append('…');
                break;
            }

            builder.Append(ch is '\n' or '\r' ? ' ' : ch);
        }

        return builder.ToString();
    }
}
