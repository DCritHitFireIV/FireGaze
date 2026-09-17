using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>「插件安装器」页：列表刷新拦截 + 安装器窗口位置记忆。</summary>
internal sealed class BlockerTab
{
    private readonly Plugin plugin;

    public BlockerTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        var drewSection = false;

        if (Plugin.BlockerFeatureEnabled)
        {
            this.DrawBlockerSection();
            drewSection = true;
        }

        if (drewSection)
        {
            ImGui.Separator();
            ImGui.Spacing();
        }

        this.DrawWindowSection();
    }

    private void DrawBlockerSection()
    {
        var config = this.plugin.Config;

        ImGui.TextWrapped("浏览插件安装器时，列表被后台重载重建会把浏览位置顶回顶部；开启后把重建拦下。");
        ImGui.TextDisabled("只在安装器一侧拦截：不碰「重载仓库」接口——插件依赖它，重载与插件自动更新照常执行。");
        ImGui.TextDisabled("开启后：打开安装器触发的那次刷新也会被跳过（列表保持不动）。");
        ImGui.TextDisabled("在安装器底部点「刷新插件列表」仍然生效（算作用户显式操作）。");

        ImGui.Separator();

        var enabled = config.BlockerMode != BlockMode.Off;
        if (ImGui.Checkbox("禁止安装器打开期间的列表更新###BlockerEnabled", ref enabled))
        {
            this.plugin.SetBlockerEnabled(enabled);
        }

        var writeLog = config.BlockerWriteLog;
        if (ImGui.Checkbox("把记录写进 dalamud.log###WriteLog", ref writeLog))
        {
            this.plugin.SetBlockerWriteLog(writeLog);
        }

        ImGui.Separator();

        ImGui.Text($"钩子状态：{this.plugin.BlockerStatusText}");
        ImGui.Text($"已跳过列表重建：{this.plugin.BlockedCount} 次");
        ImGui.TextDisabled(string.IsNullOrEmpty(this.plugin.LastAllowNote)
            ? "最近一次放行：—"
            : $"最近一次放行：{this.plugin.LastAllowNote}");

        if (ImGui.Button("清空记录###ClearBlocked"))
        {
            this.plugin.ClearBlockedRecords();
        }

        ImGui.Separator();
        ImGui.Text("最近跳过的记录");

        var sources = this.plugin.RecentBlockedSources;
        if (sources.Count == 0)
        {
            ImGui.TextDisabled("（还没有跳过过）");
            return;
        }

        if (ImGui.BeginChild("###BlockedList", new System.Numerics.Vector2(0, 140), true))
        {
            foreach (var line in sources)
            {
                ImGui.TextWrapped(line);
            }
        }

        ImGui.EndChild();
    }

    private void DrawWindowSection()
    {
        ImGui.Text("安装器窗口位置");

        var remember = this.plugin.Config.RememberInstallerWindow;
        if (ImGui.Checkbox("记住窗口位置（下次在原处打开）###RememberWin", ref remember))
        {
            this.plugin.SetRememberInstallerWindow(remember);
        }

        ImGui.TextDisabled("拖动或缩放后自动记住；位置跑到屏幕外时会夹回可见范围。");
        ImGui.TextDisabled(this.plugin.InstallerWindowNote);
    }
}
