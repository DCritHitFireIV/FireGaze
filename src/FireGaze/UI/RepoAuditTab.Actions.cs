using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

internal sealed partial class RepoAuditTab
{
    /// <summary>
    ///     操作工具条：第一行是选择类动作（停用 │ 删除… + 全选/清空），删除用红色并与停用拉开距离；
    ///     第二行左侧是动态选择摘要，右侧是恢复类动作（撤回 / 备份目录，永远渲染）。
    /// </summary>
    private void DrawActionBar(int selectedCount, int selectedHidden, List<RepoAuditItem> snapshot)
    {
        var canAct = !scanning && selectedCount > 0;
        var filteredCount = snapshot.Count;

        if (!canAct)
        {
            ImGui.BeginDisabled();
        }

        ImGui.Button($"停用所选（{selectedCount}）###DisableSelected");
        if (ImGui.IsItemClicked() && canAct)
        {
            DisableSelected();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("保留链接、只是不再加载这些库（可随时撤回）");
        }

        if (!canAct)
        {
            ImGui.EndDisabled();
        }

        // 危险动作：与「停用」拉开间距 + 红色 + 独立分组
        ImGui.SameLine();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.18f, 0.18f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.68f, 0.24f, 0.24f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.76f, 0.28f, 0.28f, 1f));

        if (!canAct)
        {
            ImGui.BeginDisabled();
        }

        ImGui.Button($"删除所选（{selectedCount}）…###DeleteSelected");
        if (ImGui.IsItemClicked() && canAct)
        {
            deleteRequested = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("从仓库列表移除（会先自动备份，之后可撤回）");
        }

        if (!canAct)
        {
            ImGui.EndDisabled();
        }

        ImGui.PopStyleColor(3);

        ImGui.SameLine();
        ImGui.TextDisabled("│");
        ImGui.SameLine();

        var canSelect = !scanning;
        if (!canSelect)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"全选当前（{filteredCount}）###SelectFiltered"))
        {
            lock (gate)
            {
                foreach (var item in snapshot.Where(x => x.IsSelectable))
                {
                    selected.Add(item.URL);
                }
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("只勾选当前筛选/搜索结果显示的行");
        }

        ImGui.SameLine();
        if (ImGui.Button("清空选择###ClearSelection"))
        {
            lock (gate)
            {
                selected.Clear();
            }
        }

        if (!canSelect)
        {
            ImGui.EndDisabled();
        }

        // ---------------- 第二行：选择摘要 + 恢复动作 ----------------
        var undo = plugin.Config.UndoHistory.Count > 0 ? plugin.Config.UndoHistory[^1] : null;

        var hint = selectedCount == 0
            ? $"结果 {snapshot.Count} 行 · 已选 0 —— 勾选列表行，或用「全选当前」"
            : $"结果 {snapshot.Count} 行 · 已选 {selectedCount}（共 {ProblemCount()} 个问题项"
              + (selectedHidden > 0 ? $"，其中 {selectedHidden} 项不在当前筛选内）" : "）");

        // 非默认排序才显示（这一行常驻内容多，避免把右侧的恢复类按钮挤下去）
        var sortText = sortKey switch
        {
            "installed" => sortDescending ? "按已安装 ↓" : "按已安装 ↑",
            "firstSeen" => sortDescending ? "按首次记录 ↓" : "按首次记录 ↑",
            "url" => sortDescending ? "按地址 ↓" : "按地址 ↑",
            _ => null,
        };

        if (sortText is not null)
        {
            hint += $" · {sortText}";
        }

        ImGui.TextDisabled(hint);
        var hintWidth = ImGui.GetItemRectSize().X;

        // 靠右摆放恢复类动作（SameLine 的参数是「距行首的绝对偏移」，不是剩余宽度）
        var undoLabel = undo is null ? "撤回上次操作###Undo" : $"撤回：{undo.Describe()}###Undo";
        var undoWidth = ImGui.CalcTextSize(undoLabel.Replace("###Undo", string.Empty)).X + 24;
        var backupWidth = ImGui.CalcTextSize("打开备份目录").X + 24;
        var totalWidth = undoWidth + backupWidth + 12;
        var maxX = ImGui.GetContentRegionMax().X;
        var targetX = maxX - totalWidth;

        if (targetX < hintWidth + 12)
        {
            // 一行放不下（长提示 + 长撤回标签）：按钮换到下一行，仍然右对齐
            ImGui.NewLine();
            targetX = MathF.Max(4f, maxX - totalWidth);
        }

        ImGui.SameLine(targetX);

        if (undo is null)
        {
            ImGui.BeginDisabled();
            ImGui.Button("撤回上次操作###Undo");
            ImGui.EndDisabled();
        }
        else
        {
            if (ImGui.Button(undoLabel))
            {
                var ok = plugin.TryUndoLast(out var message);
                SetStatus(ok ? message : "撤回失败：" + message, !ok);
                RefreshFromLive();
            }

            if (ImGui.IsItemHovered())
            {
                var lines = new List<string>
                {
                    $"备份：{Path.GetFileName(undo.BackupPath ?? "（无）")}",
                    $"剩余可撤回：{plugin.Config.UndoHistory.Count} 步",
                };
                lines.AddRange(undo.Entries.Take(3).Select(x => "样本：" + UiHelpers.Shorten(x.URL, 60)));
                ImGui.SetTooltip(string.Join('\n', lines));
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("打开备份目录###OpenBackups"))
        {
            plugin.OpenBackupDirectory();
        }
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
            return;
        }

        plugin.RecordUndo(record);
        plugin.Repos.Save(out _);
        plugin.Repos.TriggerReload(out _);

        SetStatus($"已停用 {changed} 个仓库（{DateTime.Now:HH:mm}）—— 链接保留、不再加载；可点「撤回」恢复。", false);
        RefreshFromLive();
    }

    private void DeleteSelected()
    {
        var backup = plugin.Repos.BackupRepos(out var error);
        if (string.IsNullOrEmpty(backup))
        {
            SetStatus("备份失败，已取消删除：" + error, true);
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
            SetStatus("所选的仓库已经不在列表里了。", false);
            return;
        }

        var removed = plugin.Repos.Remove(record.Entries.Select(x => x.URL), out error);
        if (error is not null)
        {
            SetStatus("删除失败：" + error, true);
            return;
        }

        plugin.RecordUndo(record);
        plugin.Repos.Save(out _);
        plugin.Repos.TriggerReload(out _);

        statusMessage =
            $"已删除 {removed} 个链接（{DateTime.Now:HH:mm}），备份：{Path.GetFileName(backup)}；" +
            "可点「撤回」把链接放回原位置。";
        statusIsError = false;
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

        ImGui.TextWrapped($"你确定要从仓库列表里删除这 {count} 个库链接吗？");
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
                    $"其中 {noInstalled} 条没从它装过插件——不代表这个库没用：可能你在别的机器装过、它只是备用源、或插件是手动/开发版装的。");
            }

            if (withInstalled > 0)
            {
                UiHelpers.ColoredWrapped(
                    UiHelpers.Warn,
                    $"其中 {withInstalled} 个库上有 {withInstalledTotal} 个已装插件：删除库链不会卸载它们，但从此不再有更新来源。");
            }
        }

        ImGui.Spacing();
        ImGui.TextWrapped("删除前会自动备份（完整仓库列表 + dalamudConfig.json），删除后可以随时点「撤回」恢复。");
        ImGui.TextWrapped("想保留链接、只是不想加载的话，建议改用「停用」。");
        ImGui.Spacing();

        if (ImGui.Button("确认删除", new Vector2(120, 0)))
        {
            ImGui.CloseCurrentPopup();
            DeleteSelected();
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(120, 0)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
