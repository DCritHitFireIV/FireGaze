using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FireGaze.UI;

/// <summary>
///     公共彩云翻译额度的一次性公告（用户 2026-10-07 定）。
/// </summary>
/// <remarks>
///     只给「全新安装」的用户第一次打开主窗口时弹一次；升级上来的用户不看（配置里没有 QuotaNoticeForNewUser）。
///     点「确定」当场写配置、以后不再弹。
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

    public override void PreDraw()
    {
        var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        FireGazeWindowPlacement.Prepare("FireGazeQuotaNotice", new Vector2(600, 300) * scale, scale, "FireGaze");
        var maximum = ImGui.GetMainViewport().WorkSize / scale;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = Vector2.Min(new(540, 0), maximum), MaximumSize = Vector2.Min(new(760, float.MaxValue), maximum) };
    }

    public override void Draw()
    {
        FireGazeWindowPlacement.Remember("FireGazeQuotaNotice");
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 560f);

        ImGui.TextWrapped("公共彩云使用共享免费额度。你也可以在「翻译设置…」中选择其他通道，填写自己申请的 key。");
        ImGui.Spacing();
        ImGui.TextWrapped("公共额度不可用时会自动切换免费通道。自备 key 的额度与费用以服务商规则为准。");
        ImGui.Spacing();
        ImGui.TextWrapped("想贡献公共彩云可以到dailyroutine的dc，插件反馈里私聊作者。");

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
