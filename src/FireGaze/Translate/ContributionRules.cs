using System.Text.RegularExpressions;

namespace FireGaze.Translate;

/// <summary>
///     提交前的本地规则检查（与 scripts/validate_contribution.py 同一套口径）。
///
///     为什么要在这里也做一遍：规则只在 GitHub Action 那侧把关的话，玩家写完超长或带网址的译文、
///     提交后才被拒——那时内容已经离开工作区。这里先拦一次，能立刻告诉他哪里不行。
///     规则本身保持极简（长度 / 控制字符 / 网址 / HTML / 很短的违规词表），尽量不限制表达。
/// </summary>
internal static class ContributionRules
{
    /// <summary>
    ///     每个字段的字数上限（与 Python 侧一致）。
    /// </summary>
    private static readonly Dictionary<string, int> Limits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = 40,
        ["Punchline"] = 99,
        ["Description"] = 999,
    };

    private static readonly string[] DenyWords =
    [
        "习近平", "法轮功", "六四", "台独", "港独", "反华", "支那", "nmsl", "共产党下台",
    ];

    private static readonly Regex Control = new(@"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", RegexOptions.Compiled);
    private static readonly Regex Urlish = new(@"(https?://|www\.|[\w-]+\.(com|net|org|cn|io|gg)\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HTML = new(@"(<\s*/?\s*[a-zA-Z][a-zA-Z0-9]*(\s[^<>]*)?>|&#\d+;|&[a-zA-Z]{2,8};)", RegexOptions.Compiled);

    public static int LimitOf(string field)
        => Limits.TryGetValue(field, out var limit) ? limit : 4096;

    /// <summary>
    ///     能不能提交；不行时给出原因（给状态行/弹窗显示）。
    /// </summary>
    public static string? Check(string field, string text)
    {
        var value = text ?? string.Empty;
        var limit = LimitOf(field);

        if (value.Length > limit)
        {
            return $"太长：{value.Length} 字，上限 {limit} 字";
        }

        if (Control.IsMatch(value))
        {
            return "里面有控制字符";
        }

        if (Urlish.IsMatch(value))
        {
            return "译文里不要放网址";
        }

        if (HTML.IsMatch(value))
        {
            return "译文里不要放 HTML 标记";
        }

        foreach (var word in DenyWords)
        {
            if (value.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return "内容里有不适合放进公共词表的词";
            }
        }

        return null;
    }
}
