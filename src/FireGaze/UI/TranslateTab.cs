using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>「简介汉化」页。</summary>
internal sealed class TranslateTab
{
    private readonly Plugin plugin;

    private string? updateMessage;
    private volatile bool updateInFlight;

    public TranslateTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        var config = this.plugin.Config;

        // ---------------- 顶部：更新词表（最显眼） ----------------
        if (this.updateInFlight)
        {
            ImGui.BeginDisabled();
            ImGui.Button("从 GitHub 更新词表###UpdateTable");
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("从 GitHub 更新词表###UpdateTable"))
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

        ImGui.SameLine();
        if (ImGui.Button("重新应用###ApplyNow"))
        {
            this.plugin.ApplyTranslations();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(this.updateInFlight ? "正在更新…" : this.updateMessage ?? string.Empty);

        ImGui.Separator();

        // ---------------- 开关 ----------------
        var enabled = config.TranslateEnabled;
        if (ImGui.Checkbox("启用简介汉化###TranslateEnabled", ref enabled))
        {
            config.TranslateEnabled = enabled;
            this.plugin.SaveConfig();
            this.plugin.ApplyTranslations();
        }

        var autoUpdate = config.AutoUpdateTable;
        if (ImGui.Checkbox("每两周自动检查词表更新###AutoUpdateTable", ref autoUpdate))
        {
            config.AutoUpdateTable = autoUpdate;
            this.plugin.SaveConfig();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"下次自动检查：{this.DescribeNextCheck()}");

        // ---------------- 词表状态 ----------------
        ImGui.Text($"词表：{this.plugin.Table.Count} 条 · 上次应用改写 {this.plugin.LastTranslatedCount} 条清单");
        if (this.plugin.Table.LoadedFrom is not null)
        {
            var when = this.plugin.Table.LoadedAt is { } time ? time.ToString("yyyy-MM-dd HH:mm") : "?";
            ImGui.TextDisabled($"来源：{this.plugin.Table.LoadedFrom}（{when}）");
        }
        else
        {
            ImGui.TextDisabled("词表未加载");
        }

        ImGui.Separator();

        // ---------------- 三个字段的呈现方式 ----------------
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

        ImGui.Spacing();
        ImGui.TextDisabled(
            "词表仅在你看到的原文与我们收录的一致时才替换；上游改了简介或卫月改了字段名时自动跳过，重新更新词表即可。");
    }

    private string DescribeNextCheck()
    {
        var config = this.plugin.Config;
        if (!config.TranslateEnabled || !config.AutoUpdateTable)
        {
            return "未启用";
        }

        if (config.LastTableUpdateCheckUtc == default)
        {
            return "游戏启动后";
        }

        var next = config.LastTableUpdateCheckUtc.AddDays(14);
        return next <= DateTime.UtcNow ? "即将检查" : next.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

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
