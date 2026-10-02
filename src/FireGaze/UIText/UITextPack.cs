using System.Text.Json;
using System.Text.Json.Serialization;

namespace FireGaze.UIText;

/// <summary>
///     插件内部文本的一条译文（键 = 原文）。
/// </summary>
internal sealed class UITextPackEntry
{
    [JsonPropertyName("Original")]
    public string Original { get; set; } = string.Empty;

    [JsonPropertyName("Translated")]
    public string Translated { get; set; } = string.Empty;

    /// <summary>
    ///     代码位置（类型.方法），只给人工核对用；打补丁时按原文匹配，不依赖它。
    /// </summary>
    [JsonPropertyName("Context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; set; }

    /// <summary>
    ///     译文来源：<c>user</c>（人工改过或玩家贡献，最优先）/ <c>ai</c>（机器翻译）/ <c>library</c>（从公共配套库下载）/ 空（按 ai 对待）。
    /// </summary>
    [JsonPropertyName("Source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>
    ///     原文变过、译文待复核时的说明。
    /// </summary>
    [JsonPropertyName("Review")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Review { get; set; }

    /// <summary>
    ///     这个字面量被 ImGui 当控件 ID 用：打补丁要写成「译文###原文」，把 ID 留在原文上。
    /// </summary>
    [JsonPropertyName("PreserveID")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PreserveID { get; set; }

    /// <summary>
    ///     最近一次抽取的判定：<c>UI</c> / <c>Ambiguous</c>（灰名单）。空 = 按 UI（旧包 / 公共库）。
    ///     打补丁时用它做闸门：灰名单只有在「连灰名单一起翻」或玩家自己译过时才会写进 DLL。
    /// </summary>
    [JsonPropertyName("Role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    /// <summary>判定的依据（灰名单的原因，给编辑器/排查用）。</summary>
    [JsonPropertyName("RoleReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoleReason { get; set; }

    [JsonIgnore]
    public bool IsAmbiguous => string.Equals(this.Role, "Ambiguous", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     判定为「不该翻」（键名、本地化 / 资源查表 key、功能串）：不进翻译候选；补丁层也硬拦。
    /// </summary>
    [JsonIgnore]
    public bool IsExcluded => string.Equals(this.Role, "Excluded", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     这一条能不能打进补丁：UI 永远能；灰名单只在「连灰名单一起翻」开着、或玩家自己译过时能；
    ///     Excluded 一律不（除玩家自己译过）——从源头上拦住「库里的噪声译文 / 盘上残留状态」把键名写坏。
    ///     没有判定（旧包 / 公共库）按 UI 对待。
    /// </summary>
    public bool IsPatchable(bool includeAmbiguous)
    {
        if (this.IsExcluded)
        {
            return this.IsUserSource;
        }

        return !this.IsAmbiguous || includeAmbiguous || this.IsUserSource;
    }

    [JsonIgnore]
    public bool IsUserSource => string.Equals(Source, "user", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translated);
}

/// <summary>
///     配套包的元数据。
/// </summary>
internal sealed class UITextPackMeta
{
    /// <summary>
    ///     包格式版本（将来改结构时用）。
    /// </summary>
    [JsonPropertyName("format")]
    public int Format { get; set; } = 1;

    /// <summary>
    ///     最后修改时间（本地包是编辑时间，公共包是收录时间）。
    /// </summary>
    [JsonPropertyName("updatedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedAt { get; set; }

    /// <summary>
    ///     这个包是对着哪个插件版本抽的（只作参考，匹配按文本内容）。
    /// </summary>
    [JsonPropertyName("pluginVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginVersion { get; set; }

    /// <summary>
    ///     <c>local</c>（本机编辑）/ <c>library</c>（公共配套库）。
    /// </summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }
}

/// <summary>
///     资源型本地化（内嵌 <c>.resources</c> 容器）里的一条译文。
/// </summary>
/// <remarks>
///     身份是「容器名 + key」，不是原文——同一个英文值可能落在多个 key 下，
///     而且插件更新后 key 才是稳定的（值会改）。
/// </remarks>
internal sealed class UITextResourceEntry
{
    [JsonPropertyName("Container")]
    public string Container { get; set; } = string.Empty;

    [JsonPropertyName("Key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>抽取时的英文值（插件更新后值会变，这里跟着更新并打 Review）。</summary>
    [JsonPropertyName("Original")]
    public string Original { get; set; } = string.Empty;

    [JsonPropertyName("Translated")]
    public string Translated { get; set; } = string.Empty;

    /// <summary>译文来源：<c>user</c> / <c>ai</c> / <c>library</c>（与字面量条目同一套口径）。</summary>
    [JsonPropertyName("Source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>原文变过、译文待复核时的说明。</summary>
    [JsonPropertyName("Review")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Review { get; set; }

    [JsonIgnore]
    public bool IsUserSource => string.Equals(this.Source, "user", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasTranslation => !string.IsNullOrWhiteSpace(this.Translated);
}

/// <summary>
///     自定义特性参数里的一条译文（身份 = 字符串值本身）。
/// </summary>
internal sealed class UITextAttributeEntry
{
    [JsonPropertyName("Original")]
    public string Original { get; set; } = string.Empty;

    [JsonPropertyName("Translated")]
    public string Translated { get; set; } = string.Empty;

    /// <summary>来源提示（<c>[属性] UIAttribute 类型::成员</c>），只给人工核对用。</summary>
    [JsonPropertyName("Context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Context { get; set; }

    [JsonPropertyName("Source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    [JsonPropertyName("Review")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Review { get; set; }

    [JsonIgnore]
    public bool IsUserSource => string.Equals(this.Source, "user", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasTranslation => !string.IsNullOrWhiteSpace(this.Translated);
}

/// <summary>
///     一次清账的结果：系统排掉多少条、恢复多少条、清掉多少条空壳。
/// </summary>
internal sealed record UITextPackPruneOutcome(int Pruned, int Restored, int Removed)
{
    /// <summary>这次清账有没有动过包。</summary>
    public bool Any => this.Pruned > 0 || this.Restored > 0 || this.Removed > 0;

    /// <summary>给界面用的一句话（没动过就返回空串）。</summary>
    public string Describe()
    {
        var parts = new List<string>(3);
        if (this.Pruned > 0)
        {
            parts.Add($"排除 {this.Pruned} 条");
        }

        if (this.Restored > 0)
        {
            parts.Add($"恢复 {this.Restored} 条");
        }

        if (this.Removed > 0)
        {
            parts.Add($"清掉 {this.Removed} 条空壳");
        }

        return parts.Count == 0 ? string.Empty : "清账：" + string.Join(" · ", parts);
    }
}

/// <summary>
///     一个插件的内部文本包：原文 → 译文，另有「用户标记不翻」的名单。
/// </summary>
internal sealed class UITextPack
{
    /// <summary>
    ///     当前包格式版本。
    /// </summary>
    /// <remarks>
    ///     v2（2026-10-02）：v1 的 <c>PreserveID</c> 把**拼接片段**也标了（ARSR 的 <c>".Name"</c> 经
    ///     <c>string.Concat</c> 拼进 ImGui label），打补丁写成 <c>.名称###.Name</c>——<c>###</c> 之后才算 ID，
    ///     整个窗口控件同 ID、拉一个滑块全部动。v1 → v2 迁移把旧包的 PreserveID 全部清零，
    ///     后续任何重打（含自动重打）都不会再写错；原文自带 <c>###</c> 的条目不受影响（打补丁走保留原 ID 那条路）。
    /// </remarks>
    public const int CurrentFormat = 2;

    [JsonPropertyName("_meta")]
    public UITextPackMeta Meta { get; set; } = new();

    [JsonPropertyName("entries")]
    public List<UITextPackEntry> Entries { get; set; } = [];

    /// <summary>
    ///     资源型本地化（内嵌 <c>.resources</c>）的译文条目。
    /// </summary>
    [JsonPropertyName("resources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<UITextResourceEntry> Resources { get; set; } = [];

    /// <summary>
    ///     自定义特性参数里的译文条目。
    /// </summary>
    [JsonPropertyName("attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<UITextAttributeEntry> Attributes { get; set; } = [];

    /// <summary>
    ///     用户明确标记「不翻」的原文（留在包里，编辑器要记住，也不参与覆盖率统计的分母口径之外）。
    /// </summary>
    [JsonPropertyName("skipped")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<string> Skipped { get; set; } = [];

    /// <summary>
    ///     用户标记「不翻」的资源条目，格式 = <c>容器\u0001key</c>（容器名里不会出现这个字符）。
    /// </summary>
    [JsonPropertyName("skippedResources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<string> SkippedResources { get; set; } = [];

    /// <summary>
    ///     用户标记「不翻」的属性条目（按原文）。
    /// </summary>
    [JsonPropertyName("skippedAttributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<string> SkippedAttributes { get; set; } = [];

    private Dictionary<string, UITextPackEntry>? index;
    private Dictionary<(string Container, string Key), UITextResourceEntry>? resourceIndex;
    private Dictionary<string, UITextAttributeEntry>? attributeIndex;

    [JsonIgnore]
    public int TranslatedCount => this.Entries.Count(e => e.HasTranslation);

    [JsonIgnore]
    public int TranslatedResourceCount => this.Resources.Count(e => e.HasTranslation);

    [JsonIgnore]
    public int TranslatedAttributeCount => this.Attributes.Count(e => e.HasTranslation);

    [JsonIgnore]
    public int TranslatedTotal => this.TranslatedCount + this.TranslatedResourceCount + this.TranslatedAttributeCount;

    [JsonIgnore]
    public int UntranslatedTotal =>
        this.Entries.Count(e => !e.HasTranslation && !this.IsSkipped(e.Original))
        + this.Resources.Count(e => !e.HasTranslation && !this.IsResourceSkipped(e.Container, e.Key))
        + this.Attributes.Count(e => !e.HasTranslation && !this.IsAttributeSkipped(e.Original));

    /// <summary>
    ///     按「容器 + key」取一条资源译文（没有就返回 null）。
    /// </summary>
    public UITextResourceEntry? FindResource(string container, string key)
    {
        this.EnsureResourceIndex();
        return this.resourceIndex!.TryGetValue((container, key), out var entry) ? entry : null;
    }

    /// <summary>
    ///     取一条资源译文、没有就建。原文变了时更新 <see cref="UITextResourceEntry.Original" /> 并打「待复核」；
    ///     key 才是身份，所以译文保留（打补丁按 key 写，不会把旧译文写到别的文本上）。
    /// </summary>
    public UITextResourceEntry GetOrAddResource(string container, string key, string original)
    {
        var entry = this.FindResource(container, key);
        if (entry is not null)
        {
            if (!string.Equals(entry.Original, original, StringComparison.Ordinal))
            {
                entry.Original = original;
                if (entry.HasTranslation)
                {
                    entry.Review = "原文改过了，译文待复核";
                }
            }

            return entry;
        }

        entry = new UITextResourceEntry { Container = container, Key = key, Original = original };
        this.Resources.Add(entry);
        this.resourceIndex![(container, key)] = entry;
        return entry;
    }

    /// <summary>删掉一条资源译文（重新抽取后容器/key 没了、且本身是空壳时清账用）。</summary>
    public bool RemoveResource(string container, string key)
    {
        this.EnsureResourceIndex();
        if (!this.resourceIndex!.Remove((container, key)))
        {
            return false;
        }

        this.Resources.RemoveAll(e => e.Container == container && e.Key == key);
        return true;
    }

    /// <summary>「不翻」名单里资源条目用的拼合键。</summary>
    public static string ResourceSkipToken(string container, string key) => container + "\u0001" + key;

    public bool IsResourceSkipped(string container, string key) =>
        this.SkippedResources.Contains(ResourceSkipToken(container, key), StringComparer.Ordinal);

    public void MarkResourceSkipped(string container, string key)
    {
        var token = ResourceSkipToken(container, key);
        if (!this.SkippedResources.Contains(token, StringComparer.Ordinal))
        {
            this.SkippedResources.Add(token);
        }
    }

    public void UnmarkResourceSkipped(string container, string key) =>
        this.SkippedResources.RemoveAll(s => string.Equals(s, ResourceSkipToken(container, key), StringComparison.Ordinal));

    /// <summary>按原文（字符串值）取一条属性译文；没有就返回 null。</summary>
    public UITextAttributeEntry? FindAttribute(string original)
    {
        this.EnsureAttributeIndex();
        return this.attributeIndex!.TryGetValue(original, out var entry) ? entry : null;
    }

    /// <summary>取一条属性译文、没有就建；Context 只补空。</summary>
    public UITextAttributeEntry GetOrAddAttribute(string original, string? context)
    {
        var entry = this.FindAttribute(original);
        if (entry is not null)
        {
            if (entry.Context is null && context is not null)
            {
                entry.Context = context;
            }

            return entry;
        }

        entry = new UITextAttributeEntry { Original = original, Context = context };
        this.Attributes.Add(entry);
        this.attributeIndex![original] = entry;
        return entry;
    }

    /// <summary>删掉一条属性译文（空壳清账用）。</summary>
    public bool RemoveAttribute(string original)
    {
        this.EnsureAttributeIndex();
        if (!this.attributeIndex!.Remove(original))
        {
            return false;
        }

        this.Attributes.RemoveAll(e => string.Equals(e.Original, original, StringComparison.Ordinal));
        return true;
    }

    public bool IsAttributeSkipped(string original) => this.SkippedAttributes.Contains(original, StringComparer.Ordinal);

    public void MarkAttributeSkipped(string original)
    {
        if (!this.SkippedAttributes.Contains(original, StringComparer.Ordinal))
        {
            this.SkippedAttributes.Add(original);
        }
    }

    public void UnmarkAttributeSkipped(string original) =>
        this.SkippedAttributes.RemoveAll(s => string.Equals(s, original, StringComparison.Ordinal));

    /// <summary>
    ///     按原文取一条（没有就返回 null）。索引失准时回退到线性查找，不问自愈。
    /// </summary>
    public UITextPackEntry? Find(string original)
    {
        this.EnsureIndex();
        if (this.index!.TryGetValue(original, out var entry))
        {
            return entry;
        }

        entry = this.Entries.Find(e => string.Equals(e.Original, original, StringComparison.Ordinal));
        if (entry is not null)
        {
            this.index[original] = entry;
        }

        return entry;
    }

    /// <summary>
    ///     取一条、没有就建（用于从抽取结果播种）。
    /// </summary>
    public UITextPackEntry GetOrAdd(string original, string? context, bool preserveID)
    {
        var entry = this.Find(original);
        if (entry is not null)
        {
            if (entry.Context is null && context is not null)
            {
                entry.Context = context;
            }

            entry.PreserveID |= preserveID;
            return entry;
        }

        entry = new UITextPackEntry { Original = original, Context = context, PreserveID = preserveID };
        this.Entries.Add(entry);
        this.index![original] = entry;
        return entry;
    }

    /// <summary>
    ///     删掉一条（重新抽取后原文没了时清账用）。
    /// </summary>
    public bool Remove(string original)
    {
        this.EnsureIndex();
        if (!this.index!.Remove(original))
        {
            return false;
        }

        this.Entries.RemoveAll(e => string.Equals(e.Original, original, StringComparison.Ordinal));
        return true;
    }

    /// <summary>
    ///     自动清账写在条目备注里的前缀。看到它就说明这条「不翻」是系统排的、可以自动恢复；
    ///     用户手动标的条目不写备注，重新抽取也不会去动它。
    /// </summary>
    public const string AutoSkipNotePrefix = "自动排除：";

    /// <summary>
    ///     这条「不翻」是不是系统自动排的（能自动恢复）。兼容 v1.2.0.37 用过的旧备注文案。
    /// </summary>
    public static bool IsAutoSkipped(UITextPackEntry entry) => IsAutoSkipReview(entry.Review);

    /// <summary>资源条目的同一判断（备注文案口径一致）。</summary>
    public static bool IsResourceAutoSkipped(UITextResourceEntry entry) => IsAutoSkipReview(entry.Review);

    /// <summary>属性条目的同一判断（备注文案口径一致）。</summary>
    public static bool IsAttributeAutoSkipped(UITextAttributeEntry entry) => IsAutoSkipReview(entry.Review);

    /// <summary>
    ///     「库合并留下的不翻残留」：库合并（v1.2.0.94 之前）清掉了自动排除的备注、却没把条目
    ///     从「不翻」名单里撤掉——形态 = 备注为空 + 有译文 + 来源是公共库。
    ///     这类条目重新成为候选时应当自动恢复，否则永久卡死：有译文、界面却一直英文
    ///     （2026-10-02 Allagan Tools 实机 509 条）。
    /// </summary>
    public static bool IsLibraryLeftOver(UITextPackEntry entry) =>
        entry.Review is null
        && entry.HasTranslation
        && string.Equals(entry.Source, "library", StringComparison.OrdinalIgnoreCase);

    private static bool IsAutoSkipReview(string? review) =>
        review is not null
        && (review.StartsWith(AutoSkipNotePrefix, StringComparison.Ordinal)
            || review.StartsWith("新一轮抽取已排除", StringComparison.Ordinal));

    /// <summary>
    ///     标记「不翻」。
    /// </summary>
    public void MarkSkipped(string original)
    {
        if (!this.Skipped.Contains(original, StringComparer.Ordinal))
        {
            this.Skipped.Add(original);
        }
    }

    /// <summary>
    ///     取消「不翻」。
    /// </summary>
    public void UnmarkSkipped(string original) => this.Skipped.RemoveAll(s => string.Equals(s, original, StringComparison.Ordinal));

    [JsonIgnore]
    public bool HasSkipped => this.Skipped.Count > 0;

    public bool IsSkipped(string original) => this.Skipped.Contains(original, StringComparer.Ordinal);

    /// <summary>
    ///     把公共配套库的包并进来：**本地人工改过的（user）永不被顶**；已有的 ai / library 可以被库里的更新覆盖。
    ///     返回「真正写进去的条数」（含更新；供界面判断要不要重打补丁）。
    /// </summary>
    public int MergeLibrary(UITextPack library)
    {
        var changed = 0;
        this.EnsureIndex();
        foreach (var incoming in library.Entries)
        {
            var existing = this.Find(incoming.Original);
            if (existing is null)
            {
                this.Entries.Add(new UITextPackEntry
                {
                    Original = incoming.Original,
                    Translated = incoming.Translated,
                    Context = incoming.Context,
                    Source = incoming.Source ?? "library",
                    PreserveID = incoming.PreserveID,
                });
                this.index![incoming.Original] = this.Entries[^1];
                if (incoming.HasTranslation)
                {
                    changed++;
                }

                continue;
            }

            if (existing.IsUserSource)
            {
                continue;
            }

            if (incoming.HasTranslation)
            {
                // 「自动排除」的条目拿到库译文：说明它确实是一条要翻的界面文本——
                // 把「不翻」一起摘掉。v1.2.0.94 之前只清 Review、skipped 残留会把条目永久卡死：
                // 有译文、界面却一直英文（2026-10-02 Allagan Tools 实机 509 条）。
                var wasAutoSkipped = this.IsSkipped(existing.Original) && IsAutoSkipped(existing);

                if (!string.Equals(existing.Translated, incoming.Translated, StringComparison.Ordinal))
                {
                    changed++;
                }

                existing.Translated = incoming.Translated;
                existing.Source = incoming.Source ?? "library";
                existing.Review = null;
                if (wasAutoSkipped)
                {
                    this.UnmarkSkipped(existing.Original);
                }
            }

            if (incoming.Context is not null)
            {
                existing.Context = incoming.Context;
            }

            existing.PreserveID |= incoming.PreserveID;
        }

        foreach (var skipped in library.Skipped)
        {
            this.MarkSkipped(skipped);
        }

        // 资源条目：同一套优先级——玩家自己改过的（user）永不被库顶掉；ai / library 可以被库更新覆盖。
        foreach (var incoming in library.Resources)
        {
            var existing = this.FindResource(incoming.Container, incoming.Key);
            if (existing is null)
            {
                this.Resources.Add(new UITextResourceEntry
                {
                    Container = incoming.Container,
                    Key = incoming.Key,
                    Original = incoming.Original,
                    Translated = incoming.Translated,
                    Source = incoming.Source ?? "library",
                });
                this.resourceIndex![(incoming.Container, incoming.Key)] = this.Resources[^1];
                if (incoming.HasTranslation)
                {
                    changed++;
                }

                continue;
            }

            if (existing.IsUserSource)
            {
                continue;
            }

            if (incoming.HasTranslation)
            {
                // 同 entries：库译文进来时，把「自动排除」残留的「不翻」一并摘掉。
                var wasAutoSkipped = this.IsResourceSkipped(existing.Container, existing.Key)
                                     && IsResourceAutoSkipped(existing);

                if (!string.Equals(existing.Translated, incoming.Translated, StringComparison.Ordinal))
                {
                    changed++;
                }

                existing.Translated = incoming.Translated;
                existing.Source = incoming.Source ?? "library";
                existing.Review = null;
                if (wasAutoSkipped)
                {
                    this.UnmarkResourceSkipped(existing.Container, existing.Key);
                }
            }

            if (incoming.Original.Length > 0)
            {
                existing.Original = incoming.Original;
            }
        }

        foreach (var skipped in library.SkippedResources)
        {
            if (!this.SkippedResources.Contains(skipped, StringComparer.Ordinal))
            {
                this.SkippedResources.Add(skipped);
            }
        }

        // 属性条目：同一套优先级
        foreach (var incoming in library.Attributes)
        {
            var existing = this.FindAttribute(incoming.Original);
            if (existing is null)
            {
                this.Attributes.Add(new UITextAttributeEntry
                {
                    Original = incoming.Original,
                    Translated = incoming.Translated,
                    Context = incoming.Context,
                    Source = incoming.Source ?? "library",
                });
                this.attributeIndex![incoming.Original] = this.Attributes[^1];
                if (incoming.HasTranslation)
                {
                    changed++;
                }

                continue;
            }

            if (existing.IsUserSource)
            {
                continue;
            }

            if (incoming.HasTranslation)
            {
                // 同 entries：库译文进来时，把「自动排除」残留的「不翻」一并摘掉。
                var wasAutoSkipped = this.IsAttributeSkipped(existing.Original)
                                     && IsAttributeAutoSkipped(existing);

                if (!string.Equals(existing.Translated, incoming.Translated, StringComparison.Ordinal))
                {
                    changed++;
                }

                existing.Translated = incoming.Translated;
                existing.Source = incoming.Source ?? "library";
                existing.Review = null;
                if (wasAutoSkipped)
                {
                    this.UnmarkAttributeSkipped(existing.Original);
                }
            }

            if (incoming.Context is not null)
            {
                existing.Context = incoming.Context;
            }
        }

        foreach (var skipped in library.SkippedAttributes)
        {
            this.MarkAttributeSkipped(skipped);
        }

        return changed;
    }

    /// <summary>
    ///     按新一次抽取结果清账：**这次没被列为 候选/灰名单 的条目一律标成「不翻」**，不再打进补丁。
    ///     用来收拾历史上被误判成 UI 的键名/功能串（比如本地化 key 被翻成中文，导致插件的查找失效）。
    /// </summary>
    /// <remarks>
    ///     两个方向都要管：<br />
    ///     ① 又变回候选的条目要**恢复**——只恢复系统自己排过的（带自动备注），用户手动标的「不翻」绝不碰；<br />
    ///     ② 没有译文、也没有来源的空壳条目直接从包里清掉——多半是上一轮对着「已经打过补丁的 DLL」
    ///     抽出来的噪声（<c>译文###原文</c>），留在包里只会污染界面。
    /// </remarks>
    public UITextPackPruneOutcome PruneAgainstExtraction(UITextExtraction extraction)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in extraction.Entries)
        {
            if (item.Role != UITextRole.Excluded)
            {
                keep.Add(item.Original);
            }
        }

        var keepResources = new HashSet<(string Container, string Key)>();
        foreach (var item in extraction.Resources)
        {
            keepResources.Add((item.Container, item.Key));
        }

        var keepAttributes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in extraction.Attributes)
        {
            keepAttributes.Add(item.Value);
        }

        var pruned = 0;
        var restored = 0;
        var dropped = new List<string>();
        foreach (var entry in this.Entries)
        {
            if (keep.Contains(entry.Original))
            {
                // 又变回候选了：恢复「系统自动排除」的；用户手动标的「不翻」保持不动。
                // IsLibraryLeftOver：库合并清备注留下的残留（同属系统侧），一并恢复。
                if (this.IsSkipped(entry.Original) && (IsAutoSkipped(entry) || IsLibraryLeftOver(entry)))
                {
                    this.UnmarkSkipped(entry.Original);
                    entry.Review = null;
                    restored++;
                }

                continue;
            }

            if (this.IsSkipped(entry.Original) && !IsAutoSkipped(entry))
            {
                continue;
            }

            if (!entry.HasTranslation && entry.Source is null)
            {
                // 没有译文、也没有来源：纯空壳——清掉（自动不翻的会连不翻名单一起摘掉）
                dropped.Add(entry.Original);
                continue;
            }

            this.MarkSkipped(entry.Original);
            entry.Review = AutoSkipNotePrefix + "已不在新一轮抽取的候选里（键名/功能串，或插件版本变了），不会打进补丁";
            pruned++;
        }

        foreach (var original in dropped)
        {
            this.UnmarkSkipped(original);
            this.Remove(original);
        }

        // 资源条目同一套清账：容器/key 不在了或值变成不可翻的（JSON 之类）→ 自动排掉；
        // 又回来看见 → 恢复；空壳清掉。
        var droppedResources = new List<(string Container, string Key)>();
        foreach (var entry in this.Resources)
        {
            if (keepResources.Contains((entry.Container, entry.Key)))
            {
                if (this.IsResourceSkipped(entry.Container, entry.Key) && IsResourceAutoSkipped(entry))
                {
                    this.UnmarkResourceSkipped(entry.Container, entry.Key);
                    entry.Review = null;
                    restored++;
                }

                continue;
            }

            // 插件自带的本地化文件：候选由「中文侧缺的键」决定，打过补丁之后那些键就不缺了——
            // 绝不能按「不在本轮候选里」自动排掉（否则刚打完立刻被标「不翻」）。
            // 它的生命周期交给写入规则：中文侧已有别的译文的键不覆盖。
            if (UITextLocalizationFiles.IsFileContainer(entry.Container))
            {
                continue;
            }

            if (this.IsResourceSkipped(entry.Container, entry.Key) && !IsResourceAutoSkipped(entry))
            {
                continue;
            }

            if (!entry.HasTranslation && entry.Source is null)
            {
                droppedResources.Add((entry.Container, entry.Key));
                continue;
            }

            this.MarkResourceSkipped(entry.Container, entry.Key);
            entry.Review = AutoSkipNotePrefix + "已不在新一轮抽取的资源里（容器/key 变了，或值已不是界面文字）";
            pruned++;
        }

        foreach (var (container, key) in droppedResources)
        {
            this.UnmarkResourceSkipped(container, key);
            this.RemoveResource(container, key);
        }

        // 属性条目：同一套清账（值不再出现在任何 UI 特性里 → 自动排掉；又出现 → 恢复）。
        var droppedAttributes = new List<string>();
        foreach (var entry in this.Attributes)
        {
            if (keepAttributes.Contains(entry.Original))
            {
                if (this.IsAttributeSkipped(entry.Original) && IsAutoSkipReview(entry.Review))
                {
                    this.UnmarkAttributeSkipped(entry.Original);
                    entry.Review = null;
                    restored++;
                }

                continue;
            }

            if (this.IsAttributeSkipped(entry.Original) && !IsAutoSkipReview(entry.Review))
            {
                continue;
            }

            if (!entry.HasTranslation && entry.Source is null)
            {
                droppedAttributes.Add(entry.Original);
                continue;
            }

            this.MarkAttributeSkipped(entry.Original);
            entry.Review = AutoSkipNotePrefix + "已不在新一轮抽取的 UI 特性里";
            pruned++;
        }

        foreach (var original in droppedAttributes)
        {
            this.UnmarkAttributeSkipped(original);
            this.RemoveAttribute(original);
        }

        return new UITextPackPruneOutcome(pruned, restored, dropped.Count + droppedResources.Count + droppedAttributes.Count);
    }

    /// <summary>
    ///     用一次抽取结果更新包：补上新出现的原文（不带译文）；原文消失的条目不删（可能只是换版本），
    ///     但会清掉它们的 Context 免得误导。
    /// </summary>
    public void ApplyExtraction(UITextExtraction extraction)
    {
        foreach (var item in extraction.Entries)
        {
            if (item.Role == UITextRole.Excluded)
            {
                continue;
            }

            this.GetOrAdd(item.Original, item.Context, item.PreserveID);
        }

        foreach (var item in extraction.Resources)
        {
            this.GetOrAddResource(item.Container, item.Key, item.Value);
        }

        foreach (var item in extraction.Attributes)
        {
            this.GetOrAddAttribute(item.Value, $"[属性] {item.Attribute} {item.Owner}");
        }
    }

    /// <summary>
    ///     清掉索引（从 JSON 反序列化后要重建）。
    /// </summary>
    public void InvalidateIndex()
    {
        this.index = null;
        this.resourceIndex = null;
        this.attributeIndex = null;
    }

    private void EnsureIndex()
    {
        if (this.index is not null)
        {
            return;
        }

        this.index = new Dictionary<string, UITextPackEntry>(this.Entries.Count, StringComparer.Ordinal);
        foreach (var entry in this.Entries)
        {
            this.index[entry.Original] = entry;
        }
    }

    private void EnsureResourceIndex()
    {
        if (this.resourceIndex is not null)
        {
            return;
        }

        this.resourceIndex = new Dictionary<(string Container, string Key), UITextResourceEntry>(this.Resources.Count);
        foreach (var entry in this.Resources)
        {
            this.resourceIndex[(entry.Container, entry.Key)] = entry;
        }
    }

    private void EnsureAttributeIndex()
    {
        if (this.attributeIndex is not null)
        {
            return;
        }

        this.attributeIndex = new Dictionary<string, UITextAttributeEntry>(this.Attributes.Count, StringComparer.Ordinal);
        foreach (var entry in this.Attributes)
        {
            this.attributeIndex[entry.Original] = entry;
        }
    }

    private static readonly JsonSerializerOptions JSONOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    ///     序列化成 UTF-8 JSON 文本（带末尾换行，与仓库里其它 JSON 一致）。
    /// </summary>
    public string ToJSON()
    {
        // 写盘总是落到当前格式（v1 包只要被保存过一次就完成迁移）
        this.Meta.Format = CurrentFormat;
        return JsonSerializer.Serialize(this, JSONOptions) + Environment.NewLine;
    }

    /// <summary>
    ///     从 JSON 读一个包；读不出来时返回 null 并把原因写进 <paramref name="error" />。
    /// </summary>
    public static UITextPack? FromJSON(string json, out string? error)
    {
        error = null;
        try
        {
            var pack = JsonSerializer.Deserialize<UITextPack>(json, JSONOptions);
            pack?.MigrateIfNeeded();
            pack?.InvalidateIndex();
            return pack;
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>旧格式迁移（<c>Meta.Format</c> 缺失 = v1）。</summary>
    private void MigrateIfNeeded()
    {
        if (this.Meta.Format >= CurrentFormat)
        {
            return;
        }

        foreach (var entry in this.Entries)
        {
            entry.PreserveID = false;
        }
    }
}
