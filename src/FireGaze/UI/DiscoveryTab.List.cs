using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FireGaze.Discovery;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

internal sealed partial class DiscoveryTab
{
    /// <summary>
    ///     筛选行 + 几个显示开关。
    /// </summary>
    private void DrawFilterRow()
    {
        FilterRadio(StateFilter.All, "全部");
        FilterRadio(StateFilter.Missing, "缺译文");
        FilterRadio(StateFilter.Machine, "机器译");
        FilterRadio(StateFilter.User, "我提交过");
        FilterRadio(StateFilter.Review, "待复核");

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "缺译文 = 上游给了内容、但还没有译文。\n"
                + "上游本来就空着的字段不算缺译，但你可以自己补一份。\n"
                + "上游原文本身就是中文的也不算缺译（那是国服/汉化分支的简介）。\n"
                + "待复核 = 上游原文改过、译文还没跟上。");
        }

        ImGui.SameLine();
        var showIcons = plugin.Config.ShowIconsInContribute;
        if (ImGui.Checkbox("显示图标###ContributeIcons", ref showIcons))
        {
            plugin.Config.ShowIconsInContribute = showIcons;
            plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("只显示本地已经有缓存的图标，不会为此下载任何东西。");
        }

        ImGui.SameLine();
        var showDisabled = plugin.Config.ContributeShowDisabled;
        if (ImGui.Checkbox("连着已停用的库###ContributeDisabled", ref showDisabled))
        {
            plugin.Config.ContributeShowDisabled = showDisabled;
            plugin.SaveConfig();
            rebuildPending = true;
            rebuildRepoPending = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾上后连已停用仓库里的插件也列出来；这类插件当前不会出现在安装器里。\n"
                             + "还没加进你库里的仓库不受这个开关影响 —— 那是整座云端语料，都能参与翻译。");
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("隐藏主库###DiscoveryHideOfficial", ref hideOfficial))
        {
            rebuildPending = true;
            rebuildRepoPending = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("隐掉卫月官方主库（Dip17）里的插件，只看第三方；官方库本来就在安装器里，不用从这里加。");
        }
    }

    /// <summary>
    ///     插件视图：一行一个插件。
    /// </summary>
    private void DrawPluginTable(float tableHeight)
    {
        RebuildFiltered();

        var showIconColumn = plugin.Config.ShowIconsInContribute;
        var columns = showIconColumn ? 10 : 9;   // ##sel + 状态 + [图标] + 插件名 + 作者 + 来源库 + 简介 + ♥ + 更新 + 操作

        if (!ImGui.BeginTable(
                "###DiscoveryRows",
                columns,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings,
                new Vector2(0, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        var column = 0u;
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize | ImGuiTableColumnFlags.NoSort, 26, column++);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 72, column++);
        if (showIconColumn)
        {
            ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 26, column++);
        }

        ImGui.TableSetupColumn("插件名", ImGuiTableColumnFlags.WidthFixed, 180, column++);
        ImGui.TableSetupColumn("作者", ImGuiTableColumnFlags.WidthFixed, 110, column++);
        ImGui.TableSetupColumn("来源库", ImGuiTableColumnFlags.WidthFixed, 120, column++);
        ImGui.TableSetupColumn("简介", ImGuiTableColumnFlags.WidthStretch, 0, column++);
        ImGui.TableSetupColumn("♥", ImGuiTableColumnFlags.WidthFixed, 86, column++);
        ImGui.TableSetupColumn("更新", ImGuiTableColumnFlags.WidthFixed, 80, column++);
        ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 58, column);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TableHeader("##sel");

        ImGui.TableNextColumn();
        ImGui.TableHeader("状态");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "缺译 = 上游给了内容、但还没有译文；机器译 = 译文全部来自机器；\n"
                + "你译 = 有你贡献过的字段；带 ! 表示原文改过、需要复核。");
        }

        if (showIconColumn)
        {
            ImGui.TableNextColumn();
            ImGui.TableHeader("##icon");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("插件名");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("点这一行展开详情：三个字段的原文与译文、点赞、编辑校对。）");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("作者");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("仓库清单里的作者名；可用来排序。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("来源库");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("插件所在的库链；点一下复制地址，右键可在浏览器打开。\n「官方主库」= 卫月官方主库（Dip17）。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("简介");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("一行简介（上游原文）；详情与译文在点开的行里看。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("♥");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("本周点赞数（后面是总数）。一个插件一周只能点一次，下周可以再点。\n默认排序就是按本周点赞降序。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("更新");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("上游清单里这个插件最后更新的日期。");
        }

        ImGui.TableNextColumn();

        var clipper = new ImGuiListClipper();
        clipper.Begin(filtered.Count);

        while (clipper.Step())
        {
            iconMisses.Clear();

            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                if (i < 0 || i >= filtered.Count)
                {
                    continue;
                }

                DrawRow(filtered[i], showIconColumn);
            }
        }

        ImGui.EndTable();
    }

    /// <summary>
    ///     仓库视图：一行一条库链（点开展开它提供的插件）。
    /// </summary>
    private void DrawRepoTable(float tableHeight)
    {
        RebuildRepoGroups();

        var showIconColumn = plugin.Config.ShowIconsInContribute;
        var columns = showIconColumn ? 7 : 6;

        if (!ImGui.BeginTable(
                "###ContributeRepos",
                columns,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings,
                new Vector2(0, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 26, 0);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 72, 1);
        if (showIconColumn)
        {
            ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 26, 2);
        }

        ImGui.TableSetupColumn("仓库", ImGuiTableColumnFlags.WidthFixed, 210, 3);
        ImGui.TableSetupColumn("插件数", ImGuiTableColumnFlags.WidthFixed, 190, 4);
        ImGui.TableSetupColumn("说明", ImGuiTableColumnFlags.WidthStretch, 0, 5);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 104, 6);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TableHeader("##sel");

        ImGui.TableNextColumn();
        ImGui.TableHeader("状态");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("这条库已经在你的列表里（启用 / 停用），还是还没加进来；\n「来源未知」= 词表里没记这条插件的库链（旧词表）。");
        }

        if (showIconColumn)
        {
            ImGui.TableNextColumn();
            ImGui.TableHeader("##icon");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("仓库");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("点仓库名展开它提供的插件；右键可复制 / 在浏览器打开 / 加到我的库。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("插件数");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("这条库提供了多少个插件，其中多少个缺译文、多少个有你贡献的译文。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("说明");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("已启用 / 已停用 / 未加入 / 来源未知；停用的可以就地点「启用」，没加的可以「加库」。");
        }

        ImGui.TableNextColumn();

        foreach (var group in repoGroups)
        {
            DrawRepoGroupRow(group, showIconColumn);
        }

        ImGui.EndTable();
    }

    private void DrawRepoGroupRow(RepoGroup group, bool showIconColumn)
    {
        var id = group.IsOfficial ? "official" : group.URL.Length == 0 ? "unknown" : group.URL;
        var expanded = expandedRepos.Contains(id);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        var allSelected = group.Plugins.Count > 0 && group.Plugins.All(x => selected.Contains(x.InternalName));
        if (ImGui.Checkbox("##selrepo-" + id, ref allSelected))
        {
            foreach (var plugin in group.Plugins)
            {
                if (allSelected)
                {
                    selected.Add(plugin.InternalName);
                }
                else
                {
                    selected.Remove(plugin.InternalName);
                }
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾上这条库里的全部插件");
        }

        ImGui.TableNextColumn();
        if (group.IsOfficial)
        {
            UiHelpers.ColoredText(UiHelpers.Info, "官方库");
        }
        else if (group.URL.Length == 0)
        {
            UiHelpers.ColoredText(UiHelpers.Muted, "来源未知");
        }
        else if (!group.Known)
        {
            UiHelpers.ColoredText(UiHelpers.Muted, "未加入");
        }
        else if (group.Enabled)
        {
            UiHelpers.ColoredText(UiHelpers.Good, "启用");
        }
        else
        {
            UiHelpers.ColoredText(UiHelpers.Muted, "停用");
        }

        if (showIconColumn)
        {
            ImGui.TableNextColumn();
        }

        // 仓库名：小按钮，点一下展开 / 收起
        ImGui.TableNextColumn();
        if (ImGui.SmallButton((expanded ? "\u25bc " : "\u25b6 ") + group.Short + "###grp-" + id))
        {
            if (expanded)
            {
                expandedRepos.Remove(id);
            }
            else
            {
                expandedRepos.Add(id);
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                (group.IsOfficial ? "卫月官方主库（Dip17）" : group.URL.Length == 0 ? "词表里没有记它的来源库（旧词表）" : group.URL)
                + "\n点一下展开它提供的插件；右键可复制 / 加库");
        }

        if (ImGui.BeginPopupContextItem("##ctxrepo-" + id))
        {
            if (group.URL.Length > 0 && !group.IsOfficial)
            {
                if (ImGui.MenuItem("复制链接"))
                {
                    ImGui.SetClipboardText(group.URL);
                    SetStatus("已复制库链地址", isError: false);
                }

                if (ImGui.MenuItem("在浏览器打开"))
                {
                    OpenInBrowser(group.URL);
                }

                if (!group.Known && ImGui.MenuItem("加到我的库（待确认）"))
                {
                    repoInput = group.URL;
                    SetStatus("地址已填到操作条的输入框，点「添加到我的库」确认", isError: false);
                }
            }

            ImGui.EndPopup();
        }

        ImGui.TableNextColumn();
        ImGui.Text($"{group.Plugins.Count} 个");
        ImGui.SameLine();
        if (group.MissingCount > 0)
        {
            UiHelpers.ColoredText(UiHelpers.Bad, $"· 缺译 {group.MissingCount}");
        }
        else
        {
            UiHelpers.ColoredText(UiHelpers.Muted, "· 无缺译");
        }

        if (group.UserCount > 0)
        {
            ImGui.SameLine();
            UiHelpers.ColoredText(UiHelpers.Good, $"· 你译 {group.UserCount}");
        }

        ImGui.TableNextColumn();
        ImGui.TextDisabled(
            group.IsOfficial
                ? "卫月官方主库"
                : group.URL.Length == 0
                    ? "词表里没有记它的来源库"
                    : !group.Known
                        ? "还没加进你的库"
                        : group.Enabled
                            ? "已经在你的库里"
                            : "这条库已停用");

        // 启用 / 加库按钮独占最后一列：列宽拖窄也不会把按钮挤到下一行、把行高顶起来
        ImGui.TableNextColumn();
        if (!group.IsOfficial && group.URL.Length > 0 && !group.Enabled)
        {
            if (group.Known)
            {
                if (ImGui.SmallButton("启用###en-" + id))
                {
                    plugin.Repos.SetEnabled([group.URL], true, out _);
                    plugin.Repos.Save(out _);
                    plugin.Repos.TriggerReload(out _);
                    plugin.TrackFirstSeen();
                    rebuildPending = true;
                    rebuildRepoPending = true;
                    SetStatus("已启用这条库；卫月会重新抓取它的插件", isError: false);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("保留链接、把它重新启用；启用后卫月会去抓它的插件");
                }
            }
            else
            {
                if (ImGui.SmallButton("加库###add-" + id))
                {
                    repoInput = group.URL;
                    SetStatus("地址已填到操作条的输入框，点「添加到我的库」确认", isError: false);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("把这条库加进你的第三方插件列表（会先自动备份）");
                }
            }
        }

        if (!expanded)
        {
            return;
        }

        foreach (var plugin in group.Plugins)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            var picked = selected.Contains(plugin.InternalName);
            if (ImGui.Checkbox("##selp-" + plugin.InternalName, ref picked))
            {
                if (picked)
                {
                    selected.Add(plugin.InternalName);
                }
                else
                {
                    selected.Remove(plugin.InternalName);
                }
            }

            ImGui.TableNextColumn();
            var (label, color) = StatusOf(plugin);
            UiHelpers.ColoredText(color, label);

            if (showIconColumn)
            {
                ImGui.TableNextColumn();
                PeekIcon(plugin);
                if (iconHandles.TryGetValue(plugin.InternalName, out var handle) && !handle.IsNull)
                {
                    ImGui.Image(handle, new Vector2(18, 18));
                }
            }

            ImGui.TableNextColumn();
            ImGui.Indent(ImGui.GetFontSize());
            if (ImGui.SmallButton(plugin.DisplayName + "###rname-" + plugin.InternalName))
            {
                BeginEdit(plugin);
            }

            ImGui.Unindent(ImGui.GetFontSize());

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("点开详情，改三个字段");
            }

            ImGui.TableNextColumn();
            ImGui.TextDisabled($"{plugin.TranslatedFields}/{plugin.TotalFields + plugin.TemplateFields}");

            ImGui.TableNextColumn();
            UiHelpers.Fitted(
                FirstNonEmpty(plugin.OriginalPunchline, plugin.OriginalDescription),
                plugin.OriginalPunchline + "\n\n" + plugin.OriginalDescription);

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("改进…###redit-" + plugin.InternalName))
            {
                BeginEdit(plugin);
            }
        }
    }

    /// <summary>
    ///     选中项的操作条：批量翻译 + 加库。
    /// </summary>
    private void DrawActionBar()
    {
        var selectedCount = selected.Count;
        if (selectedCount == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"翻译所选（{selectedCount}）###BatchEdit"))
        {
            BeginBatchEdit();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("把勾选的插件逐个打开详情，改完一个下一个；每次保存都会存到本地。");
        }

        if (selectedCount == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("全选当前###SelectAll"))
        {
            if (view == "repos")
            {
                foreach (var group in repoGroups)
                {
                    foreach (var plugin in group.Plugins)
                    {
                        selected.Add(plugin.InternalName);
                    }
                }
            }
            else
            {
                foreach (var entry in filtered)
                {
                    selected.Add(entry.InternalName);
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("清空勾选###ClearSel"))
        {
            selected.Clear();
        }

        // ---- 批量加库：把勾选插件所在的库一次加进来 ----
        ImGui.SameLine();
        ImGui.TextDisabled("│");
        ImGui.SameLine();

        var addable = SelectedReposToAdd();
        if (addable.Count == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"添加到我的库（{addable.Count}）###AddSelectedRepos"))
        {
            AddSelectedRepositories(addable);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(addable.Count == 0
                ? "先在左边勾选插件（勾的插件所在库会被一次加进来）；\n已经在你的库里的会跳过。"
                : $"把勾选的插件所在的 {addable.Count} 条库加进你的第三方插件列表；\n添加前会自动备份一份仓库列表。");
        }

        if (addable.Count == 0)
        {
            ImGui.EndDisabled();
        }

        // ---- 撤回上一次「添加」 ----
        ImGui.SameLine();
        var lastAdd = LastAddRecord();
        if (lastAdd is null)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"撤回添加（{lastAdd?.Count ?? 0}）###UndoAdd"))
        {
            UndoLastAdd();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(lastAdd is null
                ? "还没有刚添加过的库；每次「添加到我的库」后都可以在这里一键撤回。"
                : $"把刚加进来的 {lastAdd.Count} 条库从列表里移除（{lastAdd.TimeUTC.ToLocalTime():HH:mm} 那次添加）");
        }

        if (lastAdd is null)
        {
            ImGui.EndDisabled();
        }

        // ---- 单条：粘地址添加 ----
        ImGui.SameLine();
        ImGui.TextDisabled("│");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(230);
        ImGui.InputTextWithHint("###AddRepoUrl", "或粘一条仓库地址…", ref repoInput, 512);
        ImGui.SameLine();
        var canAddURL = repoInput.Trim().Length > 0;
        if (!canAddURL)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("添加这条###AddRepoUrlBtn"))
        {
            AddRepository();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("把这一条库加进你的第三方插件列表（pluginmaster.json 地址）；\n添加前会自动备份，加错了用右边的「撤回添加」。");
        }

        if (!canAddURL)
        {
            ImGui.EndDisabled();
        }
    }

    private void DrawRow(TranslationIndexEntry entry, bool showIconColumn)
    {
        var isOpen = string.Equals(expandedEntry, entry.InternalName, StringComparison.Ordinal);
        ImGui.PushID(entry.InternalName);
        ImGui.TableNextRow();

        // 整行点击展开详情（和「插件汉化」同一个做法）：Selectable 铺在行底层、跨所有列、允许被按钮覆盖
        var rowHeight = rowHeights.TryGetValue(entry.InternalName, out var knownHeight) ? knownHeight : 42f;
        ImGui.TableSetColumnIndex(0);
        if (ImGui.Selectable("##row", isOpen,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
                new Vector2(0, rowHeight)))
        {
            expandedEntry = isOpen ? null : entry.InternalName;
        }

        var rowTop = ImGui.GetCursorScreenPos().Y;
        ImGui.SameLine(0, 0);

        // 勾选
        var picked = selected.Contains(entry.InternalName);
        if (ImGui.Checkbox("##sel", ref picked))
        {
            if (picked)
            {
                selected.Add(entry.InternalName);
            }
            else
            {
                selected.Remove(entry.InternalName);
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾上后可以用「翻译所选」逐个改，方便集中处理几条。");
        }

        // 状态
        ImGui.TableNextColumn();
        var (label, color) = StatusOf(entry);
        UiHelpers.ColoredText(color, label);
        if (ImGui.IsItemHovered())
        {
            var lines = new List<string>();
            if (entry.MissingFields.Count > 0)
            {
                lines.Add("缺：" + string.Join("、", entry.MissingFields.Select(FieldLabel)));
            }
            else
            {
                lines.Add("上游给的字段都有译文");
            }

            if (entry.ContributableFields.Count > 0)
            {
                lines.Add("上游没提供、你可以自己补：" + string.Join("、", entry.ContributableFields.Select(FieldLabel)));
            }

            if (entry.HasReview)
            {
                lines.Add("有字段的原文改过，建议复核");
            }

            ImGui.SetTooltip(string.Join("\n", lines));
        }

        // 图标
        if (showIconColumn)
        {
            ImGui.TableNextColumn();
            PeekIcon(entry);
            if (iconHandles.TryGetValue(entry.InternalName, out var handle) && !handle.IsNull)
            {
                ImGui.Image(handle, new Vector2(18, 18));
            }
        }

        // 插件名（纯文本；点整行展开详情）
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(entry.DisplayName);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(entry.DisplayName + "\n" + entry.InternalName + "\n点这一行展开详情");
        }

        // 作者
        ImGui.TableNextColumn();
        if (string.IsNullOrWhiteSpace(entry.Author))
        {
            ImGui.TextDisabled("—");
        }
        else
        {
            UiHelpers.Fitted(entry.Author, entry.Author);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(entry.Author);
            }
        }

        // 来源库
        ImGui.TableNextColumn();
        if (entry.IsOfficial)
        {
            UiHelpers.ColoredText(UiHelpers.Info, "官方主库");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("卫月官方主库（Dip17）里的插件；官方库不能从这里加。");
            }
        }
        else if (entry.RepositoryURL is null)
        {
            ImGui.TextDisabled("—");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("读不到来源地址（旧词表没记库链）");
            }
        }
        else
        {
            if (ImGui.SmallButton(RepoShort(entry.RepositoryURL) + "###repo"))
            {
                ImGui.SetClipboardText(entry.RepositoryURL);
                SetStatus("已复制库链地址", isError: false);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(entry.RepositoryURL + "\n点一下复制；右键可加入我的库");
            }

            if (ImGui.BeginPopupContextItem("##ctxrepo"))
            {
                if (ImGui.MenuItem("复制链接"))
                {
                    ImGui.SetClipboardText(entry.RepositoryURL);
                }

                if (ImGui.MenuItem("在浏览器打开"))
                {
                    OpenInBrowser(entry.RepositoryURL);
                }

                if (!entry.RepositoryKnown && ImGui.MenuItem("加到我的库（待确认）"))
                {
                    repoInput = entry.RepositoryURL;
                    SetStatus("地址已填到操作条的输入框，点「添加这条」确认", isError: false);
                }

                ImGui.EndPopup();
            }
        }

        // 简介（上游原文；详情与译文在展开行里看）
        ImGui.TableNextColumn();
        var preview = FirstNonEmpty(entry.OriginalPunchline, entry.OriginalDescription);
        UiHelpers.Fitted(preview, entry.OriginalPunchline + "\n\n" + entry.OriginalDescription);

        // ♥ 本周/总共
        ImGui.TableNextColumn();
        DrawLikeButton(entry, "row");

        // 更新
        ImGui.TableNextColumn();
        if (entry.Updated is { } timestamp)
        {
            var updated = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime();
            ImGui.TextDisabled(updated.ToString("yyyy-MM-dd"));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("最后更新：" + updated.ToString("yyyy-MM-dd HH:mm"));
            }
        }
        else
        {
            ImGui.TextDisabled("—");
        }

        // 操作
        ImGui.TableNextColumn();
        if (ImGui.SmallButton((entry.State == "missing" ? "补上…" : "改进…") + "###edit"))
        {
            BeginEdit(entry);
        }

        // 量一下这一行实际多高，下一帧整行点击的 Selectable 用它（首帧用估算值）
        rowHeights[entry.InternalName] = Math.Max(20f, ImGui.GetItemRectMax().Y - rowTop);

        if (isOpen)
        {
            DrawExpandedRow(entry, showIconColumn);
        }

        ImGui.PopID();
    }

    /// <summary>展开行：三个字段的原文/译文 + 点赞 + 推荐数 + 编辑入口（列顺序与主行一致）。</summary>
    private void DrawExpandedRow(TranslationIndexEntry entry, bool showIconColumn)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();   // ##sel
        ImGui.TableNextColumn();   // 状态
        if (showIconColumn)
        {
            ImGui.TableNextColumn();
        }

        // 插件名列：内部名
        ImGui.TableNextColumn();
        ImGui.TextDisabled(entry.InternalName);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(entry.InternalName);
        }

        // 作者列：推荐数
        ImGui.TableNextColumn();
        var recommends = discoveryStats?.RecommendsOf(entry.InternalName) ?? 0;
        ImGui.TextDisabled($"推荐 {recommends}");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("从云端把这条库链加进自己库的次数（按插件计）。");
        }

        // 来源库列：完整地址 / 加库入口
        ImGui.TableNextColumn();
        if (entry.IsOfficial)
        {
            UiHelpers.ColoredText(UiHelpers.Info, "官方主库");
        }
        else if (entry.RepositoryURL is { Length: > 0 } repo)
        {
            ImGui.TextDisabled(RepoShort(repo));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(repo);
            }

            if (!entry.RepositoryKnown)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("加库###expand-add"))
                {
                    repoInput = repo;
                    SetStatus("地址已填到操作条的输入框，点「添加这条」确认", isError: false);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("把这条库加进你的第三方插件列表（会先自动备份）");
                }
            }
        }
        else
        {
            ImGui.TextDisabled("来源未知");
        }

        // 简介列：三个字段的原文 / 译文
        ImGui.TableNextColumn();
        DrawFieldLine("插件名", entry.OriginalName, entry.Entry?.Name);
        DrawFieldLine("一行简介", entry.OriginalPunchline, entry.Entry?.Punchline);
        DrawFieldLine("详情", entry.OriginalDescription, entry.Entry?.Description);

        // ♥ 列：大按钮
        ImGui.TableNextColumn();
        DrawLikeButton(entry, "detail");

        // 更新列
        ImGui.TableNextColumn();
        if (entry.Updated is { } timestamp)
        {
            ImGui.TextDisabled(DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("yyyy-MM-dd"));
        }
        else
        {
            ImGui.TextDisabled("—");
        }

        // 操作列
        ImGui.TableNextColumn();
        if (ImGui.SmallButton("编辑校对…###edit-expanded"))
        {
            BeginEdit(entry);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("打开三个字段的编辑弹窗，改完先存本地。");
        }
    }

    /// <summary>展开行里一行字段：标签 + 译文（悬停看完整原文/译文）。</summary>
    private static void DrawFieldLine(string label, string original, TransPair? pair)
    {
        var translated = pair is { HasTranslation: true } ? pair.Translated : string.Empty;
        if (string.IsNullOrWhiteSpace(original) && translated.Length == 0)
        {
            ImGui.TextDisabled(label + "：上游没提供，可以自己补");
            return;
        }

        ImGui.TextDisabled(label + "：");
        ImGui.SameLine(0, 4);
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(translated) ? "（还没译文）" : translated.Replace('\n', ' '));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"原文：{(string.IsNullOrWhiteSpace(original) ? "（无）" : original)}\n\n译文：{(translated.Length == 0 ? "（无）" : translated)}");
        }
    }

    /// <summary>行尾的 ♥：本周/总共；本机清过的一周内变灰，下周可以再点。</summary>
    private void DrawLikeButton(TranslationIndexEntry entry, string suffix)
    {
        var week = discoveryStats?.Week ?? string.Empty;
        var liked = week.Length > 0 && discoveryState.LikedThisWeek(entry.InternalName, week);
        var unsynced = discoveryState.HasPending(entry.InternalName);
        var weekly = discoveryStats?.WeeklyOf(entry.InternalName) ?? 0;
        var total = discoveryStats?.TotalOf(entry.InternalName) ?? 0;

        if (liked)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"♥ {weekly}/{total}###like-{suffix}"))
        {
            MarkLike(entry);
        }

        if (liked)
        {
            ImGui.EndDisabled();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var lines = new List<string>
            {
                $"本周 {weekly} 赞 · 总共 {total} 赞",
                liked ? "这周你已经点过赞了（下周可以再点）" : "点一下为这个插件点赞（一周一次，匿名上报）",
            };
            if (unsynced)
            {
                lines.Add("有点赞还没同步到云端，会自动重试");
            }

            ImGui.SetTooltip(string.Join("\n", lines));
        }
    }

    /// <summary>本机点赞：先记下来（支持离线/失败重试），再后台上报；成功用云端真值回填。</summary>
    private void MarkLike(TranslationIndexEntry entry)
    {
        var week = discoveryStats?.Week ?? string.Empty;
        discoveryState.MarkLiked(entry.InternalName, week);
        if (discoveryStats is not null)
        {
            discoveryStats.WeeklyLikes[entry.InternalName] = discoveryStats.WeeklyOf(entry.InternalName) + 1;
            discoveryStats.TotalLikes[entry.InternalName] = discoveryStats.TotalOf(entry.InternalName) + 1;
        }

        SetStatus("已点赞，正在同步到云端…", isError: false);
        rebuildPending = true;
        var name = entry.InternalName;
        _ = Task.Run(async () =>
        {
            var (ok, total, weekly, _) = await DiscoveryRelay.LikeAsync(name, CancellationToken.None).ConfigureAwait(false);
            if (!ok)
            {
                return;   // 留在待重试里，不谎报成功
            }

            discoveryState.CompleteLike(name);
            likeResults.Enqueue((name, total, weekly));
        });
    }

    /// <summary>
    ///     状态列的文字与颜色（表格与仓库视图共用）。
    /// </summary>
    private static (string Label, Vector4 Color) StatusOf(TranslationIndexEntry entry)
    {
        var label = entry.State switch
        {
            "missing" => "缺译",
            "user" => "你译",
            _ => "机器译",
        };

        if (entry.HasReview)
        {
            label += " !";
        }

        var color = entry.State switch
        {
            "missing" => UiHelpers.Bad,
            "user" => UiHelpers.Good,
            _ => UiHelpers.Info,
        };

        return (label, color);
    }

    private void FilterRadio(StateFilter value, string label)
    {
        ImGui.SameLine();
        if (ImGui.RadioButton(label + "###state" + label, stateFilter == value))
        {
            stateFilter = value;
            rebuildPending = true;
        }
    }
}
