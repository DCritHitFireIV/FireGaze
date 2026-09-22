using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

internal sealed partial class ContributeWindow : Window
{
    /// <summary>
    ///     下半栏（像编辑器下方的工作区）：页签 = 待提交 + 每一批历史提交（命名用日期，悬停看具体时间）。
    ///     待提交里可以直接改 / 删 / 清空 / 撤回；历史快照只读。
    /// </summary>
    private void DrawWorkspace(float height)
    {
        if (ImGui.BeginChild("###ContributeWorkspace", new Vector2(0, height)))
        {
            var pendingCount = store.Count;
            var historyCount = store.History.Sum(x => x.Contributions.Count);

            if (ImGui.BeginTabBar("###ContributeWorkspaceTabs", ImGuiTabBarFlags.None))
            {
                var pendingLabel = pendingCount > 0 ? $"待提交（{pendingCount}）###ws-pending" : "待提交###ws-pending";
                if (ImGui.BeginTabItem(pendingLabel))
                {
                    workspaceTab = -1;
                    ImGui.EndTabItem();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("你改过、还没提交的译文；可直接改或删。");
                }

                // 历史提交：合并成一个页签（之前一批一个页签，条数一多就排满了）；
                // 批次信息在表格里用组头体现，见 DrawHistoryTable。
                var historyLabel = historyCount > 0 ? $"历史提交（{historyCount}）###ws-history" : "历史提交###ws-history";
                if (ImGui.BeginTabItem(historyLabel))
                {
                    workspaceTab = 0;
                    ImGui.EndTabItem();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(store.History.Count == 0
                        ? "还没有提交过；提交后每次都会在这台机器上留一份，方便回看、加回待提交或删掉。"
                        : $"本机留档：{store.History.Count} 批 / {historyCount} 条；可整批或逐条加回待提交，也可以删掉。");
                }

                ImGui.EndTabBar();
            }

            DrawWorkspaceButtons();

            if (workspaceTab < 0 || store.History.Count == 0)
            {
                workspaceTab = -1;
                DrawPendingTable();
            }
            else
            {
                DrawHistoryTable();
            }
        }

        ImGui.EndChild();
    }

    /// <summary>
    ///     下栏按钮组：待提交一套（提交/删除/清空/撤回）、历史提交一套（加回/删除/清空），末尾统一画确认框。
    /// </summary>
    private void DrawWorkspaceButtons()
    {
        if (workspaceTab >= 0)
        {
            DrawHistoryButtons();
        }
        else
        {
            DrawPendingButtons();
        }

        DrawWorkspaceDialogs();
    }

    /// <summary>
    ///     待提交那一栏的按钮。
    /// </summary>
    private void DrawPendingButtons()
    {
        var pending = store.Count;
        var selectedCount = selectedRecords.Count;

        if (pending == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(awaitingConfirm
                ? $"确认已提交（{pending}）###ConfirmContrib"
                : $"一键提交全部（{pending}）###SubmitContrib"))
        {
            if (awaitingConfirm)
            {
                ConfirmSubmitted();
            }
            else
            {
                SubmitContributions();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                awaitingConfirm
                    ? "在刚打开的 GitHub 页面上按过 Submit 之后再点这里：会把待提交清空，并在这台机器的历史提交里留一份。"
                    : pending == 0
                        ? "先在下面攒几条译文（上面表格里点「补上… / 改进…」）"
                        : "打开 GitHub 的提交页，内容已经替你填好（匿名，不含账号信息）；\n"
                          + "在网页上按 Submit，回来点「确认已提交」。");
        }

        if (pending == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (selectedCount == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"删除勾选（{selectedCount}）###DeleteContrib"))
        {
            DeleteSelectedContributions();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(selectedCount == 0
                ? "勾选下面表格里要删的条目；删掉后词表会恢复成改之前的样子，也可以用「撤回」找回。"
                : $"删掉勾选的 {selectedCount} 条，并把词表恢复成改之前的样子");
        }

        if (selectedCount == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("清空待提交###ClearContrib"))
        {
            ImGui.OpenPopup("清空待提交###ConfirmClear");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("把「待提交」整栏清掉（有二次确认，而且可以用「撤回」找回）");
        }

        ImGui.SameLine();
        if (!store.CanUndo)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("撤回删除###UndoContrib"))
        {
            UndoContributions();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(store.CanUndo ? "把上一次删除 / 清空恢复回来" : "还没有可撤回的删除或清空");
        }

        if (!store.CanUndo)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(selectedCount > 0
            ? $"已选 {selectedCount} 条 · 这一栏是你改过、还没提交的译文"
            : "这一栏是你改过、还没提交的译文；提交后会挪进「历史提交」");
    }

    /// <summary>
    ///     历史提交那一栏的按钮：整批/逐条加回待提交、删除所选、清空。
    /// </summary>
    private void DrawHistoryButtons()
    {
        var selected = SelectedHistoryRecords().Count;
        var total = store.History.Sum(x => x.Contributions.Count);

        if (selected == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"移回待提交（{selected}）###RecallHistory"))
        {
            RecallSelectedHistory();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(selected == 0
                ? "先在上面勾几条（或勾某一批的组头）；加回后可以从「待提交」里改了再交一次"
                : $"把勾选的 {selected} 条从历史提交移回「待提交」：\n历史提交里不再保留这几条（是移动，不是复制）");
        }

        if (selected == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (selected == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"删除所选（{selected}）###DeleteHistory"))
        {
            ImGui.OpenPopup("删除所选###ConfirmDeleteHistory");
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(selected == 0
                ? "先勾几条要删的记录"
                : $"从这台机器的历史提交里删掉这 {selected} 条（有二次确认）");
        }

        if (selected == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (total == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("清空历史提交###ClearHistory"))
        {
            ImGui.OpenPopup("清空历史提交###ConfirmClearHistory");
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("把这台机器记的提交历史整个清掉（有二次确认）；已经交出去的译文不受影响");
        }

        if (total == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(selected > 0
            ? $"已选 {selected} 条 · 共 {total} 条 · {store.History.Count} 批（记在这台机器上）"
            : $"共 {total} 条 · {store.History.Count} 批（记在这台机器上）");

        DrawHistoryDetailPopup();
    }

    /// <summary>
    ///     下栏的确认框：清空待提交 / 删除所选历史 / 清空历史提交（默认焦点都在「取消」）。
    /// </summary>
    private void DrawWorkspaceDialogs()
    {
        ImGui.SetNextWindowSize(new Vector2(460, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("清空待提交###ConfirmClear", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("清空后待提交这一栏就空了，词表会恢复成你改之前的样子（之后可以用「撤回删除」找回）。确定吗？");
            ImGui.Spacing();
            if (ImGui.Button("取消", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();
            ImGui.SameLine();
            if (ImGui.Button("清空", new Vector2(120, 0)))
            {
                clearRequested = true;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (clearRequested)
        {
            clearRequested = false;
            ClearContributions();
        }

        var selected = SelectedHistoryRecords();
        ImGui.SetNextWindowSize(new Vector2(500, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("删除所选###ConfirmDeleteHistory", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"从这台机器的历史提交里删掉勾选的 {selected.Count} 条？");
            ImGui.TextDisabled("已经交出去的译文不受影响，游戏里的译文也不会变；删掉后本机无法恢复。");
            ImGui.Spacing();
            DrawHistorySamples(selected);
            ImGui.Spacing();
            if (ImGui.Button("取消", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();
            ImGui.SameLine();
            if (ImGui.Button("删除", new Vector2(120, 0)))
            {
                deleteHistoryRequested = true;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (deleteHistoryRequested)
        {
            deleteHistoryRequested = false;
            DeleteSelectedHistory();
        }

        var total = store.History.Sum(x => x.Contributions.Count);
        ImGui.SetNextWindowSize(new Vector2(460, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("清空历史提交###ConfirmClearHistory", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"把这台机器上记的 {total} 条提交历史（{store.History.Count} 批）全部删掉？");
            ImGui.TextDisabled("已经交出去的译文不受影响，游戏里的译文也不会变；删掉后本机无法恢复。");
            ImGui.Spacing();
            if (ImGui.Button("取消", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();
            ImGui.SameLine();
            if (ImGui.Button("全部清空", new Vector2(120, 0)))
            {
                clearHistoryRequested = true;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (clearHistoryRequested)
        {
            clearHistoryRequested = false;
            ClearHistoryRecords();
        }
    }

    /// <summary>
    ///     确认框里列前几条样本，方便核对到底删哪些。
    /// </summary>
    private void DrawHistorySamples(IReadOnlyList<ContributionRecord> records)
    {
        foreach (var record in records.Take(3))
        {
            var text = record.Translated.Replace('\n', ' ');
            if (text.Length > 40)
            {
                text = text[..40] + "…";
            }

            ImGui.TextDisabled($"{record.TimeLocal:MM-dd HH:mm} · {record.DisplayName} / {FieldLabel(record.Field)}：「{text}」");
        }

        if (records.Count > 3)
        {
            ImGui.TextDisabled($"等 {records.Count} 条");
        }
    }

    /// <summary>
    ///     待提交列表：勾选 + 行内「改」「删」。
    /// </summary>
    private void DrawPendingTable()
    {
        var records = store.Records;
        if (records.Count == 0)
        {
            ImGui.TextDisabled("还没改过任何译文。在上面点插件名或「补上…」，改完就会出现在这里。");
            return;
        }

        var tableHeight = MathF.Max(80f, ImGui.GetContentRegionAvail().Y - 4f);
        if (!ImGui.BeginTable(
                "###PendingRows",
                5,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings,
                new Vector2(0, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 26, 0);
        ImGui.TableSetupColumn("插件", ImGuiTableColumnFlags.WidthFixed, 170, 1);
        ImGui.TableSetupColumn("字段", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 76, 2);
        ImGui.TableSetupColumn("译文", ImGuiTableColumnFlags.WidthStretch, 0, 3);
        ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 56, 4);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TableHeader("##sel");
        ImGui.TableNextColumn();
        ImGui.TableHeader("插件");
        ImGui.TableNextColumn();
        ImGui.TableHeader("字段");
        ImGui.TableNextColumn();
        ImGui.TableHeader("译文");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("悬停看全文；点「改」重新编辑（同一个插件一个字段只会有一条）");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("操作");

        var clipper = new ImGuiListClipper();
        clipper.Begin(records.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var record = records[i];
                var key = RecordKey(record);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var picked = selectedRecords.Contains(key);
                if (ImGui.Checkbox("##rec-" + key, ref picked))
                {
                    if (picked)
                    {
                        selectedRecords.Add(key);
                    }
                    else
                    {
                        selectedRecords.Remove(key);
                    }
                }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton(record.DisplayName + "###recname-" + key))
                {
                    EditExistingContribution(record);   // 点名字也能进详情，跟上面一致
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(record.DisplayName + "\n" + record.InternalName + "\n点开详情，改这一条译文");
                }

                ImGui.TableNextColumn();
                ImGui.TextDisabled(FieldLabel(record.Field));

                ImGui.TableNextColumn();
                UiHelpers.Fitted(record.Translated.Replace('\n', ' '), record.Translated);

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("改###editrec-" + key))
                {
                    EditExistingContribution(record);
                }
            }
        }

        ImGui.EndTable();
    }

    /// <summary>
    ///     历史提交：合并成一张表，内部按批次分组（组头 = 提交时间 + 条数 + 整批勾选）。
    /// </summary>
    private void DrawHistoryTable()
    {
        EnsureHistoryRows();

        if (historyRows.Count == 0)
        {
            ImGui.TextDisabled("还没有提交过。点「一键提交全部」之后，每次都会在这台机器上留一份，方便回看、加回待提交或删掉。");
            return;
        }

        var tableHeight = MathF.Max(60f, ImGui.GetContentRegionAvail().Y - 4f);
        if (!ImGui.BeginTable(
                "###HistoryRows",
                5,   // ##sel + 插件 + 字段 + 译文 + 操作
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings,
                new Vector2(0, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 26, 0);
        ImGui.TableSetupColumn("插件", ImGuiTableColumnFlags.WidthFixed, 170, 1);
        ImGui.TableSetupColumn("字段", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 76, 2);
        ImGui.TableSetupColumn("译文", ImGuiTableColumnFlags.WidthStretch, 0, 3);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 60, 4);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TableHeader("##sel");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾选要加回待提交或要删掉的记录；批次那一行的勾选框 = 整批全选。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("插件");
        ImGui.TableNextColumn();
        ImGui.TableHeader("字段");
        ImGui.TableNextColumn();
        ImGui.TableHeader("译文");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("这一条当时交出去的译文；悬停看全文，点「查看…」看完整内容。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("操作");

        var clipper = new ImGuiListClipper();
        clipper.Begin(historyRows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                if (i < 0 || i >= historyRows.Count)
                {
                    continue;
                }

                var row = historyRows[i];
                if (row.IsHeader)
                {
                    DrawHistoryHeaderRow(row);
                }
                else
                {
                    DrawHistoryRecordRow(row);
                }
            }
        }

        ImGui.EndTable();
    }

    /// <summary>
    ///     把历史提交铺成「组头 + 记录」的平表（供 clipper 用）；只在数据变过时重建。
    /// </summary>
    private void EnsureHistoryRows()
    {
        if (!historyRowsDirty)
        {
            return;
        }

        historyRowsDirty = false;
        historyRows.Clear();

        for (var i = store.History.Count - 1; i >= 0; i--)   // 最新的一批在最上面
        {
            var batch = store.History[i];
            historyRows.Add(new HistoryRow { Batch = batch });
            foreach (var record in batch.Contributions)
            {
                historyRows.Add(new HistoryRow { Batch = batch, Record = record });
            }
        }
    }

    /// <summary>
    ///     批次那一行：整批勾选（部分选中时显半选）+ 提交时间与条数。
    /// </summary>
    private void DrawHistoryHeaderRow(HistoryRow row)
    {
        var batch = row.Batch!;
        var total = batch.Contributions.Count;
        var selected = batch.Contributions.Count(x => selectedHistory.Contains(x.ID));

        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(0.11f, 0.15f, 0.20f, 1f)));

        ImGui.TableNextColumn();
        var flags = selected == total && total > 0 ? 3 : selected > 0 ? 1 : 0;
        if (ImGui.CheckboxFlags("##batch-" + batch.SubmittedLocal.Ticks, ref flags, 3))
        {
            var select = flags != 0;
            foreach (var record in batch.Contributions)
            {
                if (select)
                {
                    selectedHistory.Add(record.ID);
                }
                else
                {
                    selectedHistory.Remove(record.ID);
                }
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"这一批 {total} 条（{batch.TimeLabel} 提交）\n点一下 = 全选 / 全不选这一批");
        }

        ImGui.TableNextColumn();
        ImGui.TextDisabled(row.BatchLabel);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"{batch.TimeLabel} 提交 · 共 {total} 条");
        }

        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
    }

    private void DrawHistoryRecordRow(HistoryRow row)
    {
        var record = row.Record!;
        var picked = selectedHistory.Contains(record.ID);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.Checkbox("##his-" + record.ID, ref picked))
        {
            if (picked)
            {
                selectedHistory.Add(record.ID);
            }
            else
            {
                selectedHistory.Remove(record.ID);
            }
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(record.DisplayName);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(record.DisplayName + "\n" + record.InternalName);
        }

        ImGui.TableNextColumn();
        ImGui.TextDisabled(FieldLabel(record.Field));

        ImGui.TableNextColumn();
        UiHelpers.Fitted(record.Translated.Replace('\n', ' '), record.Translated);

        ImGui.TableNextColumn();
        if (ImGui.SmallButton("查看…###hisview-" + record.ID))
        {
            viewingHistory = record;
            ImGui.OpenPopup("留档详情###HistoryDetail");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("看全文（只读）；要改就先「加入待提交」");
        }
    }

    /// <summary>
    ///     留档条目的只读详情：看全文与两个时间，要改就先加入待提交。
    /// </summary>
    private void DrawHistoryDetailPopup()
    {
        if (!ImGui.BeginPopup("留档详情###HistoryDetail"))
        {
            return;
        }

        var record = viewingHistory;
        if (record is null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted(record.DisplayName + " · " + FieldLabel(record.Field));
        ImGui.TextDisabled(record.InternalName);
        ImGui.Separator();
        ImGui.TextDisabled($"写于 {record.TimeLocal:yyyy-MM-dd HH:mm}   ·   提交于 {HistorySubmittedAt(record):yyyy-MM-dd HH:mm}");
        ImGui.Spacing();
        ImGui.TextDisabled("交给维护者的译文");
        var text = record.Translated;
        ImGui.InputTextMultiline(
            "###hisdetail",
            ref text,
            4096,
            new Vector2(460, ImGui.GetTextLineHeight() * 6),
            ImGuiInputTextFlags.ReadOnly);
        ImGui.Spacing();
        if (ImGui.Button("移回待提交", new Vector2(140, 0)))
        {
            RecallRecords([record]);
            ImGui.CloseCurrentPopup();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("从历史提交移回「待提交」，可以改了再交一次（历史提交里不再保留这一条）");
        }

        ImGui.SameLine();
        if (ImGui.Button("关闭", new Vector2(120, 0)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    /// <summary>
    ///     这条留档是第几批交的（找不到就退回它自己的写入时间）。
    /// </summary>
    private DateTime HistorySubmittedAt(ContributionRecord record)
    {
        foreach (var batch in store.History)
        {
            if (batch.Contributions.Contains(record))
            {
                return batch.SubmittedLocal;
            }
        }

        return record.TimeLocal;
    }
}
