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
    private void BeginEdit(TranslationIndexEntry entry)
    {
        editingBatch.Clear();
        ApplyEditing(entry);
    }

    private void ApplyEditing(TranslationIndexEntry entry)
    {
        editing = entry;
        editOpened = false;

        // 下面那一栏已经有这条译文时，直接进原来那份改（不新建、不堆历史版本）
        editName = Pending(entry, "Name") ?? entry.Entry?.Name?.Translated ?? string.Empty;
        editPunchline = Pending(entry, "Punchline") ?? entry.Entry?.Punchline?.Translated ?? string.Empty;
        editDescription = Pending(entry, "Description") ?? entry.Entry?.Description?.Translated ?? string.Empty;
    }

    /// <summary>
    ///     下栏（待提交）里这条字段的译文；没有就是 null。
    /// </summary>
    private string? Pending(TranslationIndexEntry entry, string field)
        => store.Find(entry.InternalName, field)?.Translated;

    /// <summary>
    ///     批量翻译：把勾选的插件排成一列，改完一个自动接下一个。
    /// </summary>
    private void BeginBatchEdit()
    {
        var queue = index?.All.Where(x => selected.Contains(x.InternalName)).ToList() ?? [];
        if (queue.Count == 0)
        {
            return;
        }

        editingBatch = queue;
        ApplyEditing(queue[0]);
    }

    /// <summary>
    ///     批量模式下切到下一个（没有下一个就关掉弹窗）。
    /// </summary>
    private void AdvanceBatch()
    {
        var current = editing;
        if (current is null || editingBatch.Count == 0)
        {
            editing = null;
            ImGui.CloseCurrentPopup();
            return;
        }

        var index = editingBatch.IndexOf(current);
        if (index < 0 || index + 1 >= editingBatch.Count)
        {
            editing = null;
            editingBatch.Clear();
            ImGui.CloseCurrentPopup();
            return;
        }

        ApplyEditing(editingBatch[index + 1]);
    }

    private void DrawEditPopup()
    {
        if (editing is null)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(700, 560), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("翻译###EditTranslation", ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        var entry = editing;
        ImGui.Text(entry.DisplayName);
        ImGui.SameLine();
        ImGui.TextDisabled(entry.InternalName);
        if (entry.IsOfficial)
        {
            ImGui.SameLine();
            UiHelpers.ColoredText(UiHelpers.Info, "官方库");
        }
        else if (entry.RepositoryURL is not null)
        {
            ImGui.SameLine();
            UiHelpers.Fitted(entry.RepositoryURL, entry.RepositoryURL);
        }

        if (editingBatch.Count > 0)
        {
            var done = editingBatch.IndexOf(entry) + 1;
            ImGui.SameLine();
            ImGui.TextDisabled($"· 批量 {done}/{editingBatch.Count}");
        }

        ImGui.Separator();

        DrawField("插件名", "Name", entry.OriginalName, ref editName);
        DrawField("一行简介", "Punchline", entry.OriginalPunchline, ref editPunchline);
        DrawField("插件详情", "Description", entry.OriginalDescription, ref editDescription);

        ImGui.Spacing();
        ImGui.TextDisabled("提交的译文优先于机器翻译，之后不会被覆盖；改完先存到本地。");
        if (entry.ContributableFields.Count > 0)
        {
            ImGui.TextDisabled(
                "这个插件上游没提供：" + string.Join("、", entry.ContributableFields.Select(FieldLabel))
                + "，不算缺译，你也可以自己补一份。");
        }

        ImGui.Separator();
        if (ImGui.Button("保存###SaveEdit", new Vector2(120, 0)))
        {
            SaveEdit();
        }

        if (editingBatch.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("跳过这个###SkipEdit", new Vector2(120, 0)))
            {
                AdvanceBatch();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("取消###CancelEdit", new Vector2(120, 0)))
        {
            editing = null;
            editingBatch.Clear();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"待提交 {store.Count} 条");

        ImGui.EndPopup();
    }

    private void DrawField(string label, string field, string original, ref string value)
    {
        var upstream = !string.IsNullOrWhiteSpace(original);
        if (!upstream && string.IsNullOrWhiteSpace(value))
        {
            // 上游没提供、玩家也还没写：不占空间
            return;
        }

        ImGui.Spacing();
        ImGui.Text(label);
        if (!upstream)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("上游没提供");
        }

        ImGui.SameLine();
        if (upstream && value != original)
        {
            if (ImGui.SmallButton("与原文相同###Reset" + field))
            {
                value = original;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("把这一栏填成原文（不希望被翻译时用）");
            }
        }

        if (upstream)
        {
            ImGui.TextDisabled("原文");
            ImGui.SetNextItemWidth(-1);
            var readOnly = original;
            ImGui.InputTextMultiline(
                "###orig-" + field,
                ref readOnly,
                4096,
                new Vector2(0, ImGui.GetTextLineHeight() * 4),
                ImGuiInputTextFlags.ReadOnly);
        }

        ImGui.TextDisabled(upstream ? "译文" : "你补的内容");
        ImGui.SameLine();
        var limit = ContributionRules.LimitOf(field);
        var used = (value ?? string.Empty).Length;
        UiHelpers.ColoredText(used > limit ? UiHelpers.Bad : UiHelpers.Muted, $"{used}/{limit} 字");
        if (used > limit && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"超过上限（{limit} 字）提交时会被退回，先精简一下");
        }

        ImGui.SetNextItemWidth(-1);
        var text = value ?? string.Empty;
        ImGui.InputTextMultiline(
            "###trans-" + field,
            ref text,
            4096,
            new Vector2(0, ImGui.GetTextLineHeight() * 4));
        value = text;
    }

    private void SaveEdit()
    {
        var entry = editing;
        if (entry is null)
        {
            return;
        }

        var changed = 0;
        ruleIssue = null;
        changed += Commit(entry, "Name", entry.OriginalName, editName);
        changed += Commit(entry, "Punchline", entry.OriginalPunchline, editPunchline);
        changed += Commit(entry, "Description", entry.OriginalDescription, editDescription);

        if (ruleIssue is not null)
        {
            SetStatus($"有字段没存上（规则拦下）：{ruleIssue}", isError: true);
        }
        else if (changed > 0)
        {
            plugin.Table.SaveToConfigDirectory(out var error);
            plugin.ApplyTranslations();
            RefreshIndexSoon();
            SetStatus(
                error is null
                    ? $"已存到本地 {changed} 处译文；攒够后在下面一条提交"
                    : $"已记下 {changed} 处译文，但写盘失败：{error}",
                error is not null);
        }
        else
        {
            SetStatus("没有改动", isError: false);
        }

        if (editingBatch.Count > 0)
        {
            AdvanceBatch();
            return;
        }

        editing = null;
        ImGui.CloseCurrentPopup();
    }

    private int Commit(TranslationIndexEntry entry, string field, string original, string value)
    {
        var upstream = !string.IsNullOrWhiteSpace(original);
        var next = value ?? string.Empty;

        // 与 GitHub 那边同一套规则，先在这里拦一次：超长 / 网址 / HTML / 控制字符
        var reason = ContributionRules.Check(field, next);
        if (reason is not null)
        {
            ruleIssue = $"{FieldLabel(field)}：{reason}";
            return 0;
        }
        if (!upstream && string.IsNullOrWhiteSpace(next))
        {
            return 0;   // 上游没有、玩家也没写：没什么可提交的
        }

        if (upstream && string.Equals(original, next, StringComparison.Ordinal))
        {
            return 0;   // 原样搬一遍不算改动
        }

        var current = entry.Entry is null
            ? string.Empty
            : field switch
            {
                "Name" => entry.Entry.Name?.Translated ?? string.Empty,
                "Punchline" => entry.Entry.Punchline?.Translated ?? string.Empty,
                _ => entry.Entry.Description?.Translated ?? string.Empty,
            };

        if (string.Equals(current, next, StringComparison.Ordinal))
        {
            return 0;
        }

        // 记下「改之前是什么」：删掉这条贡献时要用它恢复
        var (previousText, previousSource) = plugin.Table.GetTranslation(entry.InternalName, field);
        var existing = store.Find(entry.InternalName, field);

        plugin.Table.MarkUserTranslation(entry.InternalName, field, original, next);
        store.AddOrReplace(new ContributionRecord
        {
            ID = $"{entry.InternalName}:{field}:{DateTime.Now:yyyyMMddHHmmss}",
            TimeLocal = DateTime.Now,
            InternalName = entry.InternalName,
            DisplayName = entry.DisplayName,
            Field = field,
            Original = original,
            Translated = next,
            Previous = existing?.Previous ?? previousText,
            PreviousSource = existing?.PreviousSource ?? previousSource,
            Stale = entry.HasReview,
        });

        return 1;
    }
}
