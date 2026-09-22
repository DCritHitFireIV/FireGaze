using System.Collections;
using System.Reflection;
using Newtonsoft.Json;

namespace FireGaze.RepoAudit;

/// <summary>
///     用卫月自己的类型做仓库内容契约校验，判定结果和卫月 <c>PluginRepository.ReloadAsync</c> 完全一致：
///       · 反序列化目标 = <c>Dalamud.Plugin.Internal.Types.Manifest.RemotePluginManifest</c>（internal，反射拿）；
///       · 反序列化器 = 游戏进程里那个 Newtonsoft.Json（与卫月同版本、同设置）；
///       · 单条过滤规则 = <c>IsValidManifest</c>（InternalName / Name 非空，AssemblyVersion 不为 null）。
/// </summary>
internal static class ManifestCheck
{
    /// <summary>
    ///     校验结果。
    /// </summary>
    /// <param name="Ok">内容能否被卫月当仓库加载。</param>
    /// <param name="Count">数组里条目总数。</param>
    /// <param name="Dropped">会被卫月单条丢弃的条目数。</param>
    /// <param name="Error">失败原因。</param>
    /// <param name="CheckerAvailable">校验器本身是否可用（不可用时不要给仓库下结论）。</param>
    public sealed record CheckResult(bool Ok, int Count, int Dropped, string? Error, bool CheckerAvailable);

    private static readonly object InitLock = new();

    private static bool initialized;
    private static bool failureLogged;
    private static MethodInfo? deserializeString;
    private static Type? manifestType;
    private static PropertyInfo? propInternalName;
    private static PropertyInfo? propName;
    private static PropertyInfo? propAssemblyVersion;

    public static CheckResult Check(string text)
    {
        EnsureInitialized();

        if (deserializeString is null || manifestType is null)
        {
            return new CheckResult(false, 0, 0, "无法访问卫月的 RemotePluginManifest 类型", false);
        }

        try
        {
            var listType = typeof(List<>).MakeGenericType(manifestType);
            var method = deserializeString.MakeGenericMethod(listType);
            var obj = method.Invoke(null, [text]);
            if (obj is not IList list)
            {
                return new CheckResult(false, 0, 0, "反序列化结果为 null", true);
            }

            var dropped = 0;
            foreach (var item in list)
            {
                if (item is null)
                {
                    dropped++;
                    continue;
                }

                var internalName = propInternalName?.GetValue(item) as string;
                var name = propName?.GetValue(item) as string;
                var version = propAssemblyVersion?.GetValue(item);
                if (string.IsNullOrWhiteSpace(internalName) || string.IsNullOrWhiteSpace(name) || version is null)
                {
                    dropped++;
                }
            }

            return new CheckResult(true, list.Count, dropped, null, true);
        }
        catch (TargetInvocationException e)
        {
            var inner = e.InnerException ?? e;
            return new CheckResult(false, 0, 0, Shorten(inner.Message), true);
        }
        catch (Exception e)
        {
            return new CheckResult(false, 0, 0, Shorten(e.Message), true);
        }
    }

    private static string Shorten(string message)
    {
        message = message.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return message.Length <= 160 ? message : message[..160] + "…";
    }

    /// <summary>
    ///     线程安全的一次性初始化（扫描是多线程的，必须在锁里完成后再置位）。
    /// </summary>
    private static void EnsureInitialized()
    {
        if (Volatile.Read(ref initialized))
        {
            return;
        }

        lock (InitLock)
        {
            if (initialized)
            {
                return;
            }

            try
            {
                var dalamud = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;
                manifestType = dalamud.GetType(
                    "Dalamud.Plugin.Internal.Types.Manifest.RemotePluginManifest",
                    throwOnError: true);

                propInternalName = manifestType!.GetProperty("InternalName", BindingFlags.Public | BindingFlags.Instance);
                propName = manifestType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                propAssemblyVersion = manifestType.GetProperty("AssemblyVersion", BindingFlags.Public | BindingFlags.Instance);

                deserializeString = typeof(JsonConvert)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "DeserializeObject"
                                         && m.IsGenericMethodDefinition
                                         && m.GetParameters().Length == 1
                                         && m.GetParameters()[0].ParameterType == typeof(string));

                if (deserializeString is null)
                {
                    throw new MissingMethodException("Newtonsoft.Json.JsonConvert.DeserializeObject<T>(string)");
                }

                initialized = true;
            }
            catch (Exception e)
            {
                manifestType = null;
                deserializeString = null;
                if (!failureLogged)
                {
                    failureLogged = true;
                    PluginLogFallback.Write("校验器初始化失败：" + e.Message);
                }
            }
        }
    }
}

/// <summary>
///     校验器没法用 IPluginLog（静态类初始化太早时的兜底日志）。
/// </summary>
internal static class PluginLogFallback
{
    public static Action<string>? Sink { get; set; }

    public static void Write(string message) => Sink?.Invoke(message);
}
