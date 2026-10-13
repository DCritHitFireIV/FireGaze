using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>
///     「插件安装器」页：拦住安装器的自动刷新 + 记住列表浏览位置（两项都默认关）。
/// </summary>
internal sealed class InstallerTab
{
    private readonly Plugin plugin;
    private readonly RepoAuditTab repoAudit;

    public InstallerTab(Plugin plugin, RepoAuditTab repoAudit)
    {
        this.plugin = plugin;
        this.repoAudit = repoAudit;
    }

    public void Draw()
    {
        // 图标检查 / 下载的推进：面板搬到了这一页，下载期间每帧都要推进
        repoAudit.TickIconMaintenance();

        var block = plugin.Config.BlockInstallerAutoRefresh;
        if (ImGui.Checkbox("拦截打开插件管理器时的自动刷新###BlockAutoRefresh", ref block))
        {
            plugin.SetBlockInstallerAutoRefresh(block);
        }

        ImGui.Indent();
        UiHelpers.ColoredWrapped(
            UiHelpers.Muted,
            block
                ? "开启中：后台刷新照常进行，但不会再把你正在看的列表顶回顶部。"
                : "已关闭：后台刷新时，列表和滚动位置会被重置回顶部。");
        ImGui.Unindent();

        ImGui.Spacing();

        var remember = plugin.Config.RememberListScroll;
        if (ImGui.Checkbox("记住看到哪里（下次打开接着看）###RememberScroll", ref remember))
        {
            plugin.SetRememberListScroll(remember);
        }

        ImGui.Indent();
        if (remember && !block)
        {
            UiHelpers.ColoredWrapped(
                UiHelpers.Warn,
                "提示：上面那项关着时，后台刷新可能把记住的位置清零，这一项就会时灵时不灵。");
        }

        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        repoAudit.DrawIconMaintenance();
    }
}
