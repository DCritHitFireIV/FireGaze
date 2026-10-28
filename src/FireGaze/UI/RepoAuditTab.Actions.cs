using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.Diagnostics;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

internal sealed partial class RepoAuditTab
{
    /// <summary>Selection actions appear only when needed; recovery stays visible.</summary>
    private void DrawActionBar(int selectedCount, int selectedHidden, List<RepoAuditItem> snapshot)
    {
        var canSelect = !scanning;
        var filteredCount = snapshot.Count;
        using (UiHelpers.Disabled(!canSelect))
        {
            if (ImGui.Button($"全选当前（{filteredCount}）###SelectFiltered"))
            {
                lock (gate)
                    foreach (var item in snapshot.Where(x => x.IsSelectable)) selected.Add(item.URL);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("只选择当前搜索和筛选结果。");
        }

        if (selectedCount > 0)
        {
            PluginListLayout.ToolbarSameLineOrWrap(UiHelpers.LabelWidth("清空选择"));
            using (UiHelpers.Disabled(!canSelect))
            {
                if (ImGui.Button("清空选择###ClearSelection"))
                    lock (gate) selected.Clear();
            }

            var hint = $"已选 {selectedCount}" + (selectedHidden > 0 ? $" · 筛选外 {selectedHidden}" : string.Empty);
            PluginListLayout.ToolbarSameLineOrWrap(UiHelpers.LabelWidth(hint));
            ImGui.TextDisabled(hint);

            var canAct = !scanning;
            var disableLabel = $"停用所选（{selectedCount}）###DisableSelected";
            PluginListLayout.ToolbarSameLineOrWrap(UiHelpers.LabelWidth(disableLabel));
            using (UiHelpers.Disabled(!canAct))
            {
                if (ImGui.Button(disableLabel)) DisableSelected();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("停止加载所选仓库，保留链接，可撤回。");
            }

            var deleteLabel = $"删除所选（{selectedCount}）…###DeleteSelected";
            PluginListLayout.ToolbarSameLineOrWrap(UiHelpers.LabelWidth(deleteLabel));
            UiHelpers.PushDangerButton();
            using (UiHelpers.Disabled(!canAct))
            {
                if (ImGui.Button(deleteLabel)) deleteRequested = true;
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("移除所选仓库链接，会先备份，可撤回。");
            }
            UiHelpers.PopDangerButton();
        }

        var undo = plugin.Config.UndoHistory.Count > 0 ? plugin.Config.UndoHistory[^1] : null;
        var undoLabel = undo is null ? "撤回上次操作###Undo" : $"撤回：{undo.Describe()}###Undo";
        var undoWidth = UiHelpers.LabelWidth(undoLabel);
        var backupWidth = UiHelpers.LabelWidth("打开备份目录");
        var recoveryWidth = undoWidth + backupWidth + 10f;
        var right = ImGui.GetContentRegionMax().X;
        if (ImGui.GetItemRectMax().X + 10f + recoveryWidth <= ImGui.GetWindowPos().X + right)
            ImGui.SameLine(right - recoveryWidth);
        else
            ImGui.SetCursorPosX(MathF.Max(0, right - MathF.Min(recoveryWidth, right)));

        if (undo is null)
        {
            using (UiHelpers.Disabled()) ImGui.Button(undoLabel);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("还没有可撤回的操作。");
        }
        else
        {
            if (ImGui.Button(undoLabel))
            {
                var ok = plugin.TryUndoLast(out var message);
                SetStatus(ok ? message : "撤回失败：" + message, !ok);
                if (ok) ActivityLog.Info("仓库体检", "撤回上次操作：" + message);
                else ActivityLog.Warning("仓库体检", "撤回失败：" + message);
                RefreshFromLive();
            }
            if (ImGui.IsItemHovered())
            {
                var lines = new List<string>
                {
                    $"备份：{Path.GetFileName(undo.BackupPath ?? "（无）")}",
                    $"剩余可撤回：{plugin.Config.UndoHistory.Count} 步",
                };
                lines.AddRange(undo.Entries.Take(3).Select(x => "包含：" + UiHelpers.Shorten(x.URL, 60)));
                ImGui.SetTooltip(string.Join('\n', lines));
            }
        }

        PluginListLayout.ToolbarSameLineOrWrap(backupWidth);
        if (ImGui.Button("打开备份目录###OpenBackups")) plugin.OpenBackupDirectory();
    }

    private void DisableSelected()
    {
        var live = plugin.Repos.ReadAll(out var error);
        if (error is not null)
        {
            SetStatus("读取仓库列表失败：" + error, true);
            return;
        }

        var liveMap = live.ToDictionary(x => x.URL, x => x, StringComparer.Ordinal);
        var record = new UndoRecord { Action = "disable", TimeUTC = DateTime.UtcNow };
        var targets = new List<string>();

        foreach (var url in selected)
        {
            if (liveMap.TryGetValue(url, out var entry))
            {
                targets.Add(url);
                if (entry.IsEnabled)
                {
                    record.Entries.Add(new UndoEntry { URL = url, IsEnabled = true, Index = entry.Index });
                }
            }
        }

        if (record.Entries.Count == 0)
        {
            SetStatus("所选仓库都已经处于停用状态。", false);
            return;
        }

        var changed = plugin.Repos.SetEnabled(targets, false, out error);
        if (error is not null)
        {
            SetStatus("停用失败：" + error, true);
            ActivityLog.Warning("仓库体检", $"停用 {targets.Count} 个库失败：{error}");
            return;
        }

        plugin.RecordUndo(record);
        plugin.Repos.Save(out _);
        plugin.Repos.TriggerReload(out _);

        SetStatus($"已停用 {changed} 个仓库（{DateTime.Now:HH:mm}），链接保留，可撤回。", false);
        ActivityLog.Info("仓库体检", $"已停用 {changed} 个仓库：{string.Join("、", targets.Take(3))}{(targets.Count > 3 ? " 等" : string.Empty)}");
        RefreshFromLive();
    }

    private void DeleteSelected()
    {
        var backup = plugin.Repos.BackupRepos(out var error);
        if (string.IsNullOrEmpty(backup))
        {
            SetStatus("备份失败，已取消删除：" + error, true);
            ActivityLog.Warning("仓库体检", "删除前备份失败，已取消：" + error);
            return;
        }

        var live = plugin.Repos.ReadAll(out error);
        if (error is not null)
        {
            SetStatus("读取仓库列表失败：" + error, true);
            return;
        }

        var liveMap = live.ToDictionary(x => x.URL, x => x, StringComparer.Ordinal);
        var record = new UndoRecord
        {
            Action = "delete",
            TimeUTC = DateTime.UtcNow,
            BackupPath = backup,
        };

        foreach (var url in selected)
        {
            if (liveMap.TryGetValue(url, out var entry))
            {
                record.Entries.Add(new UndoEntry { URL = url, IsEnabled = entry.IsEnabled, Index = entry.Index });
            }
        }

        if (record.Entries.Count == 0)
        {
            SetStatus("所选仓库已不在列表中。", false);
            return;
        }

        var removed = plugin.Repos.Remove(record.Entries.Select(x => x.URL), out error);
        if (error is not null)
        {
            SetStatus("删除失败：" + error, true);
            ActivityLog.Warning("仓库体检", $"删除 {record.Entries.Count} 个库失败：{error}");
            return;
        }

        plugin.RecordUndo(record);
        plugin.Repos.Save(out _);
        plugin.Repos.TriggerReload(out _);

        statusMessage =
            $"已删除 {removed} 个链接（{DateTime.Now:HH:mm}），备份：{Path.GetFileName(backup)}；" +
            "可点「撤回」把链接放回原位置。";
        statusIsError = false;
        ActivityLog.Info("仓库体检", $"已删除 {removed} 个库链接，备份 {Path.GetFileName(backup)}");
        RefreshFromLive();
    }

    /// <summary>
    ///     写状态行（并推进世代号：图标检查的后台结果只在没人动过状态行时才回写）。
    /// </summary>
    private void SetStatus(string? message, bool isError)
    {
        statusVersion++;
        statusMessage = message;
        statusIsError = isError;
        iconMissingNames = null;   // 名单只跟随「检查缺图标」那一条
    }

    private void DrawDeleteConfirmPopup()
    {
        if (deleteRequested)
        {
            ImGui.OpenPopup("确认删除仓库链接###DeleteConfirm");
            deleteRequested = false;
        }

        FireGazeWindowPlacement.PrepareModal(new Vector2(600, 420));
        if (!ImGui.BeginPopupModal("确认删除仓库链接###DeleteConfirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        int count, unreachable;
        int noInstalled, withInstalled, withInstalledTotal;
        bool indexAvailable;
        List<RepoAuditItem> preview;
        lock (gate)
        {
            count = selected.Count;
            unreachable = items.Count(x => selected.Contains(x.URL) && x.Status == RepoStatus.Unreachable);
            indexAvailable = installedIndex is { Available: true };
            var chosen = items.Where(x => selected.Contains(x.URL)).ToList();
            noInstalled = chosen.Count(x => x.InstalledCount == 0);
            withInstalled = chosen.Count(x => x.InstalledCount > 0);
            withInstalledTotal = chosen.Where(x => x.InstalledCount > 0).Sum(x => x.InstalledCount);
            preview = items
                .Where(x => selected.Contains(x.URL))
                .OrderBy(x => UiHelpers.SeverityRank(x.Status))
                .Take(5)
                .ToList();
        }

        ImGui.TextWrapped($"删除所选的 {count} 个仓库链接？");
        ImGui.Spacing();

        foreach (var item in preview)
        {
            ImGui.TextDisabled("· " + UiHelpers.Shorten(item.URL, 64));
        }

        if (count > preview.Count)
        {
            ImGui.TextDisabled($"…… 等共 {count} 条");
        }

        if (unreachable > 0)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Warn, $"注意：其中 {unreachable} 条是「连接失败」——可能是网络问题而不是死链。");
        }

        // 「没在用」不等于「可以删」；反过来，删掉有插件的库会让那些插件失去更新来源
        if (!indexAvailable)
        {
            UiHelpers.ColoredWrapped(
                UiHelpers.Warn,
                "本次读不到已装插件列表，无法判断这些库里有没有你在用的插件。");
        }
        else
        {
            if (noInstalled > 0)
            {
                UiHelpers.ColoredWrapped(
                    UiHelpers.Muted,
                    $"本机未从其中 {noInstalled} 个仓库安装插件。它们仍可能是备用源，请确认后再删除。");
            }

            if (withInstalled > 0)
            {
                UiHelpers.ColoredWrapped(
                    UiHelpers.Warn,
                    $"其中 {withInstalled} 个库上有 {withInstalledTotal} 个已装插件：删除库链不会卸载它们，但从此不再有更新来源。");
            }
        }

        ImGui.Spacing();
        ImGui.TextWrapped("删除前会自动备份，之后可点「撤回」恢复。");
        ImGui.TextWrapped("如需保留链接，请改用「停用」。");
        ImGui.Spacing();

        // 破坏性提交：红系实底，与旁边蓝色的「取消」拉开（跨页规则）
        UiHelpers.PushDangerButton();
        if (ImGui.Button("确认删除", new Vector2(120, 0)))
        {
            ImGui.CloseCurrentPopup();
            DeleteSelected();
        }

        UiHelpers.PopDangerButton();

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(120, 0)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
