using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>「列表刷新拦截」页。</summary>
internal sealed class BlockerTab
{
    private readonly Plugin plugin;

    public BlockerTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        var config = this.plugin.Config;

        ImGui.TextWrapped("防止浏览插件安装器时列表被自动重载顶回顶部。");
        ImGui.TextDisabled("只在安装器一侧拦截：不碰「重载仓库」接口——插件依赖它，重载与插件自动更新照常执行。");
        ImGui.TextDisabled("安装器关闭时列表照常重建；想让列表立刻更新，把安装器关掉再打开即可。");

        ImGui.Separator();

        var enabled = config.BlockerMode != BlockMode.Off;
        if (ImGui.Checkbox("安装器打开时不刷新列表###BlockerEnabled", ref enabled))
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

        if (ImGui.BeginChild("###BlockedList", new System.Numerics.Vector2(0, 160), true))
        {
            foreach (var line in sources)
            {
                ImGui.TextWrapped(line);
            }
        }

        ImGui.EndChild();
    }
}
