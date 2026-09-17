using System.Collections;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;

namespace FireGaze.RepoAudit;

/// <summary>本机已装插件的一条记录（从卫月读来，不联网）。</summary>
internal sealed class InstalledPluginEntry
{
    public required string InternalName { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>安装时的仓库地址原文（可能是镜像地址，甚至为空 = 手动 / 开发版装的）。</summary>
    public string? RepositoryUrl { get; init; }

    /// <summary>卫月的 LocalPlugin 实例（给图标缓存用）。</summary>
    public required object RawPlugin { get; init; }

    public object? Manifest { get; init; }

    public bool IsThirdParty { get; init; }

    public bool IsDev { get; init; }
}

/// <summary>
/// 「本机装了哪些插件、各来自哪条库链」的索引。
/// </summary>
/// <remarks>
/// 全程反射读卫月内部、不联网。**读不到时 <see cref="Available"/> = false**：界面必须显示 `—`，
/// 绝不能把"读不到"渲染成"一个都没装"——那是假 0，会把人骗去删掉还在用的库链。
/// </remarks>
internal sealed class InstalledPluginsIndex
{
    /// <summary>本插件自己用的镜像前缀（RepoScanner.BuildChannels 同源），匹配前要还原成原始地址。</summary>
    private static readonly string[] MirrorPrefixes =
    [
        "https://gh.atmoomen.top/",
        "https://gh-proxy.org/",
    ];

    private Dictionary<string, List<InstalledPluginEntry>> byRepository = new(StringComparer.Ordinal);
    private List<InstalledPluginEntry> all = [];

    private InstalledPluginsIndex()
    {
    }

    /// <summary>数据是否可信（读得到卫月的已装插件列表）。</summary>
    public bool Available { get; private init; }

    /// <summary>统计时间（本地）。</summary>
    public DateTime CapturedLocal { get; private init; }

    /// <summary>不可信的原因（给界面/日志用）。</summary>
    public string? FailureReason { get; private init; }

    public IReadOnlyList<InstalledPluginEntry> All => this.all;

    /// <summary>有已装插件的库链数量。</summary>
    public int RepositoriesInUse => this.byRepository.Count;

    /// <summary>读一次卫月的已装插件列表并建索引。</summary>
    public static InstalledPluginsIndex Build() => BuildCore();

    private static InstalledPluginsIndex BuildCore()
    {
        try
        {
            var dalamud = typeof(IDalamudPluginInterface).Assembly;
            var serviceOpen = dalamud.GetType("Dalamud.Service`1", throwOnError: false);
            var managerType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: false);
            if (serviceOpen is null || managerType is null)
            {
                return Unavailable("拿不到卫月的插件管理器类型");
            }

            var get = serviceOpen.MakeGenericType(managerType)
                .GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
            var manager = get?.Invoke(null, null);
            if (manager is null)
            {
                return Unavailable("拿不到卫月的插件管理器实例");
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (managerType.GetProperty("InstalledPlugins", flags)?.GetValue(manager) is not IEnumerable plugins)
            {
                return Unavailable("读不到 InstalledPlugins");
            }

            var byRepository = new Dictionary<string, List<InstalledPluginEntry>>(StringComparer.Ordinal);
            var all = new List<InstalledPluginEntry>();

            foreach (var plugin in plugins)
            {
                if (plugin is null)
                {
                    continue;
                }

                var type = plugin.GetType();
                var manifest = type.GetProperty("Manifest", flags)?.GetValue(plugin);

                var internalName = type.GetProperty("InternalName", flags)?.GetValue(plugin) as string
                                   ?? manifest?.GetType().GetProperty("InternalName", flags)?.GetValue(manifest) as string
                                   ?? string.Empty;
                var displayName = type.GetProperty("Name", flags)?.GetValue(plugin) as string
                                  ?? manifest?.GetType().GetProperty("Name", flags)?.GetValue(manifest) as string
                                  ?? internalName;

                var repositoryUrl = type.GetProperty("InstalledFromUrl", flags)?.GetValue(plugin) as string
                                    ?? manifest?.GetType().GetProperty("InstalledFromUrl", flags)?.GetValue(manifest) as string;

                var isDev = type.GetProperty("IsDev", flags)?.GetValue(plugin) as bool? ?? false;
                var isThirdParty = type.GetProperty("IsThirdParty", flags)?.GetValue(plugin) as bool?
                                   ?? manifest?.GetType().GetProperty("IsThirdParty", flags)?.GetValue(manifest) as bool?
                                   ?? false;

                var entry = new InstalledPluginEntry
                {
                    InternalName = internalName,
                    DisplayName = displayName,
                    RepositoryUrl = repositoryUrl,
                    RawPlugin = plugin,
                    Manifest = manifest,
                    IsThirdParty = isThirdParty,
                    IsDev = isDev,
                };

                all.Add(entry);

                // 手动装 / 开发版没有来源地址，不参与"来自哪条库链"的统计
                if (string.IsNullOrWhiteSpace(repositoryUrl))
                {
                    continue;
                }

                var key = NormalizeRepositoryUrl(repositoryUrl);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!byRepository.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    byRepository[key] = bucket;
                }

                bucket.Add(entry);
            }

            foreach (var bucket in byRepository.Values)
            {
                bucket.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            }

            all.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            // 第二道防「假 0」：装了插件却一个来源地址都读不到 ⇒ 多半是字段改名了，宁可报「不可用」
            if (all.Count > 0 && byRepository.Count == 0)
            {
                return Unavailable("已装插件都读不到来源地址（InstalledFromUrl 可能改名了）");
            }

            Plugin.Log.Debug($"[FireGaze] 已装插件索引：{all.Count} 个插件，{byRepository.Count} 条库链在用");

            return new InstalledPluginsIndex
            {
                Available = true,
                CapturedLocal = DateTime.Now,
                byRepository = byRepository,
                all = all,
            };
        }
        catch (Exception e)
        {
            PluginLogFallback.Write("[FireGaze] 读取已装插件失败：" + e.Message);
            return Unavailable(e.Message);
        }
    }

    private static InstalledPluginsIndex Unavailable(string reason) => new()
    {
        Available = false,
        CapturedLocal = DateTime.Now,
        FailureReason = reason,
    };

    /// <summary>取某条库链在本机装的插件；不可用时返回 false（调用方应显示 `—`）。</summary>
    public bool TryGetInstalled(string repositoryUrl, out List<InstalledPluginEntry> plugins)
    {
        plugins = [];

        if (!this.Available)
        {
            return false;
        }

        var key = NormalizeRepositoryUrl(repositoryUrl);
        if (key.Length == 0)
        {
            return false;
        }

        if (this.byRepository.TryGetValue(key, out var found))
        {
            plugins = found;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 归一化仓库地址以便匹配：trim → 去掉已知镜像前缀（还原成原始地址）→ scheme/host 小写 → 去尾斜杠。
    /// （路径部分保持原样：GitHub raw 的 owner/repo 是区分大小写的。）
    /// </summary>
    public static string NormalizeRepositoryUrl(string? url)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        foreach (var prefix in MirrorPrefixes)
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = text[prefix.Length..];
            text = rest.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? rest : "https://" + rest;
            break;
        }

        while (text.EndsWith('/'))
        {
            text = text[..^1];
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimEnd('/');
            var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port;
            return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}{path}";
        }

        return text.ToLowerInvariant();
    }
}

/// <summary>
/// 插件图标：借用卫月自己的图标缓存（<c>PluginImageCache.TryGetIcon</c>），
/// 由卫月负责下载与缓存，本插件不联网。拿不到就返回 false，由界面画占位格。
/// </summary>
internal static class PluginIconLookup
{
    private static object? imageCache;
    private static MethodInfo? tryGetIcon;
    private static bool resolved;
    private static bool failed;

    /// <summary>尝试取图标；未缓存时返回 false（卫月会在后台开始下载，稍后重试即可）。</summary>
    public static bool TryGetHandle(InstalledPluginEntry entry, out ImTextureID handle)
    {
        handle = ImTextureID.Null;

        var method = ResolveMethod();
        if (method is null || imageCache is null)
        {
            return false;
        }

        try
        {
            object?[] args = [entry.RawPlugin, entry.Manifest, entry.IsThirdParty, null, null];
            if (method.Invoke(imageCache, args) is not true)
            {
                return false;
            }

            if (args[3] is IDalamudTextureWrap wrap)
            {
                handle = wrap.Handle;
                return !handle.IsNull;
            }
        }
        catch
        {
            // 图标取不到不影响主流程
        }

        return false;
    }

    private static MethodInfo? ResolveMethod()
    {
        if (resolved)
        {
            return failed ? null : tryGetIcon;
        }

        resolved = true;

        try
        {
            var dalamud = typeof(IDalamudPluginInterface).Assembly;
            var serviceOpen = dalamud.GetType("Dalamud.Service`1", throwOnError: false);
            var cacheType = dalamud.GetType("Dalamud.Interface.Internal.Windows.PluginImageCache", throwOnError: false);
            if (serviceOpen is null || cacheType is null)
            {
                failed = true;
                return null;
            }

            var get = serviceOpen.MakeGenericType(cacheType)
                .GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
            imageCache = get?.Invoke(null, null);

            // TryGetIcon 只有一个重载：参数里含内部类型 LocalPlugin，没法按类型精确匹配，按名字取即可
            tryGetIcon = cacheType.GetMethod("TryGetIcon", BindingFlags.Instance | BindingFlags.Public);

            failed = imageCache is null || tryGetIcon is null;
            return failed ? null : tryGetIcon;
        }
        catch
        {
            failed = true;
            return null;
        }
    }
}
