using System.Reflection;
using FireGaze.Diagnostics;

namespace FireGaze.RepoAudit;

/// <summary>
///     启用一个「装了但没在跑」的插件。
/// </summary>
/// <remarks>
///     和插件安装器的「启用」同一条路（反编译 26-10-01-01 的 PluginInstallerWindow 得到）：
///     <code>
///       profileManager = Service&lt;ProfileManager&gt;.Get()
///       profile = Profiles.FirstOrDefault(p =&gt; p.WantsPlugin(id).HasValue) ?? DefaultProfile
///       await profile.AddOrUpdateAsync(id, internalName, state: true, apply: false)
///       await plugin.LoadAsync(PluginLoadReason.Installer)
///     </code>
///     <c>ProfileManager</c> / <c>Profile</c> 是 internal，只能反射；<c>PluginLoadReason</c> 是公开枚举。
///     反射失败不崩：返回 (false, 原因)，界面照旧显示一行说明。
/// </remarks>
internal static class PluginEnableBridge
{
    internal sealed record EnableReceipt(bool Ok, string Message, bool LoadedByCall);
    /// <summary>
    ///     启用插件并等它加载。回调线程是线程池（调用方自己决定怎么回写界面状态）。
    /// </summary>
    public static async Task<(bool Ok, string Message)> EnableAsync(object? rawPlugin)
    {
        var result = await EnableWithReceiptAsync(rawPlugin).ConfigureAwait(false);
        return (result.Ok, result.Message);
    }

    internal static Task<EnableReceipt> EnableWithReceiptAsync(object? rawPlugin) => Task.Run(() =>
    {
        // 日志带上插件名，排查「是谁在什么时候启用的」不用猜（2026-10-03）
        var name = ReadProperty(ReadProperty(rawPlugin, "Manifest"), "InternalName") as string ?? "?";
        try
        {
            var result = Enable(rawPlugin);
            if (result.Ok)
            {
                ActivityLog.Info("启用插件", $"{name}：{result.Message}");
            }
            else
            {
                ActivityLog.Error("启用插件", $"{name}：{result.Message}");
            }

            return result;
        }
        catch (Exception e)
        {
            var inner = e.GetBaseException();

            // 「已经加载」不是错误：并发 / 状态滞后时卫月会抛 InvalidPluginOperationException。
            // 2026-10-03 用户实测：ChilledLeves 明明在跑，点「启用插件」却报红。
            if (IsAlreadyLoadedError(inner) && ReadProperty(rawPlugin, "IsLoaded") is true)
            {
                return new EnableReceipt(true, "插件已经在运行，无需重复启用。", false);
            }

            ActivityLog.Error("启用插件", $"{name}：启用失败", e);
            return new EnableReceipt(false, $"启用插件出错：{e.GetType().Name}: {inner.Message}", false);
        }
    });

    private static bool IsAlreadyLoadedError(Exception e) =>
        e.GetType().Name.Contains("InvalidPluginOperation", StringComparison.Ordinal)
        || e.Message.Contains("已经加载", StringComparison.Ordinal)
        || e.Message.Contains("already loaded", StringComparison.OrdinalIgnoreCase);

    private static EnableReceipt Enable(object? rawPlugin)
    {
        if (rawPlugin is null)
        {
            return new(false, "无法读取此插件的信息，请刷新列表后重试。", false);
        }

        var type = rawPlugin.GetType();
        var workingID = ReadProperty(rawPlugin, "EffectiveWorkingPluginId") as Guid? ?? Guid.Empty;
        var internalName = (ReadProperty(ReadProperty(rawPlugin, "Manifest"), "InternalName") as string) ?? string.Empty;
        if (workingID == Guid.Empty || internalName.Length == 0)
        {
            return new(false, "无法读取此插件的信息，请刷新列表后重试。", false);
        }

        // 已经在跑就别再 LoadAsync（卫月会抛「无法加载 X, 已经加载」，用户看到的就是这个红字）
        if (ReadProperty(rawPlugin, "IsLoaded") is true)
        {
            return new(true, "插件已经在运行，无需重复启用。", false);
        }

        // ① 记下「这个插件想要运行」——与安装器一致，不记的话重开游戏又会变回停用
        var profileNote = string.Empty;
        try
        {
            profileNote = UpdateProfileWantState(workingID, internalName);
        }
        catch (Exception e)
        {
            profileNote = $"没能记下启用状态（{e.GetType().Name}），本次仍会尝试加载";
        }

        // ② 加载
        var reason = Enum.Parse<Dalamud.Plugin.PluginLoadReason>("Installer");
        var loadAsync = type.GetMethod("LoadAsync", BindingFlags.Public | BindingFlags.Instance, [typeof(Dalamud.Plugin.PluginLoadReason), typeof(bool), typeof(CancellationToken)]);
        if (loadAsync?.Invoke(rawPlugin, [reason, false, CancellationToken.None]) is not Task task)
        {
            return new(false, "当前无法启用此插件，请在卫月插件安装器中重试。", false);
        }

        task.GetAwaiter().GetResult();
        if (task.IsFaulted)
        {
            return new(false, "加载失败：" + (task.Exception?.GetBaseException().Message ?? "未知错误"), false);
        }

        if (ReadProperty(rawPlugin, "IsLoaded") is not true)
            return new(false, "加载操作已结束，但插件未处于已加载状态。", false);

        return new(true, profileNote.Length > 0
            ? $"已启用，插件已加载；{profileNote}"
            : "已启用，插件已加载；界面即刻生效。", true);
    }

    /// <summary>把「想要运行」写进插件所在的 Profile（优先已声明它的那份，否则默认 Profile）。</summary>
    private static string UpdateProfileWantState(Guid workingID, string internalName)
    {
        var dalamud = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;
        var serviceOpen = dalamud.GetType("Dalamud.Service`1", throwOnError: false);
        var profileManagerType = dalamud.GetType("Dalamud.Plugin.Internal.Profiles.ProfileManager", throwOnError: false);
        if (serviceOpen is null || profileManagerType is null)
        {
            return "未能保存启用状态，重启游戏后可能需要重新启用。";
        }

        var get = serviceOpen.MakeGenericType(profileManagerType)
            .GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var manager = get?.Invoke(null, null);
        if (manager is null)
        {
            return "未能保存启用状态，重启游戏后可能需要重新启用。";
        }

        object? chosen = null;
        if (manager.GetType().GetProperty("Profiles")?.GetValue(manager) is System.Collections.IEnumerable profiles)
        {
            // WantsPlugin 返回 bool?：装箱后 null = 这份 Profile 压根没管过这个插件
            foreach (var profile in profiles)
            {
                if (profile is not null
                    && profile.GetType().GetMethod("WantsPlugin", [typeof(Guid)])?.Invoke(profile, [workingID]) is not null)
                {
                    chosen = profile;
                    break;
                }
            }
        }

        chosen ??= manager.GetType().GetProperty("DefaultProfile")?.GetValue(manager);
        if (chosen is null)
        {
            return "未能保存启用状态，重启游戏后可能需要重新启用。";
        }

        var addOrUpdate = chosen.GetType().GetMethod("AddOrUpdateAsync", [typeof(Guid), typeof(string), typeof(bool), typeof(bool)]);
        if (addOrUpdate?.Invoke(chosen, [workingID, internalName, true, false]) is Task task)
        {
            task.GetAwaiter().GetResult();
        }

        return string.Empty;
    }

    private static object? ReadProperty(object? target, string name) =>
        target?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.GetValue(target)
        ?? target?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
}
