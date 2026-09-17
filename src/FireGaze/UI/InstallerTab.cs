using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>「插件安装器」页：记住列表浏览位置。</summary>
internal sealed class InstallerTab
{
    private readonly Plugin plugin;

    public InstallerTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        ImGui.TextWrapped("记住插件安装器列表的浏览位置，下次打开接着看。");
        ImGui.TextDisabled("实现方式：只读/写 ImGui 里那个列表子窗口的滚动值（不使用任何钩子）。");

        ImGui.Separator();

        var remember = this.plugin.Config.RememberListScroll;
        if (ImGui.Checkbox("记住看到哪里（下次打开接着看）###RememberScroll", ref remember))
        {
            this.plugin.SetRememberListScroll(remember);
        }

        ImGui.Separator();
        ImGui.TextWrapped("实验 / 诊断（用来定位那个列表子窗口，以及「重载把列表顶掉」的问题）");

        var probe = this.plugin.InstallerProbe;
        ImGui.BulletText($"结构体自检：{probe.CtxVerdict}");
        ImGui.BulletText($"列表子窗口：{probe.ListVerdict}");
        ImGui.BulletText($"最近读到滚动值：{probe.LastScrollY:F0}");
        ImGui.BulletText($"防刷新顶飞：{probe.MaskVerdict}");
        ImGui.BulletText($"窗口清单 dump：{probe.DumpPath}");

        if (ImGui.Button("测试：触发一次仓库重载###ProbeReload"))
        {
            Plugin.Chat?.Print(probe.TriggerRepoReload()
                ? "[FireGaze] 已触发一次仓库重载（看着安装器列表，看它会不会被顶回顶部）"
                : "[FireGaze] 触发失败，看 dalamud.log");
        }
    }
}
