using Dalamud.Bindings.ImGui;
using FireGaze.Diagnostics;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>
///     「简介汉化」页。
/// </summary>
internal sealed class TranslateTab
{
    private readonly Plugin plugin;

    private string? updateMessage;
    private string? statusMessage;
    private volatile bool updateInFlight;

    // FastDalamudCN 也在翻译插件简介，两者会打架——检测到就在页面上挂一行提示（用户 2026-10-06 定）
    private DateTime fastDalamudCheckedAt = DateTime.MinValue;
    private bool fastDalamudInstalled;
    private InstalledPluginEntry? fastDalamudEntry;
    private string? fastDalamudOpenMessage;
    private Task<InstalledPluginEntry?>? fastDalamudCheckTask;

    public TranslateTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    /// <summary>
    ///     检测本机装没装 FastDalamudCN（内部名 FuckDalamudCN）：装了就在页面上挂冲突提示。
    ///     索引在后台线程建（反射卫月内部，别卡绘制），结果缓存 5 分钟。
    /// </summary>
    private void EnsureFastDalamudHint()
    {
        if (fastDalamudCheckTask is not null)
        {
            if (!fastDalamudCheckTask.IsCompleted)
            {
                return;
            }

            var finished = fastDalamudCheckTask;
            fastDalamudCheckTask = null;
            if (finished.Status == TaskStatus.RanToCompletion)
            {
                fastDalamudEntry = finished.Result;
                fastDalamudInstalled = fastDalamudEntry is not null;
            }

            fastDalamudCheckedAt = DateTime.UtcNow;
            return;
        }

        if (DateTime.UtcNow - fastDalamudCheckedAt < TimeSpan.FromMinutes(5))
        {
            return;
        }

        fastDalamudCheckedAt = DateTime.UtcNow;
        fastDalamudCheckTask = Task.Run(() =>
        {
            try
            {
                var index = InstalledPluginsIndex.Build();
                return index.Available
                    ? index.All.FirstOrDefault(x => string.Equals(x.InternalName, "FuckDalamudCN", StringComparison.Ordinal))
                    : null;
            }
            catch
            {
                return null;
            }
        });
    }

    public void Draw()
    {
        var config = plugin.Config;

        EnsureFastDalamudHint();

        // ---------------- 顶部：更新词表（最显眼） ----------------
        if (updateInFlight)
        {
            ImGui.BeginDisabled();
            ImGui.Button("从 GitHub 更新词表###UpdateTable");
            ImGui.EndDisabled();
        }
        else if (ImGui.Button("从 GitHub 更新词表###UpdateTable"))
        {
            updateInFlight = true;
            updateMessage = "正在更新…";
            _ = Task.Run(async () =>
            {
                try
                {
                    var (ok, message) = await plugin.UpdateTranslationTableAsync().ConfigureAwait(false);
                    updateMessage = message;
                    if (ok)
                    {
                        ActivityLog.Info("简介汉化", "更新词表：" + message);
                    }
                    else
                    {
                        ActivityLog.Warning("简介汉化", "更新词表失败：" + message);
                    }
                }
                catch (Exception e)
                {
                    // 原来没有 catch：一抛异常 updateInFlight 永远为 true，按钮就永远禁用了
                    updateMessage = "更新出错：" + e.GetType().Name;
                    ActivityLog.Warning("简介汉化", "更新词表异常：" + e.GetType().Name);
                }
                finally
                {
                    updateInFlight = false;
                }
            });
        }

        ImGui.SameLine();
        ImGui.TextDisabled(updateInFlight ? "正在更新…" : updateMessage ?? string.Empty);

        ImGui.Separator();

        // FastDalamudCN 冲突提示：常驻灰字（冲突是持续存在的，不该只提醒一次），并把它的设置窗口直接开出来
        if (fastDalamudInstalled)
        {
            UiHelpers.ColoredWrapped(
                UiHelpers.Muted,
                "检测到 FastDalamudCN：它也在翻译插件简介，而且译文会更优先显示。建议在 Fast 里关掉简介翻译，或关掉本插件的简介汉化。");

            if (ImGui.SmallButton("打开 FastDalamudCN 设置###OpenFastDalamudCN"))
            {
                fastDalamudOpenMessage = TryOpenFastDalamudConfig(fastDalamudEntry);
            }

            if (!string.IsNullOrEmpty(fastDalamudOpenMessage))
            {
                UiHelpers.ColoredWrapped(UiHelpers.Muted, fastDalamudOpenMessage);
            }
        }

        // ---------------- 开关 ----------------
        var enabled = config.TranslateEnabled;
        if (ImGui.Checkbox("启用简介汉化###TranslateEnabled", ref enabled))
        {
            config.TranslateEnabled = enabled;
            plugin.SaveConfig();
            if (enabled)
            {
                var applied = plugin.ApplyTranslations();
                statusMessage = $"已启用：本次改写 {applied} 条清单";
                ActivityLog.Info("简介汉化", $"已启用，改写 {applied} 条清单");
            }
            else
            {
                var restored = plugin.RestoreTranslations();
                statusMessage = $"已关闭：已把 {restored} 条还原成原文";
                ActivityLog.Info("简介汉化", $"已关闭，还原 {restored} 条");
            }
        }

        var autoUpdate = config.AutoUpdateTable;
        if (ImGui.Checkbox("每两周自动检查词表更新###AutoUpdateTable", ref autoUpdate))
        {
            config.AutoUpdateTable = autoUpdate;
            plugin.SaveConfig();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"下次自动检查：{DescribeNextCheck()}");

        if (!string.IsNullOrEmpty(statusMessage))
        {
            ImGui.TextWrapped(statusMessage);
        }

        // ---------------- 词表状态 ----------------
        ImGui.Text($"词表：{plugin.Table.Count} 条 · 上次应用改写 {plugin.LastTranslatedCount} 条清单");
        ImGui.SameLine();
        ImGui.TextDisabled("· " + DescribeTableUpdate());
        if (plugin.Table.LoadedFrom is not null)
        {
            ImGui.TextDisabled($"来源文件：{plugin.Table.LoadedFrom}");
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

            ModeRow("插件名", "NameMode", config.NameMode, m => config.NameMode = m);
            ModeRow("一行简介", "PunchlineMode", config.PunchlineMode, m => config.PunchlineMode = m);
            ModeRow("插件详情", "DescriptionMode", config.DescriptionMode, m => config.DescriptionMode = m);

            ImGui.EndTable();
        }

        ImGui.Spacing();
        ImGui.TextDisabled(
            "上游更新了简介后会跳过该段的翻译；等下一次词表更新后再生效。");

    }

    /// <summary>
    ///     「词表维护」那一行：优先用词表自带的维护日期（工作流跑的那天），其次本机更新时间，最后随插件版本的日期。
    /// </summary>
    private string DescribeTableUpdate()
    {
        // ① 词表文件里自带的 _meta.updatedAt —— 这就是「GitHub 上那份词表是哪天维护的」
        if (DateTime.TryParse(plugin.Table.MaintainedAt, out var maintained))
        {
            return $"词表维护：{maintained:yyyy-MM-dd} {WeekdayLabel(maintained.DayOfWeek)}";
        }

        // ② 老词表没有维护日期：只本机取用过的时间，不能冒充「维护日期」
        if (plugin.Config.LastTableUpdateLocal != default)
        {
            return $"词表维护：未标注，本机 {plugin.Config.LastTableUpdateLocal:MM-dd} 取到";
        }

        // ③ 随插件装上的那份（同样是未标注）
        var loaded = plugin.Table.LoadedAt ?? default;
        return loaded == default
            ? "词表维护：未标注"
            : $"词表维护：未标注，随插件版本 {loaded:yyyy-MM-dd}";
    }

    /// <summary>
    ///     打开 FastDalamudCN 的设置窗口。与插件安装器的齿轮按钮同一条路：
    ///     <c>plugin.DalamudInterface.LocalUiBuilder.OpenConfig()</c>（都是 internal，只能反射）。
    /// </summary>
    private static string TryOpenFastDalamudConfig(InstalledPluginEntry? entry)
    {
        if (entry?.RawPlugin is null)
        {
            return "没找到 FastDalamudCN 实例（可能已被停用）。";
        }

        if (!entry.IsLoaded)
        {
            return "FastDalamudCN 没在运行；先在插件安装器里启用它。";
        }

        try
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

            var dalamudInterface = entry.RawPlugin.GetType().GetProperty("DalamudInterface", flags)?.GetValue(entry.RawPlugin);
            var uiBuilder = dalamudInterface?.GetType().GetProperty("LocalUiBuilder", flags)?.GetValue(dalamudInterface);
            var open = uiBuilder?.GetType().GetMethod("OpenConfig", flags);
            if (open is null)
            {
                return "打不开：卫月没有这个入口（版本变化？）。";
            }

            open.Invoke(uiBuilder, null);
            ActivityLog.Info("简介汉化", "已从本页打开 FastDalamudCN 的设置窗口");
            return "已打开 FastDalamudCN 的设置窗口。";
        }
        catch (Exception e)
        {
            ActivityLog.Warning("简介汉化", "打开 FastDalamudCN 设置失败：" + e.GetType().Name);
            return "打开失败：" + e.GetType().Name;
        }
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
        var config = plugin.Config;
        if (!config.TranslateEnabled || !config.AutoUpdateTable)
        {
            return "未启用";
        }

        if (config.LastTableUpdateCheckUTC == default)
        {
            return "游戏启动后";
        }

        var next = config.LastTableUpdateCheckUTC.AddDays(14);
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
            plugin.SaveConfig();
            plugin.ApplyTranslations();
        }

        ImGui.TableNextColumn();
        if (ImGui.RadioButton($"中文###{id}-Chinese", mode == DisplayMode.Chinese))
        {
            set(DisplayMode.Chinese);
            plugin.SaveConfig();
            plugin.ApplyTranslations();
        }

        ImGui.TableNextColumn();
        if (ImGui.RadioButton($"双语###{id}-Both", mode == DisplayMode.Both))
        {
            set(DisplayMode.Both);
            plugin.SaveConfig();
            plugin.ApplyTranslations();
        }
    }
}
