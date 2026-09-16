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

        ImGui.TextWrapped("防止浏览插件安装器时自动重载插件仓库。");
        ImGui.TextDisabled("手动刷新、打开安装器、在卫月设置里改仓库不受影响。（默认关闭，需要时打开）");

        ImGui.Separator();
        ImGui.Text("拦截模式");

        if (ImGui.RadioButton("关闭拦截###ModeOff", config.BlockerMode == BlockMode.Off))
        {
            this.plugin.SetBlockerMode(BlockMode.Off);
        }

        ImGui.SameLine();
        ImGui.TextDisabled("(默认) 不拦截");

        if (ImGui.RadioButton("全部拦截###ModeAlways", config.BlockerMode == BlockMode.Always))
        {
            this.plugin.SetBlockerMode(BlockMode.Always);
        }

        ImGui.SameLine();
        ImGui.TextDisabled("插件发起的后台刷新一律跳过");

        if (ImGui.RadioButton("只在安装器打开时拦截###ModeOpen", config.BlockerMode == BlockMode.InstallerOpenOnly))
        {
            this.plugin.SetBlockerMode(BlockMode.InstallerOpenOnly);
        }

        ImGui.SameLine();
        ImGui.TextDisabled("没在看列表时照常刷新");

        var writeLog = config.BlockerWriteLog;
        if (ImGui.Checkbox("把拦截记录写进 dalamud.log###WriteLog", ref writeLog))
        {
            this.plugin.SetBlockerWriteLog(writeLog);
        }

        ImGui.Separator();

        ImGui.Text($"钩子状态：{this.plugin.BlockerStatusText}");
        ImGui.Text($"累计拦截：{this.plugin.BlockedCount} 次");

        if (ImGui.Button("清空记录###ClearBlocked"))
        {
            this.plugin.ClearBlockedRecords();
        }

        ImGui.Separator();
        ImGui.Text("最近被拦截的来源");

        var sources = this.plugin.RecentBlockedSources;
        if (sources.Count == 0)
        {
            ImGui.TextDisabled("（还没有拦到过 —— 也有可能这段时间没有插件在后台刷新）");
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
