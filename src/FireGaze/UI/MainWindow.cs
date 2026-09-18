using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FireGaze.UI;

/// <summary>主窗口的页签。</summary>
public enum MainTab
{
    Translate = 0,
    Contribute = 1,
    RepoAudit = 2,
    Installer = 3,
}

/// <summary>FireGaze 主窗口（/fg）。</summary>
internal sealed class MainWindow : Window
{
    private readonly RepoAuditTab repoAuditTab;
    private readonly InstallerTab installerTab;
    private readonly TranslateTab translateTab;
    private readonly ContributeWindow contributeTab;

    private MainTab? pendingSelect;

    public MainWindow(Plugin plugin)
        : base("FireGaze###FireGaze", ImGuiWindowFlags.None)
    {
        this.Size = new Vector2(700, 620);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 440),
        };

        this.repoAuditTab = new RepoAuditTab(plugin);
        this.installerTab = new InstallerTab(plugin);
        this.translateTab = new TranslateTab(plugin);
        this.contributeTab = new ContributeWindow(plugin, plugin.Contributions);
    }

    /// <summary>请求下一帧选中某个页签（由 Plugin.OpenWindow 调用）。</summary>
    public void SelectTab(MainTab tab) => this.pendingSelect = tab;

    /// <summary>窗口每次打开都落在「简介汉化」页（ImGui 会记住上次的页签，这里显式改回）。</summary>
    public override void OnOpen() => this.pendingSelect = MainTab.Translate;

    public override void Draw()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Accent);
        ImGui.TextUnformatted("FireGaze");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled("卫月插件库工具箱 · 汉化 / 体检 / 拦住自动刷新");

        ImGui.Separator();

        if (ImGui.BeginTabBar("###FireGazeTabs"))
        {
            var flags = this.pendingSelect == MainTab.Translate ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("简介汉化", flags))
            {
                this.translateTab.Draw();
                ImGui.EndTabItem();
            }

            flags = this.pendingSelect == MainTab.Contribute ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("参与翻译", flags))
            {
                this.contributeTab.Draw();
                ImGui.EndTabItem();
            }

            flags = this.pendingSelect == MainTab.RepoAudit ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("仓库体检", flags))
            {
                this.repoAuditTab.Draw();
                ImGui.EndTabItem();
            }

            flags = this.pendingSelect == MainTab.Installer ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem("插件安装器", flags))
            {
                this.installerTab.Draw();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        this.pendingSelect = null;
    }
}
