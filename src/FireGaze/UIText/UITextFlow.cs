namespace FireGaze.UIText;

/// <summary>
///     抽取结果合并 / 选翻译目标 / 收译文 —— 三步的共用逻辑。
/// </summary>
/// <remarks>
///     编辑窗口和「一键汉化」都走这里，保证两处口径一致（灰名单怎么算、PreserveID 怎么重算、
///     占位符怎么校验，只实现一份）。纯逻辑，不碰界面、不写盘。
/// </remarks>
internal static class UITextFlow
{
    /// <summary>
    ///     一次「抽取结果并进包」的结果。
    /// </summary>
    public sealed class MergeResult
    {
        /// <summary>原文 → 这一轮抽出来的角色（UI / 灰名单）。</summary>
        public Dictionary<string, UITextRole> Roles { get; } = new(StringComparer.Ordinal);

        public int UICount;

        public int AmbiguousCount;

        public UITextPackPruneOutcome Prune { get; set; } = new(0, 0, 0);
    }

    /// <summary>
    ///     把一次抽取并进包：补上新原文、按这一轮重算 PreserveID、清账（排除键名/空壳、恢复回候选的条目）。
    /// </summary>
    public static MergeResult MergeExtraction(UITextPack pack, UITextExtraction extraction)
    {
        var result = new MergeResult();
        var preserve = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in extraction.Entries)
        {
            if (item.Role == UITextRole.Excluded)
            {
                continue;
            }

            // 同一原文出现在多处时，只要有「候选」就算候选
            if (!result.Roles.TryGetValue(item.Original, out var existing)
                || (existing == UITextRole.Ambiguous && item.Role == UITextRole.UI))
            {
                result.Roles[item.Original] = item.Role;
            }

            preserve[item.Original] = preserve.GetValueOrDefault(item.Original) || item.PreserveID;
            var entry = pack.GetOrAdd(item.Original, item.Context, item.PreserveID);
            if (entry.Context is null)
            {
                entry.Context = item.Context;
            }

            if (item.Role == UITextRole.UI)
            {
                result.UICount++;
            }
            else
            {
                result.AmbiguousCount++;
            }
        }

        // PreserveID 是抽取的推导值，重新抽取要以这一轮为准（不能只 |=，判错过就永远换不掉）
        foreach (var (original, keepID) in preserve)
        {
            var entry = pack.Find(original);
            if (entry is not null)
            {
                entry.PreserveID = keepID;
            }
        }

        result.Prune = pack.PruneAgainstExtraction(extraction);
        return result;
    }

    /// <summary>
    ///     该翻哪些：没有译文、没被标「不翻」，且角色是候选（开了开关就连灰名单一起）。
    /// </summary>
    public static List<UITextPackEntry> TranslationTargets(UITextPack pack, IReadOnlyDictionary<string, UITextRole> roles, bool includeGrey)
    {
        var list = new List<UITextPackEntry>();
        foreach (var entry in pack.Entries)
        {
            if (entry.HasTranslation || pack.IsSkipped(entry.Original))
            {
                continue;
            }

            var role = roles.TryGetValue(entry.Original, out var found) ? found : UITextRole.UI;
            if (role == UITextRole.UI || (includeGrey && role == UITextRole.Ambiguous))
            {
                list.Add(entry);
            }
        }

        return list;
    }

    /// <summary>
    ///     把翻译通道的结果写回包里：清洗、占位符校验、来源标记。<br />
    ///     返回（写入条数、占位符对不上跳过的条数、原样返回的条数）。
    /// </summary>
    public static (int Applied, int PlaceholderRejected, int Unchanged) AcceptTranslations(
        UITextPack pack,
        IEnumerable<KeyValuePair<string, string>> translated,
        string channelName)
    {
        var applied = 0;
        var placeholderRejected = 0;
        var unchanged = 0;
        foreach (var (original, value) in translated)
        {
            var entry = pack.Find(original);
            if (entry is null)
            {
                continue;
            }

            var clean = UITextText.CleanTranslated(value);
            if (string.IsNullOrWhiteSpace(clean)
                || string.Equals(clean, UITextText.ForTranslation(original), StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            var problem = UITextText.CheckPlaceholders(original, clean);
            if (problem is not null)
            {
                entry.Review = problem;
                placeholderRejected++;
                continue;
            }

            entry.Translated = clean;
            entry.Source = "ai:" + channelName;
            entry.Review = null;
            applied++;
        }

        return (applied, placeholderRejected, unchanged);
    }
}
