using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FireGaze.UI;

/// <summary>
///     主窗口的页签（顺序就是界面上的顺序：插件汉化 → 插件发现 → 仓库体检 → 简介汉化 → 插件安装器）。
/// </summary>
public enum MainTab
{
    Translate = 0,
    RepoAudit = 1,
    Installer = 2,
    Discovery = 3,
    UIText = 4,
}

/// <summary>
///     FireGaze 主窗口（/fg）。
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly RepoAuditTab repoAuditTab;
    private readonly InstallerTab installerTab;
    private readonly TranslateTab translateTab;
    private readonly UITextTab uiTextTab;
    private readonly DiscoveryTab discoveryTab;

    private MainTab? pendingSelect;

    public MainWindow(Plugin plugin, UITextTab uiTextTab)
        : base("FireGaze###FireGaze", ImGuiWindowFlags.None)
    {
        Size = new Vector2(700, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 440),
        };

        this.uiTextTab = uiTextTab;
        repoAuditTab = new RepoAuditTab(plugin);
        installerTab = new InstallerTab(plugin);
        translateTab = new TranslateTab(plugin);
        discoveryTab = new DiscoveryTab(plugin);
    }

    /// <summary>
    ///     请求下一帧选中某个页签（由 Plugin.OpenWindow 调用）。
    /// </summary>
    public void SelectTab(MainTab tab) => pendingSelect = tab;

    /// <summary>插件卸载时的退订链（转给需要退订的子页签）。</summary>
    public void Detach() => this.repoAuditTab.Detach();

    /// <summary>
    ///     窗口每次打开都落在「插件汉化」页（ImGui 会记住上次的页签，这里显式改回）。
    /// </summary>
    /// <remarks>2026-10-01 用户改：默认页从「简介汉化」改成「插件汉化」（它也是第一个页签）。</remarks>
    public override void OnOpen()
    {
        pendingSelect = MainTab.UIText;
        Plugin.Log.Debug("[FireGaze] 窗口已打开（页签回到「插件汉化」）");
    }

    /// <summary>
    ///     关窗也记一笔：掉帧/崩溃排查时能看出当时在看哪一页。
    /// </summary>
    public override void OnClose() => Plugin.Log.Debug("[FireGaze] 窗口已关闭");

    public override void Draw()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Accent);
        ImGui.TextUnformatted("FireGaze");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        // 带上版本号：开发版热重载频繁，出问题时一眼就能分辨屏幕上的窗口是不是旧实例的幽灵
        ImGui.TextDisabled($"卫月插件库工具箱 · 汉化 / 体检 / 发现 · v{typeof(MainWindow).Assembly.GetName().Version}");

        ImGui.Separator();

        if (ImGui.BeginTabBar("###FireGazeTabs"))
        {
            var flags = pendingSelect == MainTab.UIText ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("插件汉化", flags))
            {
                this.uiTextTab.Draw();
                ImGui.EndTabItem();
            }

            flags = pendingSelect == MainTab.Discovery ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("插件发现", flags))
            {
                discoveryTab.Draw();
                ImGui.EndTabItem();
            }

            flags = pendingSelect == MainTab.RepoAudit ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("仓库体检", flags))
            {
                repoAuditTab.Draw();
                ImGui.EndTabItem();
            }

            flags = pendingSelect == MainTab.Translate ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("简介汉化", flags))
            {
                translateTab.Draw();
                ImGui.EndTabItem();
            }

            flags = pendingSelect == MainTab.Installer ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("插件安装器", flags))
            {
                installerTab.Draw();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        pendingSelect = null;
    }
}
