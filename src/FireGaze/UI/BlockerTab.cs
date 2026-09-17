using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>「插件安装器」页：列表刷新拦截 + 列表浏览位置。</summary>
internal sealed class BlockerTab
{
    private const string ClearButtonLabel = "清空记录";
    private const string ClearButtonId = "###ClearBlocked";

    private readonly Plugin plugin;

    public BlockerTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        if (Plugin.BlockerFeatureEnabled)
        {
            this.DrawToggleSection();
        }

        this.DrawRecordsSection();
        this.DrawStatusSection();
    }

    /// <summary>说明 + 开关。</summary>
    private void DrawToggleSection()
    {
        var config = this.plugin.Config;

        ImGui.TextWrapped("浏览插件安装器时，拦下「正在加载插件…」。");
        ImGui.TextDisabled("有需要可以手动刷新插件列表。");

        ImGui.Separator();

        var enabled = config.BlockerMode != BlockMode.Off;
        if (ImGui.Checkbox("禁止安装器打开期间的列表更新###BlockerEnabled", ref enabled))
        {
            this.plugin.SetBlockerEnabled(enabled);
        }

        var remember = config.RememberListScroll;
        if (ImGui.Checkbox("记住看到哪里（下次打开接着看）###RememberScroll", ref remember))
        {
            this.plugin.SetRememberListScroll(remember);
        }

        var writeLog = config.BlockerWriteLog;
        if (ImGui.Checkbox("把记录写进 dalamud.log###WriteLog", ref writeLog))
        {
            this.plugin.SetBlockerWriteLog(writeLog);
        }

        ImGui.Separator();
    }

    /// <summary>最近跳过的记录（在状态框上面）。</summary>
    private void DrawRecordsSection()
    {
        ImGui.Text("最近跳过的记录");
        ImGui.SameLine(this.RightAlignedOffsetFor(ClearButtonLabel));
        if (ImGui.SmallButton(ClearButtonLabel + ClearButtonId))
        {
            this.plugin.ClearBlockedRecords();
        }

        var sources = this.plugin.RecentBlockedSources;
        if (ImGui.BeginChild("###BlockedList", new System.Numerics.Vector2(0, 150), true))
        {
            if (sources.Count == 0)
            {
                ImGui.TextDisabled("（还没有跳过过）");
            }
            else
            {
                foreach (var line in sources)
                {
                    ImGui.TextUnformatted(line);
                }
            }
        }

        ImGui.EndChild();

        ImGui.Separator();
    }

    /// <summary>钩子状态（放最下面）。</summary>
    private void DrawStatusSection()
    {
        var lines = new[]
        {
            $"钩子状态：{this.plugin.BlockerStatusText}",
            $"已跳过列表重建：{this.plugin.BlockedCount} 次 · 最近 {this.plugin.LastSkipNote}",
            $"已跳过「打开安装器」的仓库重载：{this.plugin.OpenSkipNote}",
            $"已挡下「正在加载插件…」替换：{this.plugin.ListSuppressNote}",
        };

        var height = (ImGui.GetTextLineHeightWithSpacing() * lines.Length)
                     + (ImGui.GetStyle().WindowPadding.Y * 2f)
                     + 2f;

        if (ImGui.BeginChild("###BlockerStatus", new System.Numerics.Vector2(0, height), true))
        {
            foreach (var line in lines)
            {
                ImGui.TextUnformatted(line);
            }

            ImGui.TextDisabled(string.IsNullOrEmpty(this.plugin.LastAllowNote)
                ? "最近一次放行：—"
                : $"最近一次放行：{this.plugin.LastAllowNote}");
        }

        ImGui.EndChild();
    }

    /// <summary>把下一个控件右对齐（ImGui 的 SameLine 参数是「距行首的绝对偏移」）。</summary>
    private float RightAlignedOffsetFor(string text)
    {
        var width = ImGui.CalcTextSize(text).X + (ImGui.GetStyle().FramePadding.X * 2f);
        return ImGui.GetContentRegionMax().X - width;
    }
}
