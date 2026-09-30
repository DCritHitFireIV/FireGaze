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
    ///     判断一段文本是不是「已经是中文」（有汉字、又不是假名为主）——已经是中文的不用再翻。
    /// </summary>
    public static bool IsAlreadyChinese(string text)
    {
        var cjk = 0;
        var latin = 0;
        foreach (var ch in text)
        {
            if (ch is >= '\u4e00' and <= '\u9fff')
            {
                cjk++;
            }
            else if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                latin++;
            }
        }

        return cjk > 0 && latin < 2;
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
