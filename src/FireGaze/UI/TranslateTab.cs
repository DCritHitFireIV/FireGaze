using Dalamud.Bindings.ImGui;

namespace FireGaze.UI;

/// <summary>「简介汉化」页。</summary>
internal sealed class TranslateTab
{
    private readonly Plugin plugin;

    private string? updateMessage;
    private string? statusMessage;
    private volatile bool updateInFlight;
    private bool noticeReview;

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
                this.noticeReview = true;
            });
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("参与翻译…###OpenContribute"))
        {
            this.plugin.OpenContributeWindow();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("打开「参与翻译」：搜插件、改译文、攒够了一批直接提交给维护者审核。");
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
            this.statusMessage = enabled
                ? $"已启用：本次改写 {this.plugin.ApplyTranslations()} 条清单"
                : $"已关闭：已把 {this.plugin.RestoreTranslations()} 条还原成原文";
        }

        var autoUpdate = config.AutoUpdateTable;
        if (ImGui.Checkbox("每两周自动检查词表更新###AutoUpdateTable", ref autoUpdate))
        {
            config.AutoUpdateTable = autoUpdate;
            this.plugin.SaveConfig();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"下次自动检查：{this.DescribeNextCheck()}");

        if (!string.IsNullOrEmpty(this.statusMessage))
        {
            ImGui.TextWrapped(this.statusMessage);
        }

        // ---------------- 词表状态 ----------------
        ImGui.Text($"词表：{this.plugin.Table.Count} 条 · 上次应用改写 {this.plugin.LastTranslatedCount} 条清单");
        ImGui.SameLine();
        ImGui.TextDisabled("· " + this.DescribeTableUpdate());
        if (this.plugin.Table.LoadedFrom is not null)
        {
            var when = this.plugin.Table.LoadedAt is { } time ? time.ToString("yyyy-MM-dd HH:mm") : "?";
            ImGui.TextDisabled($"来源文件：{this.plugin.Table.LoadedFrom}（本地改动时间 {when}）");
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
            "上游更新了简介后会跳过该段的翻译，词表维护后重新更新即可。");
        ImGui.TextDisabled("提示：主库插件的简介汉化请使用 FastDalamudCN（本插件的词表只覆盖第三方插件库）。");

        // ---------------- 复核提醒（不再从这里进「参与翻译」，那个是独立页签） ----------------
        if (this.noticeReview)
        {
            this.noticeReview = false;
            if (this.plugin.HasReviewPending())
            {
                UiHelpers.ColoredWrapped(
                    UiHelpers.Warn,
                    "有新词表：部分条目的原文改过了，旧译文可能对不上，可以到「参与翻译」里筛「待复核」看一眼。");
            }
        }
    }

    /// <summary>「词表维护」那一行：优先用词表自带的维护日期（工作流跑的那天），其次本机更新时间，最后随插件版本的日期。</summary>
    private string DescribeTableUpdate()
    {
        // ① 词表文件里自带的 _meta.updatedAt —— 这就是「GitHub 上那份词表是哪天维护的」
        if (DateTime.TryParse(this.plugin.Table.MaintainedAt, out var maintained))
        {
            return $"词表维护：{maintained:yyyy-MM-dd}（{WeekdayLabel(maintained.DayOfWeek)}）";
        }

        // ② 老词表没有维护日期：只本机取用过的时间，不能冒充「维护日期」
        if (this.plugin.Config.LastTableUpdateUtc != default)
        {
            return $"词表维护：未标注（本机 {this.plugin.Config.LastTableUpdateUtc:MM-dd} 取到）";
        }

        // ③ 随插件装上的那份（同样是未标注）
        var loaded = this.plugin.Table.LoadedAt ?? default;
        return loaded == default
            ? "词表维护：未标注"
            : $"词表维护：未标注（随插件版本 {loaded:yyyy-MM-dd}）";
    }

    private static string WeekdayLabel(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

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
