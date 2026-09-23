using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FireGaze.Translate;

/// <summary>
///     把词表注入卫月内存中的插件清单：
///       · AvailablePlugins（安装器「可用插件」列表）与 InstalledPlugins（已安装列表）的 Manifest；
///       · 三个字段各自按 <see cref="DisplayMode"/> 显示：名字 / 一行简介 / 插件详情；
///       · 只在「当前内容命中我们认识的变体（原文 / 纯译文 / 双语）」时才替换 ——
///         不会覆盖 FastDalamudCN 等其他插件的产物，也不会反复书写。
/// </summary>
public sealed class ManifestPatcher
{
    private readonly Func<Configuration> config;
    private readonly TranslationTable table;
    private readonly Action<string> log;

    private readonly Dictionary<Type, (FieldInfo? Name, FieldInfo? Punchline, FieldInfo? Description)> fieldCache = new();

    /// <summary>本会话里我们自己写过的值（按清单对象记）：词表更新后能把自己上一版渲染替换掉。</summary>
    private readonly ConditionalWeakTable<object, WrittenValues> written = new();

    private object? pluginManager;
    private PropertyInfo? propAvailable;
    private PropertyInfo? propInstalled;

    public ManifestPatcher(Func<Configuration> config, TranslationTable table, Action<string> log)
    {
        this.config = config;
        this.table = table;
        this.log = log;
    }

    /// <summary>
    ///     应用一次；返回被改写的清单数。
    /// </summary>
    public int ApplyAll() => Run(restore: false);

    /// <summary>
    ///     把已改写的文本还原成原文（关闭汉化时用）；返回被还原的清单数。
    /// </summary>
    public int RestoreAll() => Run(restore: true);

    private int Run(bool restore)
    {
        if (table.Count == 0)
        {
            return 0;
        }

        if (!restore && !config().TranslateEnabled)
        {
            return 0;
        }

        try
        {
            if (pluginManager is null && !ResolvePluginManager())
            {
                return 0;
            }

            var cfg = config();
            var patched = 0;

            if (propAvailable?.GetValue(pluginManager) is IEnumerable available)
            {
                foreach (var manifest in available)
                {
                    if (manifest is not null && Patch(manifest, cfg, restore))
                    {
                        patched++;
                    }
                }
            }

            if (propInstalled?.GetValue(pluginManager) is IEnumerable installed)
            {
                foreach (var local in installed)
                {
                    var manifest = local?.GetType()
                                        .GetProperty("Manifest", BindingFlags.Public | BindingFlags.Instance)
                                        ?.GetValue(local);
                    if (manifest is not null && Patch(manifest, cfg, restore))
                    {
                        patched++;
                    }
                }
            }

            return patched;
        }
        catch (Exception e)
        {
            log((restore ? "还原汉化失败：" : "应用汉化失败：") + e.Message);
            return 0;
        }
    }

    private bool Patch(object manifest, Configuration cfg, bool restore)
    {
        var type = manifest.GetType();
        var internalName = type.GetProperty("InternalName", BindingFlags.Public | BindingFlags.Instance)
                               ?.GetValue(manifest) as string;

        if (string.IsNullOrEmpty(internalName) || !table.TryGet(internalName, out var entry))
        {
            return false;
        }

        if (!fieldCache.TryGetValue(type, out var fields))
        {
            fields = (FindField(type, "<Name>k__BackingField"),
                      FindField(type, "<Punchline>k__BackingField"),
                      FindField(type, "<Description>k__BackingField"));
            fieldCache[type] = fields;
        }

        var remembered = written.GetOrCreateValue(manifest);

        var changed = ApplyField(fields.Name, manifest, entry.Name, cfg.NameMode, isName: true, restore, ref remembered.Name);
        changed |= ApplyField(fields.Punchline, manifest, entry.Punchline, cfg.PunchlineMode, isName: false, restore, ref remembered.Punchline);
        changed |= ApplyField(fields.Description, manifest, entry.Description, cfg.DescriptionMode, isName: false, restore, ref remembered.Description);
        return changed;
    }

    private sealed class WrittenValues
    {
        public string? Name;

        public string? Punchline;

        public string? Description;
    }

    private static bool ApplyField
    (
        FieldInfo? field,
        object manifest,
        TransPair? pair,
        DisplayMode mode,
        bool isName,
        bool restore,
        ref string? lastWritten
    )
    {
        if (field is null || pair is null)
        {
            return false;
        }

        var original = pair.Original;
        var translated = pair.Translated;
        if (string.IsNullOrWhiteSpace(original))
        {
            // 上游本来没给这个字段、译文是玩家自己补的（2026-09-22 审查 P1-5）：
            // 以前这里直接 return false，于是「自己补的」永远进不了游戏，
            // 而留档与界面都写着「已收录」——玩家验收不了。
            if (string.IsNullOrWhiteSpace(translated))
            {
                return false;
            }

            var currentEmpty = field.GetValue(manifest) as string;
            if (restore || mode == DisplayMode.Original)
            {
                if (string.Equals(currentEmpty, translated, StringComparison.Ordinal) ||
                    (!string.IsNullOrEmpty(lastWritten) && string.Equals(currentEmpty, lastWritten, StringComparison.Ordinal)))
                {
                    field.SetValue(manifest, string.Empty);
                    lastWritten = string.Empty;
                    return true;
                }

                return false;
            }

            if (!string.IsNullOrWhiteSpace(currentEmpty))
            {
                return false;   // 上游后来自己给了内容，不抢
            }

            field.SetValue(manifest, translated);
            lastWritten = translated;
            return true;
        }

        var hasTranslation = !string.IsNullOrWhiteSpace(translated);

        string target;
        if (restore || mode == DisplayMode.Original || !hasTranslation)
        {
            target = original;
        }
        else if (mode == DisplayMode.Chinese)
        {
            target = translated;
        }
        else
        {
            target = isName ? $"{translated} ({original})" : translated + "\n\n" + original;
        }

        var current = field.GetValue(manifest) as string;
        if (string.IsNullOrEmpty(current) || string.Equals(current, target, StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsKnownCurrent(current, original, translated, isName, lastWritten))
        {
            return false;
        }

        field.SetValue(manifest, target);
        lastWritten = target;
        return true;
    }

    /// <summary>
    ///     当前字段值是不是「我们认识的变体」——只有认识才敢替换（不覆盖别的插件的产物）。
    ///     认识的：原文 / 纯译文 / 双语渲染 / 本会话里我们自己写过的值 /
    ///     旧译文留下的双语渲染（尾部还是原文，头部是上一版译文）。
    ///     2026-09-23 bozjalone 实例：旧假译文时代写成的「日文 + 空行 + 日文」，词表修好后必须能被新译文替换。
    /// </summary>
    internal static bool IsKnownCurrent(string current, string original, string translated, bool isName, string? lastWritten)
    {
        if (string.Equals(current, original, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(translated))
        {
            if (string.Equals(current, translated, StringComparison.Ordinal))
            {
                return true;
            }

            var both = isName ? $"{translated} ({original})" : translated + "\n\n" + original;
            if (string.Equals(current, both, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (!string.IsNullOrEmpty(lastWritten) && string.Equals(current, lastWritten, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(original))
        {
            var tail = isName ? $" ({original})" : "\n\n" + original;
            if (current.Length > tail.Length && current.EndsWith(tail, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static FieldInfo? FindField(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private bool ResolvePluginManager()
    {
        try
        {
            var dalamud = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;
            var serviceOpen = dalamud.GetType("Dalamud.Service`1", throwOnError: true);
            var managerType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: true);
            if (serviceOpen is null || managerType is null)
            {
                return false;
            }

            var get = serviceOpen.MakeGenericType(managerType)
                                 .GetMethod("Get", BindingFlags.Public | BindingFlags.Static);
            pluginManager = get?.Invoke(null, null);
            if (pluginManager is null)
            {
                return false;
            }

            var type = pluginManager.GetType();
            propAvailable = type.GetProperty("AvailablePlugins", BindingFlags.Public | BindingFlags.Instance);
            propInstalled = type.GetProperty("InstalledPlugins", BindingFlags.Public | BindingFlags.Instance);
            return true;
        }
        catch (Exception e)
        {
            log("无法获取 PluginManager：" + e.Message);
            return false;
        }
    }
}
