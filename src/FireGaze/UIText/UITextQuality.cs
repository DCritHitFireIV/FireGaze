using System.Text.RegularExpressions;

namespace FireGaze.UIText;

/// <summary>
///     上传前的本地译文体检：把「不健康」的条目挑出来，让用户先修、不上传到公共库。
/// </summary>
/// <remarks>
///     口径与云端 <c>scripts/inbox_uit.py::check_text</c> 保持同步（空译文 / 超长 / 控制字符 / 违规词），
///     并补两条本地方便拦的：占位符对不上（`{0}` 之类丢了会弄坏别人插件）、与原文相同（等于没翻）。
///     纯函数、可离线测（fgtest 钉住）——**不联网、不调用翻译通道**。
/// </remarks>
internal static class UITextQuality
{
    /// <summary>与云端 <c>inbox_uit.py</c> 的 MAX_TRANSLATED 同步。</summary>
    public const int MaxTranslatedLength = 2000;

    /// <summary>与云端 <c>inbox_uit.py</c> 的 MAX_ORIGINAL 同步。</summary>
    public const int MaxOriginalLength = 4000;

    /// <summary>与云端 <c>validate_contribution.py</c> 的 DENY_WORDS 同步。</summary>
    private static readonly string[] DenyWords =
    [
        "习近平", "法轮功", "六四", "台独", "港独", "反华", "支那", "nmsl", "共产党下台",
    ];

    /// <summary>与云端 <c>validate_contribution.py</c> 的 CONTROL 同步（保留 \n \t \r）。</summary>
    private static readonly Regex ControlChars = new(@"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]", RegexOptions.Compiled);

    /// <summary>一条没过体检的条目（给界面列清单用）。</summary>
    /// <param name="Label">条目在界面上的称呼（如 <c>«Some Label»</c> / <c>资源 · «…»</c>）。</param>
    /// <param name="Original">原文（编辑器里按它找）。</param>
    /// <param name="Reason">问题描述。</param>
    public sealed record Problem(string Label, string Original, string Reason);

    /// <summary>「与原文相同」的判定文案（单独拿出来：它属于「不用传」而不是「需要修」那类）。</summary>
    public const string CopyOfSourceReason = "与原文相同，等于没翻";

    /// <summary>
    ///     这条问题是不是「照抄原文」（品牌名 / 缩写这类根本无需翻译的条目）——
    ///     界面上要把它们与真正需要修的问题分开说（2026-10-03 评审 C-06）。
    /// </summary>
    public static bool IsCopyOfSource(string reason) =>
        string.Equals(reason, CopyOfSourceReason, StringComparison.Ordinal);

    /// <summary>
    ///     体检一条译文：返回问题描述；<c>null</c> = 健康、可以上传。
    /// </summary>
    public static string? Check(string original, string translated)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return "译文为空";
        }

        if (translated.Length > MaxTranslatedLength)
        {
            return $"译文超长（{translated.Length} > {MaxTranslatedLength}）";
        }

        if (original.Length > MaxOriginalLength)
        {
            return $"原文超长（{original.Length} > {MaxOriginalLength}）";
        }

        if (ControlChars.IsMatch(translated) || ControlChars.IsMatch(original))
        {
            return "有控制字符";
        }

        foreach (var word in DenyWords)
        {
            if (translated.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return $"含违规词（{word}）";
            }
        }

        if (string.Equals(translated.Trim(), original.Trim(), StringComparison.Ordinal))
        {
            return CopyOfSourceReason;
        }

        return UITextText.CheckPlaceholders(original, translated);
    }

    /// <summary>条目在问题清单里的称呼：原文一行截断。</summary>
    public static string Label(string original) => "«" + UITextText.OneLine(original.Trim(), 40) + "»";
}
