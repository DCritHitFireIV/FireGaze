using FireGaze.RepoAudit;
using FireGaze.Internal;

namespace FireGaze.UI;

internal sealed partial class DiscoveryTab
{
    private InstalledPluginsIndex? discoveryInstalled;
    private Task<InstalledPluginsIndex>? discoveryInstalledTask;
    private DateTime discoveryInstalledAt;

    private void RefreshInstalledActions()
    {
        if (this.discoveryInstalledTask is { IsCompleted: true } task)
        {
            this.discoveryInstalledTask = null;
            if (task.Status == TaskStatus.RanToCompletion)
            {
                this.discoveryInstalled = task.Result;
                if (task.Result.Available)
                    this.ReportPluginInstalls(this.discoveryState.CompleteInstallations(task.Result.All
                        .Where(e => e.InternalName != this.installingName && PluginInstallerBridge.IsSuccessfullyInstalled(e.RawPlugin))
                        .Select(e => e.InternalName)));
            }
            this.discoveryInstalledAt = DateTime.Now;
        }
        if (this.discoveryInstalledTask is null && (DateTime.Now - this.discoveryInstalledAt).TotalSeconds > 5)
            this.discoveryInstalledTask = Task.Run(InstalledPluginsIndex.Build);
    }
}
