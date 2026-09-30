using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;
using FireGaze.UIText;

namespace FireGaze.UI;

/// <summary>
///     「插件汉化」页签：列出本机插件，进各自的编辑器逐条翻译插件界面上的文本。
/// </summary>
internal sealed class UITextTab
{
    private readonly UITextEditorWindow editor;
    private readonly UITextStore store;
    private readonly UITextPatchManager patches;

    private InstalledPluginsIndex? index;
    private DateTime indexAt = DateTime.MinValue;
    private string search = string.Empty;
    private bool onlyThirdParty;

    // 包摘要只在刷新时读一次盘（每帧逐行读文件会拖死界面）
    private Dictionary<string, (int Total, int Translated, int Skipped)> packInfo = new(StringComparer.Ordinal);
    private DateTime packInfoAt = DateTime.MinValue;

    public UITextTab(UITextEditorWindow editor, UITextStore store, UITextPatchManager patches)
    {
        this.editor = editor;
        this.store = store;
        this.patches = patches;
    }

    public void Draw()
    {
        ImGui.TextWrapped(
            "把第三方插件界面上的英文翻成中文：先抽取（只认真的画在界面上的文本），在编辑器里逐条翻译或校对，再用配套包应用。");
        ImGui.TextDisabled(
            "抽取是纯静态读 IL，不加载插件、不执行任何代码；命令名、日志、配置键这类功能字符串会被自动排除。");

        this.DrawToolbar();
        this.DrawTable();
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("刷新列表") || this.index is null)
        {
            this.index = InstalledPluginsIndex.Build();
            this.indexAt = DateTime.Now;
            this.RefreshPackInfo();
        }
        else if ((DateTime.Now - this.packInfoAt).TotalSeconds > 5)
        {
            // 编辑器里改完回来，最多 5 秒后能看到新的条数
            this.RefreshPackInfo();
        }

        ImGui.SameLine();
        ImGui.Checkbox("只看第三方插件", ref this.onlyThirdParty);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("官方插件（Dip17）默认也列出：它们一样是英文界面，只是更新由官方安装器负责，补丁同样会被覆盖、需要重打。");
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("###UITextPluginSearch", "搜索插件名 / 内部名…", ref this.search, 128);

        if (this.index is null)
        {
            ImGui.TextDisabled("正在读取插件列表…");
            return;
        }

        if (!this.index.Available)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Bad, "读不到卫月的插件列表（" + (this.index.FailureReason ?? "未知原因") + "）");
            return;
        }

        ImGui.SameLine();
        var official = this.index.All.Count(e => !e.IsThirdParty);
        ImGui.TextDisabled($"已装 {this.index.All.Count} 个（官方 {official} · 第三方 {this.index.All.Count - official}）· {this.indexAt:HH:mm:ss} 读取");
    }

    private void DrawTable()
    {
        if (this.index is null || !this.index.Available)
        {
            return;
        }

        var filter = this.search.Trim();
        var items = this.index.All
            .Where(e => !this.onlyThirdParty || e.IsThirdParty)
            .Where(e => filter.Length == 0
                        || e.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || e.InternalName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (items.Count == 0)
        {
            ImGui.TextDisabled(this.onlyThirdParty && this.index.All.Any(e => !e.IsThirdParty)
                ? "当前筛选下没有插件——取消「只看第三方插件」就能看到官方插件。"
                : "当前筛选下没有插件。");
        }

        ImGui.TextDisabled($"列出 {items.Count} / {this.index.All.Count} 个");

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                                      ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("###UITextPlugins", 5, flags, new System.Numerics.Vector2(0, -1)))
        {
            return;
        }

        ImGui.TableSetupColumn("插件", ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn("内部名", ImGuiTableColumnFlags.WidthStretch, 0.25f);
        ImGui.TableSetupColumn("本地包", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("补丁", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var plugin in items)
        {
            ImGui.PushID(plugin.InternalName);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(plugin.DisplayName);
            if (!plugin.IsThirdParty)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("[官方]");
            }

            if (plugin.IsDev && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("开发版插件（补丁会被重新构建覆盖，需要重打）");
            }

            ImGui.TableNextColumn();
            ImGui.TextDisabled(plugin.InternalName);

            ImGui.TableNextColumn();
            var hasPack = this.packInfo.TryGetValue(plugin.InternalName, out var info);
            if (!hasPack)
            {
                ImGui.TextDisabled("—");
            }
            else
            {
                ImGui.TextUnformatted($"{info.Translated} / {info.Total} 条");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"本地包：{info.Total} 条原文，{info.Translated} 条已翻" +
                                     (info.Skipped > 0 ? $"，{info.Skipped} 条标记不翻" : string.Empty));
                }
            }

            ImGui.TableNextColumn();
            var patchStatus = this.patches.StatusOf(plugin, out var patchDetail);
            if (patchStatus == UITextPatchStatus.NotPatched)
            {
                ImGui.TextDisabled("—");
            }
            else
            {
                var color = patchStatus switch
                {
                    UITextPatchStatus.Applied => UiHelpers.Good,
                    UITextPatchStatus.PendingReload => UiHelpers.Info,
                    UITextPatchStatus.NeedsRepatch => UiHelpers.Warn,
                    _ => UiHelpers.Bad,
                };
                UiHelpers.ColoredText(color, PatchLabel(patchStatus));
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(patchDetail);
                }
            }

            ImGui.TableNextColumn();
            var canEdit = !string.IsNullOrEmpty(plugin.DLLPath) && File.Exists(plugin.DLLPath);
            ImGui.BeginDisabled(!canEdit);
            if (ImGui.Button(hasPack ? "打开编辑器" : "开始汉化"))
            {
                this.editor.OpenFor(plugin);
            }

            ImGui.EndDisabled();
            if (!canEdit && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("读不到这个插件的主程序集路径");
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static string PatchLabel(UITextPatchStatus status) => status switch
    {
        UITextPatchStatus.Applied => "已打",
        UITextPatchStatus.PendingReload => "待重载",
        UITextPatchStatus.NeedsRepatch => "需重打",
        UITextPatchStatus.Failed => "失败",
        _ => "—",
    };

    /// <summary>
    ///     读一遍本地包摘要（只在刷新列表 / 页面停留超过 5 秒时做）。
    /// </summary>
    private void RefreshPackInfo()
    {
        var info = new Dictionary<string, (int Total, int Translated, int Skipped)>(StringComparer.Ordinal);
        foreach (var name in this.store.List())
        {
            var pack = this.store.Load(name);
            info[name] = (pack.Entries.Count, pack.TranslatedCount, pack.Skipped.Count);
        }

        this.packInfo = info;
        this.packInfoAt = DateTime.Now;
    }
}
