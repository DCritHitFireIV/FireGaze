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
    [JsonPropertyName("_meta")]
    public UITextPackMeta Meta { get; set; } = new();

    [JsonPropertyName("entries")]
    public List<UITextPackEntry> Entries { get; set; } = [];

    /// <summary>
    ///     用户明确标记「不翻」的原文（留在包里，编辑器要记住，也不参与覆盖率统计的分母口径之外）。
    /// </summary>
    [JsonPropertyName("skipped")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<string> Skipped { get; set; } = [];

    private Dictionary<string, UITextPackEntry>? index;

    [JsonIgnore]
    public int TranslatedCount => this.Entries.Count(e => e.HasTranslation);

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
    public static bool IsAutoSkipped(UITextPackEntry entry) =>
        entry.Review is not null
        && (entry.Review.StartsWith(AutoSkipNotePrefix, StringComparison.Ordinal)
            || entry.Review.StartsWith("新一轮抽取已排除", StringComparison.Ordinal));

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
    /// </summary>
    public void MergeLibrary(UITextPack library)
    {
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
                continue;
            }

            if (existing.IsUserSource)
            {
                continue;
            }

            if (incoming.HasTranslation)
            {
                existing.Translated = incoming.Translated;
                existing.Source = incoming.Source ?? "library";
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

        var pruned = 0;
        var restored = 0;
        var dropped = new List<string>();
        foreach (var entry in this.Entries)
        {
            if (keep.Contains(entry.Original))
            {
                // 又变回候选了：只把「上次是系统自动排除」的恢复；用户手动标的「不翻」保持不动
                if (this.IsSkipped(entry.Original) && IsAutoSkipped(entry))
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

        return new UITextPackPruneOutcome(pruned, restored, dropped.Count);
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
    }

    /// <summary>
    ///     清掉索引（从 JSON 反序列化后要重建）。
    /// </summary>
    public void InvalidateIndex() => this.index = null;

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

    private static readonly JsonSerializerOptions JSONOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    ///     序列化成 UTF-8 JSON 文本（带末尾换行，与仓库里其它 JSON 一致）。
    /// </summary>
    public string ToJSON() => JsonSerializer.Serialize(this, JSONOptions) + Environment.NewLine;

    /// <summary>
    ///     从 JSON 读一个包；读不出来时返回 null 并把原因写进 <paramref name="error" />。
    /// </summary>
    public static UITextPack? FromJSON(string json, out string? error)
    {
        error = null;
        try
        {
            var pack = JsonSerializer.Deserialize<UITextPack>(json, JSONOptions);
            pack?.InvalidateIndex();
            return pack;
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }
}
