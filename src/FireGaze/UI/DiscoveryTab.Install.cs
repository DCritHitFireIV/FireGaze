using FireGaze.Diagnostics;
using FireGaze.Internal;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

internal sealed partial class DiscoveryTab
{
    private Task<PluginInstallResult>? installTask;
    private string? installingName;
    private string? installFallbackName;

    private void StartInstallation(TranslationIndexEntry entry)
    {
        if (this.installTask is not null) return;
        this.installingName = entry.InternalName;
        this.installFallbackName = null;
        this.WatchDiscoveryInstallation(entry.InternalName);
        SetStatus("正在准备安装 " + entry.DisplayName + "，必要时自动刷新插件库…", isError: false);
        ActivityLog.Info("插件发现", "请求卫月安装 " + entry.InternalName);
        this.installTask = PluginInstallerBridge.InstallAsync(entry.InternalName, entry.RepositoryURL, entry.IsOfficial);
    }

    private void PollInstallation()
    {
        if (this.installTask is not { IsCompleted: true } task) return;
        var name = this.installingName!;
        this.installTask = null;
        this.installingName = null;
        var result = task.Status == TaskStatus.RanToCompletion ? task.Result
            : new PluginInstallResult(false, false, true, "无法确认安装结果，请打开卫月安装器查看");
        if (result.Ok && !result.AlreadyInstalled)
            this.ReportPluginInstalls(this.discoveryState.CompleteInstallations([name]));
        else if (!result.StillRunning)
            this.discoveryState.ForgetInstallation(name);
        this.installFallbackName = result.Ok ? null : name;
        // Read a fresh snapshot so the successful row immediately offers Go Translate.
        this.discoveryInstalledTask = null;
        this.discoveryInstalled = InstalledPluginsIndex.Build();
        this.discoveryInstalledAt = DateTime.Now;
        if (result.Ok) ActivityLog.Info("插件发现", name + "：" + result.Message);
        else ActivityLog.Warning("插件发现", name + "：" + result.Message);
        if (!result.Ok)
        {
            // Keep native safety/permission decisions intact, but complete the navigation fallback for the player.
            var opened = PluginInstallerBridge.Open(name, out var error);
            if (opened)
            {
                if (!result.StillRunning) this.WatchDiscoveryInstallation(name);
                SetStatus(name + (result.StillRunning ? "：卫月仍在处理，已定位安装器查看进度。"
                    : "：已自动定位到插件安装器。"), isError: false);
                ActivityLog.Info("插件发现", name + "：自动打开并定位原生安装器");
            }
            else
            {
                SetStatus(name + "：暂未完成安装，请稍后重试；也可从插件菜单打开安装器。", isError: false);
                ActivityLog.Warning("插件发现", name + "：自动定位安装器不可用：" + error);
            }
        }
        else SetStatus(name + "：" + result.Message, isError: false);
    }

    private void OpenInstallationFallback(string name)
    {
        var ok = PluginInstallerBridge.Open(name, out var error);
        if (ok) this.WatchDiscoveryInstallation(name);
        SetStatus(ok ? "已打开卫月安装器：" + name : error ?? "打开安装器失败", !ok);
    }
}
