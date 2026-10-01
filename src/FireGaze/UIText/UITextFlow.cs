namespace FireGaze.UIText;

/// <summary>
///     待翻译的一条：要么是字面量条目（<see cref="Entry" />），要么是资源条目（<see cref="Resource" />），二选一。
/// </summary>
internal sealed record UITextTarget(string Original, string? Context, UITextPackEntry? Entry, UITextResourceEntry? Resource);

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

        /// <summary>这一轮抽出来的资源型界面文字条数（容器 + key）。</summary>
        public int ResourceCount;

        public UITextPackPruneOutcome Prune { get; set; } = new(0, 0, 0);

        /// <summary>
        ///     这一轮抽取是不是改动了包（新增条目 / 某条的 PreserveID 被改对）。
        ///     改过就说明盘上的补丁可能已经过时（例如 2026-10-01 的 hint 误加 ###），要重打一次。
        /// </summary>
        public bool ReapplyNeeded;
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
            var existed = pack.Find(item.Original) is not null;
            var entry = pack.GetOrAdd(item.Original, item.Context, item.PreserveID);
            if (!existed)
            {
                result.ReapplyNeeded = true;
            }

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

        // 资源型界面文字（.resx / ResourceManager）：身份是「容器 + key」，值才是原文。
        // 不参与 Roles（灰名单口径只管字面量）；全部按候选处理，用户在编辑器里可以单条标「不翻」。
        foreach (var item in extraction.Resources)
        {
            if (pack.FindResource(item.Container, item.Key) is null)
            {
                result.ReapplyNeeded = true;
            }

            pack.GetOrAddResource(item.Container, item.Key, item.Value);
            result.ResourceCount++;
        }

        // PreserveID 是抽取的推导值，重新抽取要以这一轮为准（不能只 |=，判错过就永远换不掉）
        foreach (var (original, keepID) in preserve)
        {
            var entry = pack.Find(original);
            if (entry is null)
            {
                continue;
            }

            if (entry.PreserveID != keepID)
            {
                // PreserveID 变了 = 盘上的补丁写法（要不要 ###原文）已经不对，必须重打
                result.ReapplyNeeded = true;
                entry.PreserveID = keepID;
            }
        }

        result.Prune = pack.PruneAgainstExtraction(extraction);
        return result;
    }

    /// <summary>
    ///     该翻哪些：没有译文、没被标「不翻」，且角色是候选（开了开关就连灰名单一起）。
    ///     资源条目全部算候选（判定器已把不可翻的值滤掉）。
    /// </summary>
    public static List<UITextTarget> TranslationTargets(UITextPack pack, IReadOnlyDictionary<string, UITextRole> roles, bool includeGrey)
    {
        var list = new List<UITextTarget>();
        foreach (var entry in pack.Entries)
        {
            if (entry.HasTranslation || pack.IsSkipped(entry.Original))
            {
                continue;
            }

            var role = roles.TryGetValue(entry.Original, out var found) ? found : UITextRole.UI;
            if (role == UITextRole.UI || (includeGrey && role == UITextRole.Ambiguous))
            {
                list.Add(new UITextTarget(entry.Original, entry.Context, entry, null));
            }
        }

        foreach (var entry in pack.Resources)
        {
            if (entry.HasTranslation || pack.IsResourceSkipped(entry.Container, entry.Key))
            {
                continue;
            }

            var context = "资源：" + entry.Container + " · " + entry.Key;
            list.Add(new UITextTarget(entry.Original, context, null, entry));
        }

        return list;
    }

    /// <summary>
    ///     把目标列表转成发给翻译通道的条目：**按原文去重**（多个 key 共用同一英文值时只翻一次）。
    /// </summary>
    public static List<UITextTranslateItem> BuildTranslateItems(IReadOnlyList<UITextTarget> targets)
    {
        var items = new List<UITextTranslateItem>(targets.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (!seen.Add(target.Original))
            {
                continue;
            }

            items.Add(new UITextTranslateItem(target.Original, target.Context));
        }

        return items;
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

        // 先固化一份原文 → 译文（通道返回的是字典；接口只保证 IEnumerable，不能依赖多次枚举）
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (original, value) in translated)
        {
            values[original] = value;
        }

        foreach (var (original, value) in values)
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

        // 资源条目：译文同样按原文匹配（多个 key 共用同一值时一次写入多处），
        // 已标「不翻」的不写（打补丁也不会碰）。
        foreach (var entry in pack.Resources)
        {
            if (!values.TryGetValue(entry.Original, out var value))
            {
                continue;
            }

            if (pack.IsResourceSkipped(entry.Container, entry.Key))
            {
                continue;
            }

            var clean = UITextText.CleanTranslated(value);
            if (string.IsNullOrWhiteSpace(clean)
                || string.Equals(clean, UITextText.ForTranslation(entry.Original), StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            var problem = UITextText.CheckPlaceholders(entry.Original, clean);
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
