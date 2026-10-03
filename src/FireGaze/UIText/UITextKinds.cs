namespace FireGaze.UIText;

/// <summary>
///     投稿时声明 / 库里展示的「翻译类型」（2026-10-04 用户定）：
///     免费翻译 / 大模型翻译 / 人工翻译。玩家上传时勾选，公共译文库里按包标注。
/// </summary>
internal static class UITextKinds
{
    public const string Free = "free";
    public const string Llm = "llm";
    public const string Human = "human";

    /// <summary>把条目的 Source 映射成类型；认不出来返回 null（不参与默认勾选）。</summary>
    /// <remarks>
    ///     Source 的写法见 <c>UITextFlow</c>：人工改的是 <c>user</c>，机器译是 <c>ai:&lt;通道短名&gt;</c>
    ///     （google / mymemory / caiyun / public-caiyun / deepl / llm / deepseek）；<c>library</c> 是从公共库
    ///     合并进来的，不算玩家自己的翻译，不参与。
    /// </remarks>
    public static string? FromSource(string? source)
    {
        var value = (source ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "user" => Human,
            "ai:google" or "ai:mymemory" or "ai:caiyun" or "ai:public-caiyun" or "ai:deepl" => Free,
            "ai:llm" or "ai:deepseek" => Llm,
            _ => value.StartsWith("ai:", StringComparison.Ordinal) ? Free : null,
        };
    }

    /// <summary>把一串类型变成人话（给界面用）；空列表返回空串。</summary>
    public static string Describe(IEnumerable<string>? kinds)
    {
        if (kinds is null)
        {
            return string.Empty;
        }

        var parts = kinds
            .Select(kind => kind?.Trim().ToLowerInvariant() switch
            {
                Free => "免费翻译",
                Llm => "大模型翻译",
                Human => "人工翻译",
                _ => string.Empty,
            })
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return string.Join("、", parts);
    }
}
