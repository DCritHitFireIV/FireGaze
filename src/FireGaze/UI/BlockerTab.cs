using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>「插件安装器」页：列表刷新拦截 + 列表浏览位置。</summary>
internal sealed class BlockerTab
{
    private const string ClearId = "###ClearBlocked";
    private const string CopyId = "###CopyDiag";

    private readonly Plugin plugin;

    /// <summary>「清空记录」的二次确认截止时间（第一次点击后几秒内再点才真清）。</summary>
    private DateTime clearArmedUntil = DateTime.MinValue;

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
        ImGui.TextDisabled("打开安装器不再联网重拉仓库，也不会重置排序与搜索。");
        ImGui.TextDisabled("有需要可以手动刷新插件列表。");

        ImGui.Separator();

        var enabled = config.BlockerMode != BlockMode.Off;
        if (ImGui.Checkbox("安装器打开时拦下列表更新###BlockerEnabled", ref enabled))
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
        ImGui.Text("最近拦下的记录");

        var hasRecords = this.plugin.HasBlockerRecords;
        var armed = DateTime.UtcNow < this.clearArmedUntil;

        ImGui.SameLine(this.RightAlignedOffsetFor("复制诊断", "清空记录"));
        if (ImGui.SmallButton("复制诊断" + CopyId))
        {
            ImGui.SetClipboardText(this.plugin.BuildDiagnostics());
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(!hasRecords);
        if (ImGui.SmallButton((armed ? "确认清空？" : "清空记录") + ClearId))
        {
            if (armed)
            {
                this.plugin.ClearBlockedRecords();
                this.clearArmedUntil = DateTime.MinValue;
            }
            else
            {
                this.clearArmedUntil = DateTime.UtcNow.AddSeconds(5);
            }
        }

        ImGui.EndDisabled();

        if (armed)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("再点一次即清空（含计数）");
        }

        var sources = this.plugin.RecentBlockedSources;
        var rows = Math.Clamp(sources.Count, 1, 6);
        var height = (ImGui.GetTextLineHeightWithSpacing() * rows)
                     + (ImGui.GetStyle().WindowPadding.Y * 2f)
                     + 2f;

        if (ImGui.BeginChild("###BlockedList", new System.Numerics.Vector2(0, height), true))
        {
            if (sources.Count == 0)
            {
                ImGui.TextDisabled("（还没有拦下）");
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
        var hooked = this.plugin.HookSummary.StartsWith("已就绪", StringComparison.Ordinal);
        var allow = this.plugin.LastAllowNote;

        var lines = new List<string>
        {
            CounterLine("已拦下列表重建", this.plugin.BlockedCount, this.plugin.LastSkipNote),
            CounterLine("已拦下「打开安装器」的仓库重载", this.plugin.OpenSkipCount, this.plugin.OpenSkipTime),
            CounterLine("已拦下「正在加载插件…」替换", this.plugin.ListSuppressCount, this.plugin.ListSuppressTime),
            string.IsNullOrEmpty(allow) ? "最近一次未拦下：—" : $"最近一次未拦下：{allow}",
        };

        // 高度按「实际行数 + 钩子状态那一行的可能换行余量」算，别把最后一行裁掉
        var height = (ImGui.GetTextLineHeightWithSpacing() * (lines.Count + 2))
                     + (ImGui.GetStyle().WindowPadding.Y * 2f)
                     + 2f;

        if (ImGui.BeginChild("###BlockerStatus", new System.Numerics.Vector2(0, height), true))
        {
            UiHelpers.ColoredWrapped(
                hooked ? UiHelpers.Good : UiHelpers.Warn,
                $"拦截状态：{this.plugin.HookSummary}");

            foreach (var line in lines)
            {
                ImGui.TextWrapped(line);
            }
        }

        ImGui.EndChild();
    }

    private static string CounterLine(string label, int count, string time)
        => count == 0 || string.IsNullOrEmpty(time)
            ? $"{label}：—"
            : $"{label}：{count} 次 · 最近 {time}";

    /// <summary>把若干小按钮右对齐（ImGui 的 SameLine 参数是「距行首的绝对偏移」）。</summary>
    private float RightAlignedOffsetFor(params string[] labels)
    {
        var width = 0f;
        foreach (var label in labels)
        {
            width += ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2f);
        }

        width += ImGui.GetStyle().ItemSpacing.X * (labels.Length - 1);
        return ImGui.GetContentRegionMax().X - width;
    }
}
