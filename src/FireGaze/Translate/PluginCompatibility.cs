using System.Reflection;

namespace FireGaze.Translate;

/// <summary>
///     插件兼容性检测：与卫月安装器同一口径（<c>PluginManager.IsManifestEligible</c>），
///     有效 API 等级低于「当前卫月 API 等级 − 1」的插件卫月根本不会加载，插件发现里也没必要列。
/// </summary>
/// <remarks>
///     卫月的判定（2026-10-07 反编译核实）：
///     <code>
///     if (manifest.ApplicableVersion &lt; dalamud.StartInfo.GameVersion) return false;
///     var level = (可用测试分支且 TestingDalamudApiLevel &gt; DalamudApiLevel)
///         ? TestingDalamudApiLevel : DalamudApiLevel;
///     if (level &lt; DalamudApiLevel - 1) return false;   // 当前等级或当前 − 1 都算合格
///     </code>
///     这里只做等级这半段；词表没带等级字段（老表 / 云端还没出这份数据）时按「不拦」处理。
/// </remarks>
internal static class PluginCompatibility
{
    private static readonly object Gate = new();
    private static bool resolved;
    private static int? current;

    /// <summary>
    ///     当前卫月的 API 等级；读不到返回 null（= 不判断）。
    ///     只取 <c>PluginManager</c> 类型上的**静态**属性，不走 <c>Service&lt;T&gt;.Get()</c>
    ///     ——后者在非卫月进程（fgtest）里会等一个永不完成的 TCS。
    /// </summary>
    internal static int? CurrentAPILevel()
    {
        lock (Gate)
        {
            if (resolved)
            {
                return current;
            }

            resolved = true;
            try
            {
                var dalamud = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;
                var managerType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager", throwOnError: false);
                var property = managerType?.GetProperty(
                    "DalamudApiLevel",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                if (property is not null && property.GetValue(null) is int value)
                {
                    current = value;
                }
            }
            catch
            {
                // 读不到就当不知道
            }

            return current;
        }
    }

    /// <summary>
    ///     纯函数版口径：等级低于「当前 − 1」= 不兼容；任一侧未知 = 不拦（fail-open）。
    /// </summary>
    internal static bool IsAPICompatible(int? level, int? currentAPILevel)
    {
        if (level is not { } value || currentAPILevel is not { } current)
        {
            return true;
        }

        return value >= current - 1;
    }

    /// <summary>用当前进程里卫月的等级判定。</summary>
    internal static bool IsAPICompatible(int? level) => IsAPICompatible(level, CurrentAPILevel());
}
