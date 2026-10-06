using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FireGaze.UI;

/// <summary>
///     公共彩云翻译额度的一次性公告（用户 2026-10-07 定）。
/// </summary>
/// <remarks>
///     第一次打开 FireGaze 主窗口时弹一次；点「知道了」当场写配置、以后不再弹。
///     用户可能用 DailyRoutines 的「即刻登出」，所以确认必须立即落盘，不能等游戏正常退出——
///     否则配置没保存，下次打开又会弹一遍。
/// </remarks>
internal sealed class QuotaNoticeWindow : Window
{
    private readonly Plugin plugin;

    public QuotaNoticeWindow(Plugin plugin)
        : base("关于公共彩云的翻译额度###FireGazeQuotaNotice", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
        this.SizeCondition = ImGuiCond.Appearing;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(540, 0),
            MaximumSize = new Vector2(760, float.MaxValue),
        };
    }

    public override void OnOpen()
    {
        // 折叠状态会被 ImGui 的 ini 记住；这是要人看的公告，每次打开都展开
        ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
    }

    public override void Draw()
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 560f);

        ImGui.TextWrapped("公共彩云的翻译额度由作者提供，到目前一共提供了 200 万字符；仅 10 月 6 日一天就用掉 120 万字符。");
        ImGui.Spacing();
        ImGui.TextWrapped("新版本会最后再提供 100 万字符供大家使用，之后就不再提供了。");
        ImGui.Spacing();
        ImGui.TextWrapped("有条件的朋友建议改用大模型翻译，效果更好；想用免费的也可以自己注册彩云、在「翻译设置…」里填自己的 key。");
        ImGui.Spacing();
        ImGui.TextWrapped("如果你用不完自己的彩云免费额度，又愿意把服务提供给大家用，可以到 DailyRoutines 的 Discord「插件讨论」的 FireGaze 里私聊我 API key，或直接通过「反馈」提交。");
        ImGui.Spacing();
        ImGui.TextWrapped("狒人就这样互相照顾彼此。");

        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.Button("确定###QuotaNoticeOk", new Vector2(120, 0)))
        {
            plugin.ConfirmTranslateQuotaNotice();
            IsOpen = false;
        }
    }
}
