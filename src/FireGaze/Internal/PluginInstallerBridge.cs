using System.Collections;
using System.Diagnostics;
using System.Reflection;
using FireGaze.RepoAudit;

namespace FireGaze.Internal;

internal sealed record PluginInstallResult(bool Ok, bool AlreadyInstalled, bool StillRunning, string Message);

internal static class PluginInstallerBridge
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    public static Task<PluginInstallResult> InstallAsync(string internalName, string? repositoryURL, bool official)
    {
        try
        {
            var assembly = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;
            object Resolve(string name)
            {
                var service = assembly.GetType("Dalamud.Service`1", true)!;
                var type = assembly.GetType(name, true)!;
                return service.MakeGenericType(type).GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
            }
            return InstallFromServicesAsync(Resolve("Dalamud.Interface.Internal.DalamudInterface"),
                Resolve("Dalamud.Plugin.Internal.PluginManager"), internalName, repositoryURL, official, TimeSpan.FromMinutes(5));
        }
        catch (Exception e)
        {
            return Task.FromResult(Failed("当前卫月不支持一键安装，请打开安装器：" + e.GetBaseException().Message));
        }
    }

    internal static async Task<PluginInstallResult> InstallFromServicesAsync(object ui, object manager,
        string internalName, string? repositoryURL, bool official, TimeSpan timeout)
    {
        if (!InstallGate.Wait(0)) return Failed("正在安装另一个插件，请等它完成后再试");
        var started = false;
        try
        {
            if (string.IsNullOrWhiteSpace(internalName)) return Failed("没有指定要安装的插件");
            if (Flag(manager, "SafeMode")) return Failed("卫月处于安全模式，请先退出安全模式再安装");
            if (!Flag(manager, "PluginsReady") || !Flag(manager, "ReposReady"))
                return Failed("卫月正在读取插件或刷新插件库，请稍后重试");
            var existing = Items(manager, "InstalledPlugins").FirstOrDefault(p => Name(p) == internalName);
            if (existing is not null)
                return new(IsSuccessfullyInstalled(existing), true, false, "这个插件已经安装，请到插件汉化查看；加载异常可在卫月安装器处理");

            var window = Read(ui, "pluginWindow") ?? throw new MissingMemberException("pluginWindow");
            foreach (var status in new[] { "installStatus", "updateStatus", "enableDisableStatus" })
                if (Read(window, status)?.ToString() == "InProgress") return Failed("卫月正在安装、更新或启停插件，请完成后重试");

            var candidates = Items(manager, "AvailablePlugins")
                .Where(m => Name(m) == internalName && MatchesSource(Read(m, "SourceRepo")!, repositoryURL, official)).ToList();
            if (candidates.Count == 0) return Failed("这条来源暂时没有可安装的版本，请刷新插件库后重试，或打开安装器查看");
            if (candidates.Count != 1) return Failed("这条来源存在多个同名版本，请在卫月安装器确认后安装");
            var manifest = candidates[0];
            if (!InvokeFlag(manager, "IsManifestEligible", manifest)) return Failed("这个插件暂不符合卫月的安装条件");
            var canTest = InvokeFlag(manager, "CanUseTesting", manifest);
            var testing = InvokeFlag(manager, "UseTesting", manifest);
            var exclusive = Flag(manifest, "IsTestingExclusive");
            if ((testing || exclusive) && !canTest) return Failed("这个插件需要测试版，请先在卫月安装器开启相应测试选项");
            testing |= exclusive;
            if (Read(manifest, "AssemblyVersion") is null || (testing && Read(manifest, "TestingAssemblyVersion") is null))
                return Failed("插件清单缺少可安装的版本信息，请刷新插件库后重试");
            if (InvokeFlag(window, "IsAvailableManifestOutdated", manifest, testing)
                || InvokeFlag(window, "IsAvailableManifestIncompatible", manifest))
                return Failed("这个版本不兼容当前卫月，请等待插件更新");

            var start = window.GetType().GetMethod("StartInstall", InstanceFlags, [manifest.GetType(), typeof(bool)])
                ?? throw new MissingMethodException("StartInstall");
            var previousError = Read(window, "errorModalMessage") as string;
            // Use the native installer unchanged: it owns downloads, rollback, loading and notifications.
            started = true;
            start.Invoke(window, [manifest, testing]);
            var elapsed = Stopwatch.StartNew();
            while (Read(window, "installStatus")?.ToString() == "InProgress")
            {
                if (elapsed.Elapsed >= timeout)
                    return new(false, false, true, "卫月仍在安装；请打开安装器查看进度，完成前不要重复安装");
                await Task.Delay(100).ConfigureAwait(false);
            }
            // Native continuation resets the status before publishing its error message.
            await Task.Delay(50).ConfigureAwait(false);
            var installed = Items(manager, "InstalledPlugins").FirstOrDefault(p => Name(p) == internalName);
            if (installed is not null && IsSuccessfullyInstalled(installed) && MatchesInstalledSource(installed, repositoryURL, official))
                return new(true, false, false, "安装成功，点「去汉化」继续");
            var error = Read(window, "errorModalMessage") as string;
            return Failed(!string.IsNullOrWhiteSpace(error) && error != previousError
                ? "安装失败：" + error : "安装未成功加载，请在卫月安装器查看错误后重试");
        }
        catch (Exception e)
        {
            return new(false, false, started, (started ? "无法确认安装结果，请打开卫月安装器查看：" : "一键安装不可用，请打开卫月安装器：")
                + e.GetBaseException().Message);
        }
        finally { InstallGate.Release(); }
    }

    internal static bool IsSuccessfullyInstalled(object plugin)
    {
        try { return Read(plugin, "State")?.ToString() is "Loaded" or "Unloaded"; }
        catch { return false; }
    }

    private static PluginInstallResult Failed(string message) => new(false, false, false, message);
    private static object? Read(object value, string member)
    {
        var type = value.GetType();
        var property = type.GetProperty(member, InstanceFlags);
        if (property is not null) return property.GetValue(value);
        var field = type.GetField(member, InstanceFlags) ?? throw new MissingMemberException(type.FullName, member);
        return field.GetValue(value);
    }
    private static bool Flag(object value, string member) => Read(value, member) is bool flag ? flag : throw new InvalidOperationException(member);
    private static bool InvokeFlag(object value, string method, params object[] args)
        => value.GetType().GetMethod(method, InstanceFlags)?.Invoke(value, args) is bool flag ? flag : throw new MissingMethodException(method);
    private static IEnumerable<object> Items(object value, string member)
        => Read(value, member) is IEnumerable items ? items.Cast<object>().ToArray() : throw new InvalidOperationException(member);
    private static string? Name(object plugin) => Read(plugin, "InternalName") as string;
    private static bool MatchesSource(object repo, string? url, bool official)
        => official ? !Flag(repo, "IsThirdParty") : Flag(repo, "IsThirdParty") && SameRepository(Read(repo, "PluginMasterUrl") as string, url);
    private static bool MatchesInstalledSource(object plugin, string? url, bool official)
    {
        var installedURL = plugin.GetType().GetProperty("InstalledFromUrl", InstanceFlags)?.GetValue(plugin) as string
            ?? (Read(plugin, "Manifest") is { } manifest ? Read(manifest, "InstalledFromUrl") as string : null);
        return official ? installedURL == "OFFICIAL" : SameRepository(installedURL, url);
    }
    private static bool SameRepository(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
            && Uri.TryCreate(first.Trim(), UriKind.Absolute, out var firstUri)
            && Uri.TryCreate(second.Trim(), UriKind.Absolute, out var secondUri)
            && firstUri.Scheme is "http" or "https" && secondUri.Scheme is "http" or "https"
            && InstalledPluginsIndex.NormalizeRepositoryURL(first) == InstalledPluginsIndex.NormalizeRepositoryURL(second)
            && firstUri.Query == secondUri.Query;

    public static bool Open(string query, out string? error)
    {
        try
        {
            var assembly = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;
            var service = assembly.GetType("Dalamud.Service`1");
            var type = assembly.GetType("Dalamud.Interface.Internal.DalamudInterface");
            var instance = service?.MakeGenericType(type!).GetMethod("Get", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            if (instance is not null) return OpenFromInterface(instance, query, out error);
            error = "没有找到卫月安装器，请在插件安装器里搜索 " + query;
            return false;
        }
        catch (Exception e) { error = "打开安装器失败：" + e.GetBaseException().Message; return false; }
    }

    internal static bool OpenFromInterface(object instance, string query, out string? error)
    {
        error = null;
        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = instance.GetType();
            var window = type.GetField("pluginWindow", flags)?.GetValue(instance);
            var open = type.GetMethod("OpenPluginInstallerTo", flags);
            var search = window?.GetType().GetMethod("SetSearchText", flags, [typeof(string)]);
            if (window is null || open is null || search is null)
            {
                error = "当前卫月安装器不支持定位搜索，请手动搜索 " + query;
                return false;
            }
            var kind = Enum.Parse(open.GetParameters()[0].ParameterType, "AllPlugins");
            open.Invoke(instance, [kind]);
            search.Invoke(window, [query]);
            return true;
        }
        catch (Exception e) { error = "打开安装器失败：" + e.GetBaseException().Message; return false; }
    }
}
