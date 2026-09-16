using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>「简介汉化」页。</summary>
internal sealed class TranslateTab
{
    private readonly Plugin plugin;

    public TranslateTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        var config = this.plugin.Config;

        ImGui.TextWrapped(
            "用内置词表把插件安装器里的简介显示成中文。三个字段可以各自选择展现形式：" +
            "名字默认「英语原版」，一行简介默认「中文」，插件详情默认「双语（中文 + 空行 + 原文）」。");

        ImGui.Separator();

        var enabled = config.TranslateEnabled;
        if (ImGui.Checkbox("启用简介汉化###TranslateEnabled", ref enabled))
        {
            config.TranslateEnabled = enabled;
            this.plugin.SaveConfig();
            this.plugin.ApplyTranslations();
        }

        ImGui.Spacing();
        if (ImGui.BeginTable("###modeTable", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("字段", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("原版", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("中文", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("双语", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();

            this.ModeRow("插件名", "NameMode", config.NameMode, m => config.NameMode = m);
            this.ModeRow("一行简介", "PunchlineMode", config.PunchlineMode, m => config.PunchlineMode = m);
            this.ModeRow("插件详情", "DescriptionMode", config.DescriptionMode, m => config.DescriptionMode = m);

            ImGui.EndTable();
        }

        ImGui.TextDisabled("双语格式：名字为「中文 (English)」；简介 / 详情为「中文 + 空行 + 原文」。名字的译文取决于词表，没有译名时自动回退原文。");

        ImGui.Separator();

        ImGui.Text($"词表：{this.plugin.Table.Count} 条");
        ImGui.SameLine();
        ImGui.TextDisabled(this.plugin.Table.LoadedFrom is null ? "（未加载）" : $"来源：{this.plugin.Table.LoadedFrom}");
        if (this.plugin.Table.LoadedAt is { } loadedAt)
        {
            ImGui.TextDisabled($"词表时间：{loadedAt:yyyy-MM-dd HH:mm}");
        }

        ImGui.Text($"上次应用：改写 {this.plugin.LastTranslatedCount} 条清单");

        if (ImGui.Button("重新应用###ApplyNow"))
        {
            var count = this.plugin.ApplyTranslations();
            this.plugin.OpenWindow(MainTab.Translate);
            _ = count;
        }

        ImGui.SameLine();
        if (ImGui.Button("从 GitHub 更新词表###UpdateTable"))
        {
            this.updateInFlight = true;
            this.updateMessage = "正在更新…";
            _ = Task.Run(async () =>
            {
                var (_, message) = await this.plugin.UpdateTranslationTableAsync().ConfigureAwait(false);
                this.updateMessage = message;
                this.updateInFlight = false;
            });
        }

        if (this.updateInFlight)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(this.updateMessage ?? "正在更新…");
        }
        else if (!string.IsNullOrEmpty(this.updateMessage))
        {
            ImGui.TextWrapped(this.updateMessage);
        }

        ImGui.Spacing();
        ImGui.TextDisabled(
            "词表按 InternalName 匹配，仅在你看到的原文与我们收录的一致时才替换；" +
            "上游改了简介或卫月改了字段名时，这一条会自动跳过，重新更新词表即可。");
    }

    private string? updateMessage;
    private volatile bool updateInFlight;

    private void ModeRow(string label, string id, DisplayMode current, Action<DisplayMode> set)
    {
        var mode = current;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);

        ImGui.TableNextColumn();
        if (ImGui.RadioButton($"原版###{id}-Original", mode == DisplayMode.Original))
        {
            set(DisplayMode.Original);
            this.plugin.SaveConfig();
            this.plugin.ApplyTranslations();
        }

        ImGui.TableNextColumn();
        if (ImGui.RadioButton($"中文###{id}-Chinese", mode == DisplayMode.Chinese))
        {
            set(DisplayMode.Chinese);
            this.plugin.SaveConfig();
            this.plugin.ApplyTranslations();
        }

        ImGui.TableNextColumn();
        if (ImGui.RadioButton($"双语###{id}-Both", mode == DisplayMode.Both))
        {
            set(DisplayMode.Both);
            this.plugin.SaveConfig();
            this.plugin.ApplyTranslations();
        }
    }
}
