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
                ? "已开启：后台刷新照常进行，当前浏览位置保持不变。"
                : "已关闭：后台刷新可能使列表回到顶部。");
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
                "自动刷新可能重置浏览位置。建议同时开启上方的刷新拦截。");
        }

        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        repoAudit.DrawIconMaintenance();
    }
}
