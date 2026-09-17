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
        ImGui.TextDisabled("重载本身照常执行（插件依赖它，不打断）；只跳过它引发的「安装器列表重建」。");
        ImGui.TextDisabled("手动刷新、打开安装器、在卫月设置里改仓库不受影响。");

        ImGui.Separator();
        ImGui.Text("跳过模式");

        if (ImGui.RadioButton("关闭###ModeOff", config.BlockerMode == BlockMode.Off))
        {
            this.plugin.SetBlockerMode(BlockMode.Off);
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("全部跳过###ModeAlways", config.BlockerMode == BlockMode.Always))
        {
            this.plugin.SetBlockerMode(BlockMode.Always);
        }

        ImGui.SameLine();
        ImGui.TextDisabled("插件发起的重载，一律不重建安装器列表");

        if (ImGui.RadioButton("只在安装器打开时跳过###ModeOpen", config.BlockerMode == BlockMode.InstallerOpenOnly))
        {
            this.plugin.SetBlockerMode(BlockMode.InstallerOpenOnly);
        }

        ImGui.SameLine();
        ImGui.TextDisabled("没在看列表时照常重建");

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
        ImGui.Text("最近跳过的来源");

        var sources = this.plugin.RecentBlockedSources;
        if (sources.Count == 0)
        {
            ImGui.TextDisabled("（还没有跳过过 —— 也有可能这段时间没有插件在后台重载）");
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
