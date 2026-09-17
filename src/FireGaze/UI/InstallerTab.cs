using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>「插件安装器」页：记住列表浏览位置 + 已知问题。</summary>
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
        ImGui.TextDisabled("想刷新列表内容，点安装器底部的「刷新插件列表」。");

        ImGui.Separator();

        var remember = this.plugin.Config.RememberListScroll;
        if (ImGui.Checkbox("记住看到哪里（下次打开接着看）###RememberScroll", ref remember))
        {
            this.plugin.SetRememberListScroll(remember);
        }

        ImGui.Separator();

        UiHelpers.ColoredWrapped(UiHelpers.Muted, "目前已知问题：");
        UiHelpers.ColoredWrapped(
            UiHelpers.Muted,
            "· 开启本插件的情况下，更新新版本的 OmniToolbox / XSZToolbox / PFRadar / OCNFarmer 时会加载失败，需要重启一次游戏解决。");
    }
}
