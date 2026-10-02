using System.Text.Json;
using System.Text.Json.Nodes;

namespace FireGaze.UIText;

/// <summary>
///     DLL **内嵌 JSON 本地化表**（HaselTweaks / LeveHelper 这一套）：
///     程序集资源里放一份 <c>{ "键": { "en": "…", "ja": "…", "zh": "…" } }</c>，
///     运行时按键取当前语言、缺了就回退 <c>en</c>。
/// </summary>
/// <remarks>
///     这些字符串既不在 <c>ldstr</c>、也不在 <c>.resources</c> 里，普通抽取看不到
///     （2026-10-03 用户报：HaselTweaks 的 tweak 名一直显示英文；实测该表 514 键里 383 键
///     已有社区中文、131 键缺 zh——缺的那些就是游戏里看到英文的）。
///     <para>
///     · 只收「有 en、缺 zh」的键；值套用资源值过滤（<see cref="UiStringExtractor.LooksTranslatableResourceValue" />），
///       JSON / 网址 / 长文档不进候选。
///     · 写回只补 zh 空的槽，**上游已有 zh 一律不覆盖**（与「磁盘本地化文件」同一口径）。
///     · 容器名 <c>json:&lt;资源名&gt;</c>、key 就是 JSON 里的键；生命周期同 <c>file:</c> 容器——
///       打过补丁后 zh 侧不再缺，但这批条目不许按「不在本轮候选」自动清账（否则刚打完就被排掉），
///       见 <see cref="UITextPack.PruneAgainstExtraction" /> 里的豁免。
///     </para>
/// </remarks>
internal static class UITextJSONResources
{
    /// <summary>资源条目容器前缀：<c>json:</c> + 程序集资源名（如 <c>HaselTweaks.Translations.json</c>）。</summary>
    public const string Prefix = "json:";

    /// <summary>要补的语言槽。</summary>
    public const string TargetLanguage = "zh";

    private const string FallbackLanguage = "en";

    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool IsJSONContainer(string container) =>
        container.StartsWith(Prefix, StringComparison.Ordinal);

    public static string ResourceNameOf(string container) => container[Prefix.Length..];

    /// <summary>
    ///     嗅探「语言表」形状：根是对象、每个值都是「语言 → 文本」的对象、至少一半条目有 <c>en</c>。
    ///     不满足就返回 false（不抛），避免把插件里随便一份数据 JSON 当成翻译表。
    /// </summary>
    public static bool TryParse(string json, out Dictionary<string, Dictionary<string, string>> table)
    {
        table = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(TrimBom(json));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var withEnglish = 0;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    table.Clear();
                    return false;
                }

                var languages = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var language in property.Value.EnumerateObject())
                {
                    if (language.Value.ValueKind != JsonValueKind.String)
                    {
                        table.Clear();
                        return false;
                    }

                    languages[language.Name] = language.Value.GetString() ?? string.Empty;
                }

                if (languages.ContainsKey(FallbackLanguage))
                {
                    withEnglish++;
                }

                table[property.Name] = languages;
            }

            if (table.Count == 0 || withEnglish * 2 < table.Count)
            {
                table.Clear();
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            table.Clear();
            return false;
        }
    }

    /// <summary>「有 en、缺 zh」的键 → 候选（键, 英文值）。</summary>
    public static List<(string Key, string Value)> CollectMissing(
        Dictionary<string, Dictionary<string, string>> table)
    {
        var list = new List<(string Key, string Value)>();
        foreach (var (key, languages) in table)
        {
            if (!languages.TryGetValue(FallbackLanguage, out var english) || string.IsNullOrWhiteSpace(english))
            {
                continue;
            }

            if (languages.TryGetValue(TargetLanguage, out var chinese) && !string.IsNullOrWhiteSpace(chinese))
            {
                continue;
            }

            list.Add((key, english));
        }

        return list;
    }

    /// <summary>
    ///     把译文补进 zh 槽（只在 zh 还是空的时候补）；一条都没改返回 null。
    ///     上游已有 zh 的键原样保留——不覆盖别人翻好的。
    /// </summary>
    public static string? Apply(string json, IReadOnlyDictionary<string, string> translations, out int changed)
    {
        changed = 0;
        try
        {
            if (JsonNode.Parse(TrimBom(json)) is not JsonObject root)
            {
                return null;
            }

            foreach (var (key, translated) in translations)
            {
                if (string.IsNullOrWhiteSpace(translated) || root[key] is not JsonObject entry)
                {
                    continue;
                }

                if (entry[TargetLanguage] is JsonValue current
                    && current.TryGetValue<string>(out var existing)
                    && !string.IsNullOrWhiteSpace(existing))
                {
                    // 上游已经有中文：不覆盖（与「磁盘本地化文件」同一口径）
                    continue;
                }

                entry[TargetLanguage] = translated.Trim();
                changed++;
            }

            return changed == 0 ? null : root.ToJsonString(SerializeOptions);
        }
        catch (Exception)
        {
            // 结构跟预期不一样、或写不进去：当作没改动，由调用方记明细
            return null;
        }
    }

    /// <summary>反向还原：当前 zh == 我们打过的译文 → 把 zh 槽去掉（回来时原样就没 zh）。</summary>
    public static string? Revert(string json, IReadOnlyDictionary<string, string> translations, out int changed)
    {
        changed = 0;
        try
        {
            if (JsonNode.Parse(TrimBom(json)) is not JsonObject root)
            {
                return null;
            }

            foreach (var (key, translated) in translations)
            {
                if (string.IsNullOrWhiteSpace(translated) || root[key] is not JsonObject entry)
                {
                    continue;
                }

                if (entry[TargetLanguage] is JsonValue current
                    && current.TryGetValue<string>(out var existing)
                    && string.Equals(existing, translated.Trim(), StringComparison.Ordinal))
                {
                    entry.Remove(TargetLanguage);
                    changed++;
                }
            }

            return changed == 0 ? null : root.ToJsonString(SerializeOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string TrimBom(string text) =>
        text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
}
