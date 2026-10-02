namespace FireGaze.UIText;

/// <summary>
///     待翻译的一条：字面量条目 / 资源条目 / 属性条目，三选一。
/// </summary>
internal sealed record UITextTarget(
    string Original,
    string? Context,
    UITextPackEntry? Entry,
    UITextResourceEntry? Resource,
    UITextAttributeEntry? Attribute = null);

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

        /// <summary>原文 → 这一轮的角色依据（拿胜利那条的；编辑器行里显示用）。</summary>
        public Dictionary<string, string> Reasons { get; } = new(StringComparer.Ordinal);

        public int UICount;

        public int AmbiguousCount;

        /// <summary>这一轮抽出来的资源型界面文字条数（容器 + key）。</summary>
        public int ResourceCount;

        /// <summary>这一轮抽出来的属性字符串条数（值去重后）。</summary>
        public int AttributeCount;

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
                result.Reasons[item.Original] = item.Reason;
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

            // 记下本轮的判定（UI / 灰名单）：打补丁时当闸门用。
            // 首次把「已有译文的条目」记成灰名单时要重打——它可能在升级前已经写进过 DLL，得按新口径撤出来。
            var newRole = item.Role == UITextRole.Ambiguous ? "Ambiguous" : "UI";
            if (entry.Role is null)
            {
                entry.Role = newRole;
                if (newRole == "Ambiguous" && entry.HasTranslation)
                {
                    result.ReapplyNeeded = true;
                }
            }
            else if (!string.Equals(entry.Role, newRole, StringComparison.Ordinal))
            {
                entry.Role = newRole;
                result.ReapplyNeeded = true;
            }

            entry.RoleReason = item.Reason;

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

        // 属性字符串（UIAttribute / TweakName…）：同样不参与 Roles，全部按候选。
        foreach (var item in extraction.Attributes)
        {
            if (pack.FindAttribute(item.Value) is null)
            {
                result.ReapplyNeeded = true;
            }

            pack.GetOrAddAttribute(item.Value, $"[属性] {item.Attribute} {item.Owner}");
            result.AttributeCount++;
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

        // 清账把条目改成「不翻」= 盘上的旧译文也得撤掉：不置 ReapplyNeeded 的话，
        // 列表页会以「已经是最新」短路，补丁里的旧译文永远留着（2026-10-03 评审 B-05）
        if (result.Prune.Any)
        {
            result.ReapplyNeeded = true;
        }

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

            var context = UITextLocalizationFiles.IsFileContainer(entry.Container)
                ? "本地化文件：" + UITextLocalizationFiles.RelativeOf(entry.Container) + " · " + entry.Key
                : UITextJSONResources.IsJSONContainer(entry.Container)
                    ? "内嵌 JSON：" + UITextJSONResources.ResourceNameOf(entry.Container) + " · " + entry.Key
                    : "资源：" + entry.Container + " · " + entry.Key;
            list.Add(new UITextTarget(entry.Original, context, null, entry));
        }

        foreach (var entry in pack.Attributes)
        {
            if (entry.HasTranslation || pack.IsAttributeSkipped(entry.Original))
            {
                continue;
            }

            list.Add(new UITextTarget(entry.Original, entry.Context, null, null, entry));
        }

        return list;
    }

    /// <summary>
    ///     一次云端包合并的预检结果（给「先看差异再应用」用）：新增 / 覆盖机器译 / 保留玩家译 / 无变化。
    /// </summary>
    public readonly record struct MergePreview(int Added, int Overwritten, int Protected, int Same);

    /// <summary>
    ///     先算一遍「云端包并进本机会发生什么」，不改任何东西。
    ///     人工译永不被覆盖（算 Protected）；相同译文算 Same；其余机器/库译文会被新值覆盖（算 Overwritten）。
    /// </summary>
    public static MergePreview PreviewMerge(UITextPack local, UITextPack incoming)
    {
        var added = 0;
        var overwritten = 0;
        var protect = 0;
        var same = 0;

        void Count(bool hasTarget, bool hasTranslation, bool isUser, string current, string next)
        {
            if (!hasTarget || !hasTranslation)
            {
                added++;
            }
            else if (isUser)
            {
                protect++;
            }
            else if (string.Equals(current, next, StringComparison.Ordinal))
            {
                same++;
            }
            else
            {
                overwritten++;
            }
        }

        foreach (var entry in incoming.Entries)
        {
            if (!entry.HasTranslation)
            {
                continue;
            }

            var target = local.Find(entry.Original);
            Count(target is not null, target?.HasTranslation == true, target?.IsUserSource == true, target?.Translated ?? string.Empty, entry.Translated);
        }

        foreach (var entry in incoming.Resources)
        {
            if (!entry.HasTranslation)
            {
                continue;
            }

            var target = local.FindResource(entry.Container, entry.Key);
            Count(target is not null, target?.HasTranslation == true, target?.IsUserSource == true, target?.Translated ?? string.Empty, entry.Translated);
        }

        foreach (var entry in incoming.Attributes)
        {
            if (!entry.HasTranslation)
            {
                continue;
            }

            var target = local.FindAttribute(entry.Original);
            Count(target is not null, target?.HasTranslation == true, target?.IsUserSource == true, target?.Translated ?? string.Empty, entry.Translated);
        }

        return new MergePreview(added, overwritten, protect, same);
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

            // 同值的其它 key 已有译文（可能是人工改过的）就不动它：本批只补缺
            //（2026-10-03 评审 B-04：资源按「原文值」匹配，一值多 key，旧行为会顶掉人工译文）
            if (entry.HasTranslation)
            {
                unchanged++;
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

        // 属性条目：按原文（字符串值）匹配。
        foreach (var entry in pack.Attributes)
        {
            if (!values.TryGetValue(entry.Original, out var value))
            {
                continue;
            }

            if (pack.IsAttributeSkipped(entry.Original))
            {
                continue;
            }

            // 同属性已有译文就不动（同上）
            if (entry.HasTranslation)
            {
                unchanged++;
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
