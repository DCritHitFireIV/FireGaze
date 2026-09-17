using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>「拦住自动更新」页：拦住安装器的自动刷新 + 记住列表浏览位置。</summary>
internal sealed class InstallerTab
{
    private readonly Plugin plugin;

    public InstallerTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        ImGui.TextWrapped("让插件安装器别再自己刷新：自动重载不再把列表和你的位置顶掉，并且记住你上次看到哪里。");
        ImGui.Separator();

        var block = this.plugin.Config.BlockInstallerAutoRefresh;
        if (ImGui.Checkbox("防止打开插件管理器自动刷新###BlockAutoRefresh", ref block))
        {
            this.plugin.SetBlockInstallerAutoRefresh(block);
        }

        ImGui.TextDisabled("刷新仍在后台进行，只是不再把你正在看的列表顶回顶部。");

        ImGui.Spacing();

        var remember = this.plugin.Config.RememberListScroll;
        if (ImGui.Checkbox("记住看到哪里（下次打开接着看）###RememberScroll", ref remember))
        {
            this.plugin.SetRememberListScroll(remember);
        }

        ImGui.TextDisabled("关掉窗口再打开，列表会回到上次的位置。");
    }
}
