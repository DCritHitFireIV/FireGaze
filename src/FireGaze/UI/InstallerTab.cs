using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>
///     「插件安装器」页：刷新时保持列表与重新打开时恢复浏览位置（两项都默认关）。
/// </summary>
internal sealed class InstallerTab
{
    private readonly Plugin plugin;
    private readonly RepoAuditTab repoAudit;
    private readonly TranslateTab translateTab;

    public InstallerTab(Plugin plugin, RepoAuditTab repoAudit, TranslateTab translateTab)
    {
        this.plugin = plugin;
        this.repoAudit = repoAudit;
        this.translateTab = translateTab;
    }

    public void Draw()
    {
        // Keep the installer workflow first. Icon maintenance still advances below the
        // surface on every frame, but its manual cache controls are intentionally hidden.
        ImGui.TextUnformatted("安装器");

        // 图标检查 / 下载的推进：面板搬到了这一页，下载期间每帧都要推进
        repoAudit.TickIconMaintenance();

        var block = plugin.Config.BlockInstallerAutoRefresh;
        if (ImGui.Checkbox("刷新时保持浏览位置###BlockAutoRefresh", ref block))
        {
            plugin.SetBlockInstallerAutoRefresh(block);
        }

        ImGui.Indent();
        UiHelpers.ColoredWrapped(
            UiHelpers.Muted,
            block
                ? "刷新期间保持当前列表与浏览位置。"
                : "刷新时，列表可能回到顶部。");
        ImGui.Unindent();

        ImGui.Spacing();

        var remember = plugin.Config.RememberListScroll;
        if (ImGui.Checkbox("重新打开时恢复浏览位置###RememberScroll", ref remember))
        {
            plugin.SetRememberListScroll(remember);
        }

        ImGui.Indent();
        if (remember && !block)
        {
            UiHelpers.ColoredWrapped(
                UiHelpers.Warn,
                "建议同时开启「刷新时保持浏览位置」。");
        }

        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("简介汉化");
        translateTab.Draw();
    }
}
