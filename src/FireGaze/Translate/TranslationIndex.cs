using System.Collections;
using System.Reflection;
using System.Text;
using FireGaze.RepoAudit;

namespace FireGaze.Translate;

/// <summary>
///     翻译搜索索引里的一条：一个插件在词表里的现状。
/// </summary>
internal sealed class TranslationIndexEntry
{
    public required string InternalName { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    ///     插件所在的库链地址（官方主库为空）。
    /// </summary>
    public string? RepositoryURL { get; init; }

    /// <summary>
    ///     这条库链在不在本机的插件库里（false = 还没加过）。本机配置读不到时也按 false 处理。
    /// </summary>
    public bool RepositoryKnown { get; init; }

    public bool RepositoryEnabled { get; init; }

    public string OriginalName { get; init; } = string.Empty;

    public string OriginalPunchline { get; init; } = string.Empty;

    public string OriginalDescription { get; init; } = string.Empty;

    /// <summary>
    ///     作者名（云端词表带的；本机清单更新它）。null / 空 = 不知道，排序时排后面。
    /// </summary>
    public string? Author { get; set; }

    /// <summary>
    ///     上游最后更新时间（unix 秒）；null = 不知道，排序时排后面。
    /// </summary>
    public long? Updated { get; set; }

    /// <summary>
    ///     词表里对这个插件的现有记录（没有 = null）。
    /// </summary>
    public TransEntry? Entry { get; private set; }

    /// <summary>
    ///     有多少个字段能翻。注意：不包括上游本来就是空、玩家也没贡献过的字段。
    /// </summary>
    public int TotalFields { get; private set; }

    /// <summary>
    ///     上游本来就是空、但玩家贡献过的字段数（算进完成度，不算缺译）。
    /// </summary>
    public int TemplateFields { get; private set; }

    /// <summary>
    ///     有多少个字段已经有译文。
    /// </summary>
    public int TranslatedFields { get; private set; }

    /// <summary>
    ///     有没有玩家提交的字段。
    /// </summary>
    public bool HasUserTranslation { get; private set; }

    /// <summary>
    ///     有没有「原文改过、还没重译」的字段。
    /// </summary>
    public bool HasReview { get; private set; }

    /// <summary>
    ///     是不是官方主库（Dip17）里的插件。
    /// </summary>
    public bool IsOfficial { get; init; }

    /// <summary>
    ///     上游没提供、但玩家可以补上译文的字段（Name / Punchline / Description）。
    /// </summary>
    public List<string> ContributableFields { get; } = [];

    /// <summary>
    ///     缺译文的字段名（Name / Punchline / Description）；上游没提供的字段不算缺译。
    /// </summary>
    public List<string> MissingFields { get; } = [];

    /// <summary>
    ///     整条插件的状态（用户筛选「缺译文 / 机器译 / 玩家译」用）。
    /// </summary>
    public string State { get; private set; } = "missing";

    /// <summary>
    ///     可以显示图标（作者声明了图标地址 / 官方库通道）。
    /// </summary>
    public bool DeclaresIcon { get; set; }

    /// <summary>
    ///     IconURL（给卫月的图标服务用）。
    /// </summary>
    public string? IconURL { get; set; }

    /// <summary>
    ///     是否第三方库插件。
    /// </summary>
    public bool IsThirdParty { get; init; } = true;

    /// <summary>
    ///     给图标缓存用的包装（本地缓存命中时才有）。
    /// </summary>
    public object? RawPlugin { get; init; }

    public object? Manifest { get; init; }

    /// <summary>
    ///     搜索用的归一化文本（名称 + 原文 + 译文）。
    /// </summary>
    internal string SearchBlob { get; set; } = string.Empty;

    /// <summary>
    ///     完成度：已有译文的字段 / 该有译文的字段（含玩家补的空字段）。
    /// </summary>
    public float Completion
    {
        get
        {
            var total = TotalFields + TemplateFields;
            return total == 0 ? 0f : (float)TranslatedFields / total;
        }
    }

    public void FinalizeState()
    {
        State = MissingFields.Count > 0
            ? "missing"
            : HasUserTranslation ? "user" : "machine";
    }

    /// <summary>
    ///     按当前词表重算这一行的状态与完成度（保存 / 删除 / 撤回后刷新用）。
    ///     不反射卫月、也不重建整个索引 —— 只动我们自己的缓存。
    /// </summary>
    public void RefreshFrom(TransEntry? entry)
    {
        Entry = entry;
        Evaluate();
    }

    /// <summary>
    ///     与建索引时同一套判定：哪个字段缺译 / 哪个字段上游没给 / 完成度多少。
    /// </summary>
    internal void Evaluate()
    {
        MissingFields.Clear();
        ContributableFields.Clear();

        var total = 0;
        var template = 0;
        var translated = 0;
        var user = false;
        var review = false;
        var missing = new List<string>();
        var contributable = new List<string>();

        var fields = new (string Field, string Original, TransPair? Pair)[]
        {
            ("Name", OriginalName, Entry?.Name),
            ("Punchline", OriginalPunchline, Entry?.Punchline),
            ("Description", OriginalDescription, Entry?.Description),
        };

        foreach (var (field, original, pair) in fields)
        {
            var hasOriginal = !string.IsNullOrWhiteSpace(original);
            var hasTranslation = pair is { HasTranslation: true };

            if (hasOriginal && !hasTranslation && TranslationIndex.IsChinese(original))
            {
                continue;   // 上游原文本身就是中文：不用翻，也不算缺译（日文不算，见 IsChinese）
            }

            if (!hasOriginal && !hasTranslation)
            {
                contributable.Add(field);
                continue;
            }

            if (!hasOriginal)
            {
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

        TotalFields = total;
        TemplateFields = template;
        TranslatedFields = translated;
        HasUserTranslation = user;
        HasReview = review;
        MissingFields.AddRange(missing);
        ContributableFields.AddRange(contributable);
        FinalizeState();
    }
}

/// <summary>
///     「参与翻译」的搜索索引：以云端词表（整座语料）为准，本机卫月的清单用来更新原文、补新插件、标库链状态
///     —— 全程读内存，不联网。
/// </summary>
internal sealed class TranslationIndex
{
    private readonly List<TranslationIndexEntry> all = [];

    private TranslationIndex()
    {
    }

    /// <summary>
    ///     数据是否可信（至少从词表或卫月拿到了一份插件清单）。
    /// </summary>
    public bool Available { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTime CapturedLocal { get; private set; }

    public IReadOnlyList<TranslationIndexEntry> All => all;

    /// <summary>
    ///     有多少条能翻的插件（原文里有至少一个字段非空）。
    /// </summary>
    public int TranslatableCount => all.Count;

    public int MissingCount => all.Count(x => x.State == "missing");

    public int UserCount => all.Count(x => x.HasUserTranslation);

    public int ReviewCount => all.Count(x => x.HasReview);

    /// <summary>
    ///     建一次索引（云端词表 + 卫月内存，不联网；可以放后台线程）。
    ///
    ///     列表以**云端词表**为准：词表里每个插件都进列表（本机没加的库也能看到、能补译文），
    ///     本机卫月的清单只用来取「更新的原文 + 图标」并补词表还没收录的新插件。
    ///     2026-10-06 用户指出：参与翻译要覆盖整个云端语料，不能只看本机已加载的仓库。
    /// </summary>
    public static TranslationIndex Build(
        Dictionary<string, TransEntry> table,
        IReadOnlyList<RepoEntry>? localRepositories)
    {
        try
        {
            var repoState = BuildRepoState(localRepositories);

            var index = FromTable(table, repoState);
            var cloudCount = index.all.Count;

            var localAvailable = AddLocalManifests(index, table, out var localOnly, out var skipped);

            if (!localAvailable && cloudCount == 0)
            {
                return Unavailable("拿不到卫月的插件管理器，云端词表里也没有可列出的插件");
            }

            index.Available = true;
            index.CapturedLocal = DateTime.Now;
            index.all.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            foreach (var entry in index.all)
            {
                entry.SearchBlob = BuildBlob(entry);
            }

            Plugin.Log.Debug(
                $"[FireGaze] 参与翻译索引：{index.all.Count} 个插件（云端 {cloudCount} · 本机补充 {localOnly}；"
                + $"缺译 {index.MissingCount} · 玩家译 {index.UserCount}；跳过测试版/无内容 {skipped} 个）");
            return index;
        }
        catch (Exception e)
        {
            PluginLogFallback.Write("[FireGaze] 建立参与翻译索引失败：" + e.Message);
            return Unavailable(e.Message);
        }
    }

    /// <summary>
    ///     把本机已配置的库链折成「归一化地址 → 是否启用」（参与翻译靠它标 启用/停用/未加入）。
    ///     传 null = 读不到本机配置（界面就只画地址、不提供启用/加库判断）。
    /// </summary>
    internal static Dictionary<string, bool>? BuildRepoState(IReadOnlyList<RepoEntry>? localRepositories)
    {
        if (localRepositories is null)
        {
            return null;
        }

        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var repo in localRepositories)
        {
            if (!string.IsNullOrWhiteSpace(repo.URL))
            {
                map[InstalledPluginsIndex.NormalizeRepositoryURL(repo.URL)] = repo.IsEnabled;
            }
        }

        return map;
    }

    /// <summary>
    ///     只用云端词表建索引（不碰卫月，fgtest 可以离线跑）。
    ///     <paramref name="localRepos" /> = 归一化后的库链 → 是否启用；null = 本机配置不可用。
    /// </summary>
    internal static TranslationIndex FromTable(
        Dictionary<string, TransEntry> table,
        IReadOnlyDictionary<string, bool>? localRepos)
    {
        var index = new TranslationIndex();
        foreach (var (internalName, transEntry) in table)
        {
            if (string.IsNullOrWhiteSpace(internalName) || transEntry is null)
            {
                continue;
            }

            // 测试版专用插件不进列表（用户 2026-09-22 定；云端词表用 Testing 标记）
            if (transEntry.Testing == true)
            {
                continue;
            }

            var entry = FromTableEntry(internalName, transEntry, localRepos);
            if (entry is not null)
            {
                index.all.Add(entry);
            }
        }

        // 搜索结果靠 SearchBlob（全字段拼好的小写串）；FromTable 自己就带上，
        // 这样离线（fgtest）与 Build 合并后的结果一致。
        foreach (var entry in index.all)
        {
            entry.SearchBlob = BuildBlob(entry);
        }

        return index;
    }

    private static TranslationIndexEntry? FromTableEntry(
        string internalName,
        TransEntry transEntry,
        IReadOnlyDictionary<string, bool>? localRepos)
    {
        var name = transEntry.Name?.Original ?? string.Empty;
        var repoURL = (transEntry.Repo ?? string.Empty).Trim();

        var known = false;
        var enabled = false;
        if (repoURL.Length > 0 && localRepos is not null)
        {
            known = localRepos.TryGetValue(InstalledPluginsIndex.NormalizeRepositoryURL(repoURL), out var value);
            enabled = known && value;
        }

        var result = new TranslationIndexEntry
        {
            InternalName = internalName,
            DisplayName = string.IsNullOrWhiteSpace(name) ? internalName : name,
            RepositoryURL = repoURL.Length == 0 ? null : repoURL,
            RepositoryKnown = known,
            RepositoryEnabled = enabled,
            OriginalName = name,
            OriginalPunchline = transEntry.Punchline?.Original ?? string.Empty,
            OriginalDescription = transEntry.Description?.Original ?? string.Empty,
            Author = transEntry.Author,
            Updated = transEntry.Updated,
            DeclaresIcon = !string.IsNullOrWhiteSpace(transEntry.Icon),
            IconURL = transEntry.Icon,
            IsThirdParty = true,
            IsOfficial = transEntry.Official == true,
        };

        // 三个字段的现状由 Evaluate() 统一判定（与保存后的就地刷新共用一套逻辑）
        result.RefreshFrom(transEntry);

        // 三个字段都没有原文、词表里也没有玩家译：没什么可翻的，直接不进列表
        if (result.TotalFields + result.TemplateFields == 0 && !result.HasUserTranslation)
        {
            return null;
        }

        return result;
    }

    /// <summary>
    ///     用本机卫月的清单补齐/更新索引：同一内部名用本机的原文（更新鲜）整条替换云端那条；
    ///     词表还没有的新插件也补进来。返回 false = 读不到卫月（不影响云端列表可用）。
    /// </summary>
    private static bool AddLocalManifests(
        TranslationIndex index,
        Dictionary<string, TransEntry> table,
        out int localOnly,
        out int skipped)
    {
        localOnly = 0;
        skipped = 0;

        var manager = ResolvePluginManager();
        if (manager is null)
        {
            return false;
        }

        var managerType = manager.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        if (managerType.GetProperty("Repos", flags)?.GetValue(manager) is not IEnumerable repos)
        {
            return false;
        }

        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < index.all.Count; i++)
        {
            byName[index.all[i].InternalName] = i;
        }

        foreach (var repo in repos)
        {
            if (repo is null)
            {
                continue;
            }

            var repoType = repo.GetType();
            var repoURL = repoType.GetProperty("PluginMasterUrl", flags)?.GetValue(repo) as string;
            var repoEnabled = repoType.GetProperty("IsEnabled", flags)?.GetValue(repo) as bool? ?? false;
            var isThirdParty = repoType.GetProperty("IsThirdParty", flags)?.GetValue(repo) as bool? ?? false;

            // 官方主库（Dip17）不进参与翻译列表：那不是本词表的范围，
            // 而且官库插件的简介由官方发布方维护，玩家改这里的意义不大。
            if (!isThirdParty)
            {
                continue;
            }

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

                var entry = Create(manifest, repoURL, repoEnabled, table);
                if (entry is null)
                {
                    skipped++;
                    continue;
                }

                if (byName.TryGetValue(entry.InternalName, out var at))
                {
                    // 本机原文更新鲜、还带着图标引用：整条替换云端那条；
                    // 云端才有的作者/更新时间/官方标记不能被换掉。
                    var cloud = index.all[at];
                    entry.Author ??= cloud.Author;
                    entry.Updated ??= cloud.Updated;
                    entry.IconURL ??= cloud.IconURL;
                    entry.DeclaresIcon |= cloud.DeclaresIcon;
                    index.all[at] = entry;
                }
                else
                {
                    byName[entry.InternalName] = index.all.Count;
                    index.all.Add(entry);
                    localOnly++;
                }
            }
        }

        return true;
    }

    private static TranslationIndexEntry? Create(
        object manifest,
        string? repoURL,
        bool repoEnabled,
        Dictionary<string, TransEntry> table)
    {
        var type = manifest.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var internalName = type.GetProperty("InternalName", flags)?.GetValue(manifest) as string;
        if (string.IsNullOrWhiteSpace(internalName))
        {
            return null;
        }

        // 测试版专用插件（卫月自家标记 IsTestingExclusive）不进这份列表：
        // 它们是测试分支、随时改，玩家基本用不到，也永远补不完 —— 用户 2026-09-22 定：不算缺译。
        if (type.GetProperty("IsTestingExclusive", flags)?.GetValue(manifest) as bool? == true)
        {
            return null;
        }

        var name = type.GetProperty("Name", flags)?.GetValue(manifest) as string ?? string.Empty;
        var punchline = type.GetProperty("Punchline", flags)?.GetValue(manifest) as string ?? string.Empty;
        var description = TextOf(type.GetProperty("Description", flags)?.GetValue(manifest));
        var iconURL = type.GetProperty("IconUrl", flags)?.GetValue(manifest) as string;
        var dip17 = type.GetProperty("Dip17Channel", flags)?.GetValue(manifest) as string;
        var author = type.GetProperty("Author", flags)?.GetValue(manifest) as string;
        var lastUpdateRaw = type.GetProperty("LastUpdate", flags)?.GetValue(manifest);
        var lastUpdate = lastUpdateRaw is long stamp && stamp > 0 ? stamp : (long?)null;

        table.TryGetValue(internalName, out var entry);

        var result = new TranslationIndexEntry
        {
            InternalName = internalName,
            DisplayName = string.IsNullOrWhiteSpace(name) ? internalName : name,
            RepositoryURL = string.IsNullOrWhiteSpace(repoURL) ? null : repoURL,
            RepositoryKnown = true,
            RepositoryEnabled = repoEnabled,
            OriginalName = name,
            OriginalPunchline = punchline,
            OriginalDescription = description,
            Author = string.IsNullOrWhiteSpace(author) ? null : author,
            Updated = lastUpdate,
            DeclaresIcon = !string.IsNullOrWhiteSpace(iconURL) || !string.IsNullOrWhiteSpace(dip17),
            IconURL = iconURL,
            IsThirdParty = true,
            Manifest = manifest,
            IsOfficial = false,
        };

        // 三个字段的现状由 Evaluate() 统一判定（与保存后的就地刷新共用一套逻辑）
        result.RefreshFrom(entry);

        // 三个字段都没有原文、词表里也没有：没什么可翻的，直接不进列表
        if (result.TotalFields + result.TemplateFields == 0 && !result.HasUserTranslation)
        {
            return null;
        }

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

    /// <summary>
    ///     这段文字里有没有中日韩汉字（用来判断「上游原文本身就是中文」）。
    /// </summary>
    internal static bool HasCjk(string text)
    {
        foreach (var ch in text)
        {
            if ((ch >= 0x3400 && ch <= 0x4DBF) ||    // 扩展 A
                (ch >= 0x4E00 && ch <= 0x9FFF) ||    // 基本区
                (ch >= 0xF900 && ch <= 0xFAFF) ||    // 兼容汉字
                (ch >= 0x3000 && ch <= 0x303F) ||    // 中文标点
                (ch >= 0xFF00 && ch <= 0xFFEF))      // 全角字符
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     有没有日语假名（平/片假名、半角片假名、片假名扩展）。
    /// </summary>
    internal static bool HasKana(string text)
    {
        foreach (var ch in text)
        {
            if ((ch >= 0x3040 && ch <= 0x30FF) ||    // 平假名 + 片假名
                (ch >= 0x31F0 && ch <= 0x31FF) ||    // 片假名扩展
                (ch >= 0xFF66 && ch <= 0xFF9D))      // 半角片假名
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     上游原文是不是「本来就是中文」：有汉字、而且没有假名。
    ///     日文夹着汉字，光看汉字会把它当成中文 —— 但玩家要的是中文译文（2026-09-22 用户定：日语也要翻）。
    /// </summary>
    internal static bool IsChinese(string text) => HasCjk(text) && !HasKana(text);

    private static string BuildBlob(TranslationIndexEntry entry)
    {
        var builder = new StringBuilder(256);
        builder.Append(entry.InternalName).Append('\n');
        builder.Append(entry.DisplayName).Append('\n');
        builder.Append(entry.OriginalName).Append('\n');
        builder.Append(entry.Author).Append('\n');
        builder.Append(entry.OriginalPunchline).Append('\n');
        builder.Append(entry.OriginalDescription).Append('\n');
        builder.Append(entry.Entry?.Name?.Translated).Append('\n');
        builder.Append(entry.Entry?.Punchline?.Translated).Append('\n');
        builder.Append(entry.Entry?.Description?.Translated).Append('\n');
        return builder.ToString().ToLowerInvariant();
    }

    private static object? reposReadyManager;
    private static PropertyInfo? reposReadyProperty;

    /// <summary>
    ///     卫月的插件库是不是都读完了（<c>PluginManager.ReposReady</c>：仓库刷新任务已完成）。
    ///     刷新期间每个仓库的清单会被先清空再逐个填回 —— 此刻抓的索引会大量漏插件
    ///     （2026-10-06 实例：一次刷新刚开始 0.7 秒时抓快照，1743 条词表只对上 98 个插件）。
    ///     拿不到这个信息时返回 null，调用方按「已完成」处理（保持旧行为）。
    /// </summary>
    internal static bool? IsReposReady()
    {
        try
        {
            if (reposReadyProperty is null)
            {
                var manager = ResolvePluginManager();
                if (manager is null)
                {
                    return null;
                }

                reposReadyProperty = manager.GetType().GetProperty(
                    "ReposReady",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                reposReadyManager = manager;
            }

            return reposReadyProperty?.GetValue(reposReadyManager) as bool?;
        }
        catch
        {
            return null;
        }
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
