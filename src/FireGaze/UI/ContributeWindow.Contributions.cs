using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

internal sealed partial class ContributeWindow : Window
{
    /// <summary>
    ///     投稿中继（Cloudflare Worker）：一键提交直接把这一批发出去，不用开浏览器、玩家不需要 GitHub 账号。
    ///     只有中继确认建好 issue 才会归档清空；中继不可用时回退到 GitHub 提交页。
    /// </summary>
    private const string RelayURL = "https://firegaze-relay.yuoonmail.workers.dev/";

    /// <summary>
    ///     中继/issue 正文上限内的安全余量（GitHub issue 正文上限 65536）。
    /// </summary>
    private const int RelayMaxBody = 60000;

    private static readonly HttpClient RelayClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    ///     中继提交在途中：拦住重复点击。
    /// </summary>
    private bool submitting;

    private void RecallSelectedHistory() => RecallRecords(SelectedHistoryRecords());

    /// <summary>
    ///     把留档里的记录放回「待提交」（是移动：留档里不再保留），并写回词表。
    /// </summary>
    private void RecallRecords(IReadOnlyList<ContributionRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        // 记下「现在的值」：以后删掉这条草稿时会恢复成加回来之前的样子
        foreach (var record in records)
        {
            var (text, source) = plugin.Table.GetTranslation(record.InternalName, record.Field);
            record.Previous = text;
            record.PreviousSource = source;
            record.TimeLocal = DateTime.Now;
        }

        var recalled = store.RecallFromHistory(records);
        selectedHistory.Clear();

        foreach (var record in recalled)
        {
            plugin.Table.MarkUserTranslation(record.InternalName, record.Field, record.Original, record.Translated);
        }

        if (recalled.Count > 0)
        {
            plugin.Table.SaveToConfigDirectory(out _);
            plugin.ApplyTranslations();
        }

        RefreshIndexSoon();
        SetStatus(
            recalled.Count == 0
                ? "这几条已经不在历史提交里了"
                : $"已把 {recalled.Count} 条放回「待提交」；历史提交里不再保留这几条",
            recalled.Count == 0);
    }

    private void DeleteSelectedHistory()
    {
        var removed = store.RemoveFromHistory(SelectedHistoryRecords());
        selectedHistory.Clear();
        SetStatus($"已从历史提交里删掉 {removed} 条（这台机器上的记录；已交出去的译文不受影响）", isError: false);
    }

    private void ClearHistoryRecords()
    {
        var count = store.ClearHistory();
        selectedHistory.Clear();
        SetStatus($"已清空历史提交（{count} 条，这台机器上的记录）", isError: false);
    }

    private List<ContributionRecord> SelectedHistoryRecords()
    {
        var result = new List<ContributionRecord>();
        foreach (var batch in store.History)
        {
            foreach (var record in batch.Contributions)
            {
                if (selectedHistory.Contains(record.ID))
                {
                    result.Add(record);
                }
            }
        }

        return result;
    }

    private static string RecordKey(ContributionRecord record) => record.InternalName + ":" + record.Field;

    /// <summary>
    ///     从下面那一栏打开已有译文：直接进原来的那份改，不新建。
    /// </summary>
    private void EditExistingContribution(ContributionRecord record)
    {
        var entry = index?.All.FirstOrDefault(
            x => string.Equals(x.InternalName, record.InternalName, StringComparison.Ordinal));
        if (entry is null)
        {
            SetStatus($"{record.DisplayName} 现在不在你的插件库里，只能删掉这条", isError: true);
            return;
        }

        BeginEdit(entry);
    }

    private void DeleteSelectedContributions()
    {
        var deleted = 0;
        foreach (var key in selectedRecords.ToList())
        {
            var separator = key.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var internalName = key[..separator];
            var field = key[(separator + 1)..];
            var record = store.Remove(internalName, field);
            if (record is null)
            {
                continue;
            }

            plugin.Table.SetTranslation(internalName, field, record.Previous, record.PreviousSource);
            deleted++;
        }

        selectedRecords.Clear();
        if (deleted > 0)
        {
            plugin.Table.SaveToConfigDirectory(out _);
            plugin.ApplyTranslations();
            InvalidateIndex();
        }

        SetStatus($"已删除 {deleted} 条待提交译文；词表已恢复成改之前的样子（可用「撤回」找回）", isError: false);
    }

    private void ClearContributions()
    {
        var count = store.Count;
        foreach (var record in store.Records.ToList())
        {
            plugin.Table.SetTranslation(record.InternalName, record.Field, record.Previous, record.PreviousSource);
        }

        store.ClearAll();
        selectedRecords.Clear();
        plugin.Table.SaveToConfigDirectory(out _);
        plugin.ApplyTranslations();
        InvalidateIndex();
        SetStatus($"已清空 {count} 条待提交译文（可用「撤回」找回）", isError: false);
    }

    private void UndoContributions()
    {
        var restored = store.Undo(out var message);
        foreach (var record in restored)
        {
            plugin.Table.MarkUserTranslation(record.InternalName, record.Field, record.Original, record.Translated);
        }

        if (restored.Count > 0)
        {
            plugin.Table.SaveToConfigDirectory(out _);
            plugin.ApplyTranslations();
        }

        RefreshIndexSoon();
        SetStatus(message, restored.Count == 0);
    }

    /// <summary>
    ///     一键提交（第一步）：把这一批填进 GitHub 的「新建 issue」页并打开。
    ///     插件**不直接给维护者手机发消息**（那需要把推送密钥内嵌进插件，等于发给所有人）；
    ///     提交进 GitHub 后由仓库的工作流用 secret 里的地址通知维护者（审查 P0-2）。
    ///     两步式：玩家在网页按过 Submit，回来点「确认已提交」才归档 + 清空。
    /// </summary>
    private void SubmitContributions()
    {
        if (store.Count == 0 || submitting)
        {
            return;
        }

        var count = store.Count;
        var title = $"翻译贡献 {DateTime.Now:yyyy-MM-dd HH:mm}（{count} 条）";
        var body = store.BuildMarkdown();

        // 优先走中继：一步提交（成功才归档清空）
        if (body.Length <= RelayMaxBody)
        {
            submitting = true;
            SetStatus($"正在提交 {count} 条…", isError: false);

            _ = Task.Run(async () =>
            {
                var (ok, message) = await TryRelaySubmitAsync(title, body).ConfigureAwait(false);
                try
                {
                    await Plugin.Framework.RunOnFrameworkThread(() => OnRelaySubmitDone(ok, message, title, count));
                }
                catch (Exception e)
                {
                    Plugin.Log.Warning(e, "[FireGaze] 中继提交回调调度失败");
                }
            });
            return;
        }

        // 这一批大到中继收不下：走原来的「打开 GitHub 提交页」（自带截断与附件提示）
        OpenIssueSubmitPage(count, title, body);
    }

    /// <summary>
    ///     回退路径：打开填好的 GitHub 新建 issue 页（玩家在网页上按 Submit，回来确认）。
    /// </summary>
    private void OpenIssueSubmitPage(int count, string title, string fullBody)
    {
        try
        {
            var body = fullBody;

            if (body.Length > 6000)
            {
                // 正文太长：只发摘要 + 完整 JSON 落盘（玩家把文件拖进 issue 即可）
                var path = store.SaveExportFile();
                body = $"### FireGaze 翻译贡献\n\n- 条数：{count}\n- 完整清单见附件\n\n"
                       + string.Join("\n", store.Records.Take(20)
                           .Select(r => $"- {r.InternalName} / {r.Field}：{r.Translated.Replace('\n', ' ')}"));
                extraHint = path is null
                    ? "这一批太大，完整内容请用「导出」备份后当附件提交。"
                    : $"这一批太大，完整内容已导出到 {path}：在网页上把它拖进附件再提交。";
            }

            var url = $"{ContributionsStore.RepoURL}/issues/new"
                      + $"?title={Uri.EscapeDataString(title)}"
                      + $"&body={Uri.EscapeDataString(body)}";

            if (url.Length > 20000)
            {
                var path = store.SaveExportFile();
                extraHint = path is null
                    ? "这一批太大，先在网页上提交一个空 issue，再把导出的 JSON 当附件补上。"
                    : $"这一批太大，完整内容已导出到 {path}：先提交一个空 issue，再把它拖进附件。";
                url = $"{ContributionsStore.RepoURL}/issues/new";
            }

            Dalamud.Utility.Util.OpenLink(url);
            awaitingConfirm = true;
            SetStatus($"已打开 GitHub 提交页（{count} 条）：在网页上按 Submit，回来点「确认已提交」", isError: false);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "[FireGaze] 打开 GitHub 提交页失败");
            var path = store.SaveExportFile();
            SetStatus(
                path is null
                    ? $"没打开提交页（{e.GetType().Name}）；可以先用「导出」备份这一批"
                    : $"没打开提交页（{e.GetType().Name}）；已导出到 {path}，可以手工发到 GitHub issue",
                isError: true);
        }
    }

    /// <summary>
    ///     中继结果回到绘制线程：成功才归档清空；失败回退到 GitHub 提交页（绝不谎报提交成功）。
    /// </summary>
    private void OnRelaySubmitDone(bool ok, string message, string title, int count)
    {
        submitting = false;

        if (ok)
        {
            store.ArchiveSubmission();
            workspaceTab = 0;   // 切到「历史提交」，让玩家看到刚才那一批
            selectedRecords.Clear();
            SetStatus($"已提交 {count} 条：维护者已收到通知，收录后会进词表", isError: false);
            return;
        }

        SetStatus($"中继没成功（{message}），已改为打开 GitHub 提交页", isError: true);
        OpenIssueSubmitPage(count, title, store.BuildMarkdown());
    }

    /// <summary>
    ///     把投稿发给中继；返回 (是否成功, 失败原因或 issue 地址)。
    /// </summary>
    private static async Task<(bool Ok, string Message)> TryRelaySubmitAsync(string title, string body)
    {
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(new { title, body }),
                Encoding.UTF8,
                "application/json");

            using var response = await RelayClient.PostAsync(RelayURL, content).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(text);
            var ok = doc.RootElement.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.True;
            if (ok)
            {
                var issue = doc.RootElement.TryGetProperty("issue", out var issueProp)
                    ? issueProp.GetString() ?? string.Empty
                    : string.Empty;
                return (true, issue);
            }

            var error = doc.RootElement.TryGetProperty("error", out var errProp)
                ? errProp.GetString() ?? "unknown"
                : "unknown";
            return (false, error);
        }
        catch (Exception e)
        {
            return (false, e.GetType().Name);
        }
    }

    /// <summary>
    ///     一键提交（第二步）：玩家在网页按过 Submit 之后才归档 + 清空。
    /// </summary>
    private void ConfirmSubmitted()
    {
        if (store.Count == 0)
        {
            awaitingConfirm = false;
            return;
        }

        var count = store.Count;
        store.ArchiveSubmission();
        workspaceTab = 0;   // 切到「历史提交」，让玩家看到刚才那一批
        selectedRecords.Clear();
        awaitingConfirm = false;
        SetStatus($"已提交 {count} 条：GitHub 上那条 issue 里能看到，维护者收录后会进词表", isError: false);
    }
}
