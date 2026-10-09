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
        SetStatus("正在安装 " + entry.DisplayName + "…", isError: false);
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
        SetStatus(name + "：" + result.Message, isError: !result.Ok && !result.StillRunning);
        if (result.Ok) ActivityLog.Info("插件发现", name + "：" + result.Message);
        else ActivityLog.Warning("插件发现", name + "：" + result.Message);
    }

    private void OpenInstallationFallback(string name)
    {
        var ok = PluginInstallerBridge.Open(name, out var error);
        if (ok) this.WatchDiscoveryInstallation(name);
        SetStatus(ok ? "已打开卫月安装器：" + name : error ?? "打开安装器失败", !ok);
    }
}
