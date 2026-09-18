using System.Collections;
using System.Reflection;
using System.Text;
using FireGaze.RepoAudit;

namespace FireGaze.Translate;

/// <summary>翻译搜索索引里的一条：一个插件在词表里的现状。</summary>
internal sealed class TranslationIndexEntry
{
    public required string InternalName { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>插件所在的库链地址（官方主库为空）。</summary>
    public string? RepositoryUrl { get; init; }

    public bool RepositoryEnabled { get; init; }

    public string OriginalName { get; init; } = string.Empty;

    public string OriginalPunchline { get; init; } = string.Empty;

    public string OriginalDescription { get; init; } = string.Empty;

    /// <summary>词表里对这个插件的现有记录（没有 = null）。</summary>
    public TransEntry? Entry { get; init; }

    /// <summary>有多少个字段能翻。注意：不包括上游本来就是空、玩家也没贡献过的字段。</summary>
    public int TotalFields { get; init; }

    /// <summary>上游本来就是空、但玩家贡献过的字段数（算进完成度，不算缺译）。</summary>
    public int TemplateFields { get; init; }

    /// <summary>有多少个字段已经有译文。</summary>
    public int TranslatedFields { get; init; }

    /// <summary>有没有玩家提交的字段。</summary>
    public bool HasUserTranslation { get; init; }

    /// <summary>有没有「原文改过、还没重译」的字段。</summary>
    public bool HasReview { get; init; }

    /// <summary>是不是官方主库（Dip17）里的插件。</summary>
    public bool IsOfficial { get; init; }

    /// <summary>上游没提供、但玩家可以补上译文的字段（Name / Punchline / Description）。</summary>
    public List<string> ContributableFields { get; } = [];

    /// <summary>缺译文的字段名（Name / Punchline / Description）；上游没提供的字段不算缺译。</summary>
    public List<string> MissingFields { get; } = [];

    /// <summary>整条插件的状态（用户筛选「缺译文 / 机器译 / 玩家译」用）。</summary>
    public string State { get; private set; } = "missing";

    /// <summary>可以显示图标（作者声明了图标地址 / 官方库通道）。</summary>
    public bool DeclaresIcon { get; init; }

    /// <summary>IconUrl（给卫月的图标服务用）。</summary>
    public string? IconUrl { get; init; }

    /// <summary>是否第三方库插件。</summary>
    public bool IsThirdParty { get; init; } = true;

    /// <summary>给图标缓存用的包装（本地缓存命中时才有）。</summary>
    public object? RawPlugin { get; init; }

    public object? Manifest { get; init; }

    /// <summary>搜索用的归一化文本（名称 + 原文 + 译文）。</summary>
    internal string SearchBlob { get; set; } = string.Empty;

    /// <summary>完成度：已有译文的字段 / 该有译文的字段（含玩家补的空字段）。</summary>
    public float Completion
    {
        get
        {
            var total = this.TotalFields + this.TemplateFields;
            return total == 0 ? 0f : (float)this.TranslatedFields / total;
        }
    }

    public void FinalizeState()
    {
        this.State = this.MissingFields.Count > 0
            ? "missing"
            : this.HasUserTranslation ? "user" : "machine";
    }
}

/// <summary>
/// 「参与翻译」的搜索索引：遍历卫月当前认得的所有插件库（含已停用的），
/// 取每个插件的原文，再与词表对照 —— 全程读内存，不联网。
/// </summary>
internal sealed class TranslationIndex
{
    private readonly List<TranslationIndexEntry> all = [];

    private TranslationIndex()
    {
    }

    /// <summary>数据是否可信（读得到卫月的插件库列表）。</summary>
    public bool Available { get; private init; }

    public string? FailureReason { get; private init; }

    public DateTime CapturedLocal { get; private init; }

    public IReadOnlyList<TranslationIndexEntry> All => this.all;

    /// <summary>有多少条能翻的插件（原文里有至少一个字段非空）。</summary>
    public int TranslatableCount => this.all.Count;

    public int MissingCount => this.all.Count(x => x.State == "missing");

    public int UserCount => this.all.Count(x => x.HasUserTranslation);

    public int ReviewCount => this.all.Count(x => x.HasReview);

    /// <summary>建一次索引（读卫月内存，不联网；可以放后台线程）。</summary>
    public static TranslationIndex Build(Dictionary<string, TransEntry> table)
    {
        try
        {
            var manager = ResolvePluginManager();
            if (manager is null)
            {
                return Unavailable("拿不到卫月的插件管理器");
            }

            var managerType = manager.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            if (managerType.GetProperty("Repos", flags)?.GetValue(manager) is not IEnumerable repos)
            {
                return Unavailable("读不到卫月的插件库列表");
            }

            var map = new Dictionary<string, TranslationIndexEntry>(StringComparer.Ordinal);

            foreach (var repo in repos)
            {
                if (repo is null)
                {
                    continue;
                }

                var repoType = repo.GetType();
                var repoUrl = repoType.GetProperty("PluginMasterUrl", flags)?.GetValue(repo) as string;
                var repoEnabled = repoType.GetProperty("IsEnabled", flags)?.GetValue(repo) as bool? ?? false;
                var isThirdParty = repoType.GetProperty("IsThirdParty", flags)?.GetValue(repo) as bool? ?? false;

                if (repoType.GetProperty("PluginMaster", flags)?.GetValue(repo) is not IEnumerable manifests)
                {
                    continue;
                }

                foreach (var manifest in manifests)
                {
                    if (manifest is null)
                    {
                        continue;
                    }

                    var entry = Create(manifest, repoUrl, repoEnabled, isThirdParty, table);
                    if (entry is null)
                    {
                        continue;
                    }

                    // 同一个内部名可能同时出现在主库与第三方库：留信息更全的那条
                    if (map.TryGetValue(entry.InternalName, out var existing) &&
                        !(existing.RepositoryUrl is null && entry.RepositoryUrl is not null))
                    {
                        continue;
                    }

                    map[entry.InternalName] = entry;
                }
            }

            var index = new TranslationIndex
            {
                Available = true,
                CapturedLocal = DateTime.Now,
            };

            index.all.AddRange(map.Values.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase));
            foreach (var entry in index.all)
            {
                entry.SearchBlob = BuildBlob(entry);
            }

            Plugin.Log.Debug(
                $"[FireGaze] 参与翻译索引：{index.all.Count} 个插件（缺译 {index.MissingCount} · 玩家译 {index.UserCount}）");
            return index;
        }
        catch (Exception e)
        {
            PluginLogFallback.Write("[FireGaze] 建立参与翻译索引失败：" + e.Message);
            return Unavailable(e.Message);
        }
    }

    private static TranslationIndexEntry? Create(
        object manifest,
        string? repoUrl,
        bool repoEnabled,
        bool isThirdParty,
        Dictionary<string, TransEntry> table)
    {
        var type = manifest.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var internalName = type.GetProperty("InternalName", flags)?.GetValue(manifest) as string;
        if (string.IsNullOrWhiteSpace(internalName))
        {
            return null;
        }

        var name = type.GetProperty("Name", flags)?.GetValue(manifest) as string ?? string.Empty;
        var punchline = type.GetProperty("Punchline", flags)?.GetValue(manifest) as string ?? string.Empty;
        var description = TextOf(type.GetProperty("Description", flags)?.GetValue(manifest));
        var iconUrl = type.GetProperty("IconUrl", flags)?.GetValue(manifest) as string;
        var dip17 = type.GetProperty("Dip17Channel", flags)?.GetValue(manifest) as string;

        table.TryGetValue(internalName, out var entry);

        // 三个字段的现状
        var fields = new (string Field, string Original, TransPair? Pair)[]
        {
            ("Name", name, entry?.Name),
            ("Punchline", punchline, entry?.Punchline),
            ("Description", description, entry?.Description),
        };

        var total = 0;
        var template = 0;
        var translated = 0;
        var user = false;
        var review = false;
        var missing = new List<string>();
        var contributable = new List<string>();

        foreach (var (field, original, pair) in fields)
        {
            var hasOriginal = !string.IsNullOrWhiteSpace(original);
            var hasTranslation = pair is { HasTranslation: true };

            if (!hasOriginal && !hasTranslation)
            {
                // 上游压根没提供这个字段：不算缺译，但允许玩家贡献一份
                contributable.Add(field);
                continue;
            }

            if (!hasOriginal)
            {
                // 上游没提供、玩家自己写了：算进完成度，不算缺译
                template++;
                translated++;
            }
            else if (hasTranslation)
            {
                total++;
                translated++;
            }
            else
            {
                total++;
                missing.Add(field);
            }

            user |= pair is { IsUserSource: true };
            review |= pair?.Review is not null;
        }

        // 三个字段都没有原文、词表里也没有：没什么可翻的，直接不进列表
        if (total + template == 0 && !user)
        {
            return null;
        }

        var result = new TranslationIndexEntry
        {
            InternalName = internalName,
            DisplayName = string.IsNullOrWhiteSpace(name) ? internalName : name,
            RepositoryUrl = string.IsNullOrWhiteSpace(repoUrl) ? null : repoUrl,
            RepositoryEnabled = repoEnabled,
            OriginalName = name,
            OriginalPunchline = punchline,
            OriginalDescription = description,
            Entry = entry,
            DeclaresIcon = !string.IsNullOrWhiteSpace(iconUrl) || !string.IsNullOrWhiteSpace(dip17),
            IconUrl = iconUrl,
            IsThirdParty = isThirdParty,
            Manifest = manifest,
            TotalFields = total,
            TemplateFields = template,
            TranslatedFields = translated,
            HasUserTranslation = user,
            HasReview = review,
            IsOfficial = !isThirdParty,
        };

        result.MissingFields.AddRange(missing);
        result.ContributableFields.AddRange(contributable);
        result.FinalizeState();
        return result;
    }

    private static string TextOf(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        IEnumerable<string> lines => string.Join("\n", lines),
        IEnumerable list => string.Join(
            "\n",
            list.Cast<object?>().Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0)),
        _ => value.ToString() ?? string.Empty,
    };

    private static string BuildBlob(TranslationIndexEntry entry)
    {
        var builder = new StringBuilder(256);
        builder.Append(entry.InternalName).Append('\n');
        builder.Append(entry.DisplayName).Append('\n');
        builder.Append(entry.OriginalName).Append('\n');
        builder.Append(entry.OriginalPunchline).Append('\n');
        builder.Append(entry.OriginalDescription).Append('\n');
        builder.Append(entry.Entry?.Name?.Translated).Append('\n');
        builder.Append(entry.Entry?.Punchline?.Translated).Append('\n');
        builder.Append(entry.Entry?.Description?.Translated).Append('\n');
        return builder.ToString().ToLowerInvariant();
    }

    private static object? ResolvePluginManager()
    {
        try
        {
            var dalamud = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;
            var serviceOpen = dalamud.GetType("Dalamud.Service`1", throwOnError: false);
            var managerType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: false);
            if (serviceOpen is null || managerType is null)
            {
                return null;
            }

            var get = serviceOpen.MakeGenericType(managerType)
                .GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
            return get?.Invoke(null, null);
        }
        catch
        {
            return null;
        }
    }

    private static TranslationIndex Unavailable(string reason) => new()
    {
        Available = false,
        FailureReason = reason,
        CapturedLocal = DateTime.Now,
    };
}
