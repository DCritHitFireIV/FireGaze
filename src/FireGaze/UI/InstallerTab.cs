using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>「插件安装器」页：记住列表浏览位置。</summary>
internal sealed class InstallerTab
{
    private readonly Plugin plugin;

    public InstallerTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        ImGui.TextWrapped("记住插件安装器列表的浏览位置，下次打开接着看。");
        ImGui.TextDisabled("实现方式：只读/写 ImGui 里那个列表子窗口的滚动值（不使用任何钩子）。");

        ImGui.Separator();

        var remember = this.plugin.Config.RememberListScroll;
        if (ImGui.Checkbox("记住看到哪里（下次打开接着看）###RememberScroll", ref remember))
        {
            this.plugin.SetRememberListScroll(remember);
        }
    }
}
