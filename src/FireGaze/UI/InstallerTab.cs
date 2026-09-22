using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>「插件安装器」页：拦住安装器的自动刷新 + 记住列表浏览位置（两项都默认关）。</summary>
internal sealed class InstallerTab
{
    private readonly Plugin plugin;

    public InstallerTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        UiHelpers.ColoredWrapped(
            UiHelpers.Muted,
            "以下开关会将仓库刷新限制在后台，阻止插件安装器在使用时自动更新插件仓库，将列表重置回顶部。"
            + "此功能不影响获取新的插件列表，只影响列表显示。");

        ImGui.Separator();

        var block = this.plugin.Config.BlockInstallerAutoRefresh;
        if (ImGui.Checkbox("拦截打开插件管理器时的自动刷新###BlockAutoRefresh", ref block))
        {
            this.plugin.SetBlockInstallerAutoRefresh(block);
        }

        ImGui.Indent();
        UiHelpers.ColoredWrapped(
            UiHelpers.Muted,
            block
                ? "开启中：后台刷新照常进行，但不会再把你正在看的列表顶回顶部。"
                : "已关闭：后台刷新时，列表和滚动位置会被重置回顶部。");
        ImGui.Unindent();

        ImGui.Spacing();

        var remember = this.plugin.Config.RememberListScroll;
        if (ImGui.Checkbox("记住看到哪里（下次打开接着看）###RememberScroll", ref remember))
        {
            this.plugin.SetRememberListScroll(remember);
        }

        ImGui.Indent();
        UiHelpers.ColoredWrapped(
            UiHelpers.Muted,
            remember
                ? "开启中：关掉窗口再打开，列表会回到上次的位置。"
                : "已关闭：每次都从列表顶部开始。");

        if (remember && !block)
        {
            UiHelpers.ColoredWrapped(
                UiHelpers.Warn,
                "提示：上面那项关着时，后台刷新可能把记住的位置清零，这一项就会时灵时不灵。");
        }

        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        UiHelpers.ColoredWrapped(
            UiHelpers.Muted,
            "状态：" + this.plugin.InstallerFeatures.StatusText(block, remember));
        UiHelpers.ColoredWrapped(
            UiHelpers.Muted,
            "改动立即保存：拦截即刻生效，位置记忆在下次打开插件安装器时生效。");
    }
}
