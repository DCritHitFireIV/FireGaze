using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>「仓库体检」页：扫描全部第三方仓库 → 展示问题 → 停用 / 删除（带备份与撤回）。</summary>
internal sealed class RepoAuditTab
{
    private readonly Plugin plugin;
    private readonly object gate = new();
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);

    private List<RepoAuditItem> items = [];
    private bool scanning;
    private CancellationTokenSource? cancellation;
    private int done;
    private int total;
    private int okCount;
    private int deadCount;
    private int invalidCount;
    private int unreachableCount;
    private string? statusMessage;
    private bool statusIsError;
    private string filter = "problems";
    private bool deleteRequested;

    public RepoAuditTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        ImGui.TextWrapped(
            "扫描你添加的全部第三方仓库（包含已停用的）：检查链接还能不能用、内容是不是合法的仓库文件（与卫月同款校验）。");
        ImGui.TextDisabled(
            "内容不合规 = 拿到的不是合法仓库文件：插件安装器无法识别这些条目，可能导致插件列表残缺或排版错乱。");
        ImGui.TextDisabled("死链 / 内容不合规的库可以一键停用，或直接删除；删除前会自动备份，删除后可以随时撤回。");

        ImGui.Spacing();

        // ---------------- 扫描控制 ----------------
        if (this.scanning)
        {
            if (ImGui.Button("取消扫描###CancelScan"))
            {
                try
                {
                    this.cancellation?.Cancel();
                }
                catch
                {
                    // ignore
                }
            }
        }
        else
        {
            if (ImGui.Button("开始体检###StartScan"))
            {
                this.StartScan();
            }
        }

        ImGui.SameLine();
        var includeDisabled = this.plugin.Config.ScanIncludeDisabled;
        if (ImGui.Checkbox("包含已停用的库###IncDisabled", ref includeDisabled))
        {
            this.plugin.Config.ScanIncludeDisabled = includeDisabled;
            this.plugin.SaveConfig();
        }

        // ---------------- 进度 / 统计 ----------------
        int d, t, ok, dead, invalid, unreachable;
        bool scan;
        lock (this.gate)
        {
            d = this.done;
            t = this.total;
            ok = this.okCount;
            dead = this.deadCount;
            invalid = this.invalidCount;
            unreachable = this.unreachableCount;
            scan = this.scanning;
        }

        if (scan && t > 0)
        {
            ImGui.ProgressBar((float)d / t, new Vector2(220, 0), $"{d} / {t}");
            ImGui.SameLine();
            ImGui.TextUnformatted($"可用 {ok} · 死链 {dead} · 内容不合规 {invalid} · 连接失败 {unreachable}");
        }
        else if (t > 0)
        {
            ImGui.TextUnformatted(
                $"共 {t} 个仓库：可用 {ok} · 死链 {dead} · 内容不合规 {invalid} · 连接失败 {unreachable}");
            if (this.plugin.Config.LastScanUtc != default)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"（上次体检：{this.plugin.Config.LastScanUtc.ToLocalTime():yyyy-MM-dd HH:mm}）");
            }
        }
        else
        {
            ImGui.TextDisabled("还没有体检过。点「开始体检」扫描一次。");
        }

        ImGui.Separator();

        // ---------------- 过滤器（选择帮手贴着列表） ----------------
        ImGui.Text("显示");
        this.FilterRadio("problems", "有问题的");
        this.FilterRadio("all", "全部");
        this.FilterRadio("deadinvalid", "死链 + 内容不合规");
        this.FilterRadio("disabled", "已停用");
        this.FilterRadio("ok", "可用");

        ImGui.SameLine();
        ImGui.TextDisabled("│");
        ImGui.SameLine();
        if (ImGui.Button("全选问题项###SelectProblems"))
        {
            lock (this.gate)
            {
                foreach (var item in this.items.Where(x => x.IsProblem))
                {
                    this.selected.Add(item.Url);
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("清空选择###ClearSelection"))
        {
            lock (this.gate)
            {
                this.selected.Clear();
            }
        }

        // ---------------- 操作工具条（列表正上方，始终可见） ----------------
        this.DrawActionBar();

        // ---------------- 状态行 ----------------
        if (!string.IsNullOrEmpty(this.statusMessage))
        {
            UiHelpers.ColoredWrapped(this.statusIsError ? UiHelpers.Bad : UiHelpers.Muted, this.statusMessage);
        }
        else if (!this.scanning)
        {
            ImGui.TextDisabled("提示：删除只把链接从仓库列表里去掉；想留着又不加载，用「停用」即可。");
        }

        // ---------------- 结果表 ----------------
        List<RepoAuditItem> snapshot;
        lock (this.gate)
        {
            snapshot = this.Filtered();
        }

        var tableHeight = MathF.Max(140f, ImGui.GetContentRegionAvail().Y - 8f);
        var tableFlags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                         ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings;

        if (ImGui.BeginTable("###RepoRows", 4, tableFlags, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 118);
            ImGui.TableSetupColumn("仓库地址", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("首次记录", ImGuiTableColumnFlags.WidthFixed, 112);
            ImGui.TableHeadersRow();

            foreach (var item in snapshot)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                var isSelected = this.selected.Contains(item.Url);
                if (ImGui.Checkbox("##sel-" + item.Url, ref isSelected))
                {
                    if (isSelected)
                    {
                        this.selected.Add(item.Url);
                    }
                    else
                    {
                        this.selected.Remove(item.Url);
                    }
                }

                ImGui.TableNextColumn();
                var label = item.StatusText + (item.IsEnabled ? string.Empty : " · 已停用");
                UiHelpers.ColoredText(UiHelpers.StatusColor(item.Status), label);
                if (ImGui.IsItemHovered())
                {
                    var tip = item.Status switch
                    {
                        RepoStatus.Invalid => "内容不合规：插件安装器无法识别这些条目，可能导致插件列表残缺或排版错乱。\n",
                        RepoStatus.Dead => "链接已失效（404 / 410）。\n",
                        RepoStatus.Unreachable => "网络问题（超时 / 证书 / 服务器错误），不一定是死链。\n",
                        RepoStatus.Blocked => "服务器拒绝访问（可能是私有仓库或限流）。\n",
                        _ => string.Empty,
                    };
                    if (!string.IsNullOrEmpty(item.Note) || !string.IsNullOrEmpty(tip))
                    {
                        ImGui.SetTooltip(tip + (item.Note ?? string.Empty));
                    }
                }

                ImGui.TableNextColumn();
                var tooltip = item.Url + (string.IsNullOrEmpty(item.Note) ? string.Empty : "\n" + item.Note);
                if (item.HttpStatus > 0)
                {
                    tooltip += $"\nHTTP {item.HttpStatus}";
                }

                UiHelpers.Truncated(item.Url, 96, tooltip);

                ImGui.TableNextColumn();
                if (string.IsNullOrEmpty(item.FirstSeen))
                {
                    ImGui.TextDisabled("—");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("卫月不记录仓库添加时间。\nFireGaze 只会记录自己第一次看到该链接的时间；\n安装本插件之前就存在的库没有记录。");
                    }
                }
                else
                {
                    ImGui.TextUnformatted(item.FirstSeen);
                }
            }

            ImGui.EndTable();
        }

        this.DrawDeleteConfirmPopup();
    }

    /// <summary>
    /// 操作工具条：选择类操作（停用 / 删除）在前，删除用红色并与安全操作分开；
    /// 恢复类操作（撤回 / 备份）在后，用竖线分隔；无选择时按钮置灰并给出提示。
    /// </summary>
    private void DrawActionBar()
    {
        var selectedCount = this.selected.Count;
        var canAct = !this.scanning && selectedCount > 0;

        if (!canAct)
        {
            ImGui.BeginDisabled();
        }

        ImGui.Button($"停用所选（{selectedCount}）###DisableSelected");
        if (ImGui.IsItemClicked() && canAct)
        {
            this.DisableSelected();
        }

        if (!canAct && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("先在下面的列表里勾选要处理的仓库");
        }

        if (!canAct)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.42f, 0.16f, 0.16f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.56f, 0.21f, 0.21f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.64f, 0.25f, 0.25f, 1f));
        if (!canAct)
        {
            ImGui.BeginDisabled();
        }

        ImGui.Button($"删除所选（{selectedCount}）…###DeleteSelected");
        if (ImGui.IsItemClicked() && canAct)
        {
            this.deleteRequested = true;
        }

        if (!canAct && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("先在下面的列表里勾选要处理的仓库");
        }

        if (!canAct)
        {
            ImGui.EndDisabled();
        }

        ImGui.PopStyleColor(3);

        ImGui.SameLine();
        ImGui.TextDisabled("│");
        ImGui.SameLine();

        var undo = this.plugin.Config.UndoHistory.Count > 0
            ? this.plugin.Config.UndoHistory[^1]
            : null;

        if (undo is null)
        {
            ImGui.BeginDisabled();
            ImGui.Button("撤回上次操作###Undo");
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled("（暂无可撤回的操作）");
        }
        else
        {
            if (ImGui.Button($"撤回上次操作（{undo.Describe()}）###Undo"))
            {
                var ok = this.plugin.TryUndoLast(out var message);
                this.statusMessage = ok ? message : "撤回失败：" + message;
                this.statusIsError = !ok;
                this.RefreshFromLive();
            }

            ImGui.SameLine();
            if (ImGui.Button("打开备份目录###OpenBackups"))
            {
                this.plugin.OpenBackupDirectory();
            }
        }
    }

    private void FilterRadio(string key, string label)
    {
        ImGui.SameLine();
        if (ImGui.RadioButton($"{label}###Filter-{key}", this.filter == key))
        {
            this.filter = key;
        }
    }

    private List<RepoAuditItem> Filtered()
    {
        IEnumerable<RepoAuditItem> query = this.filter switch
        {
            "all" => this.items,
            "deadinvalid" => this.items.Where(x => x.Status is RepoStatus.Dead or RepoStatus.Invalid),
            "disabled" => this.items.Where(x => !x.IsEnabled),
            "ok" => this.items.Where(x => x.Status is RepoStatus.Ok or RepoStatus.Empty),
            _ => this.items.Where(x => x.IsProblem),
        };

        return query.ToList();
    }

    // ------------------------------------------------------------------ 扫描

    private void StartScan()
    {
        if (this.scanning)
        {
            return;
        }

        this.plugin.TrackFirstSeen();

        var repos = this.plugin.Repos.ReadAll(out var error);
        if (error is not null)
        {
            this.statusMessage = "读取仓库列表失败：" + error;
            this.statusIsError = true;
            return;
        }

        var includeDisabled = this.plugin.Config.ScanIncludeDisabled;
        var list = repos
            .Where(x => includeDisabled || x.IsEnabled)
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .Select(x => new RepoAuditItem
            {
                Url = x.Url,
                IsEnabled = x.IsEnabled,
                Index = x.Index,
                FirstSeen = this.plugin.GetFirstSeen(x.Url),
            })
            .ToList();

        if (list.Count == 0)
        {
            this.statusMessage = "仓库列表是空的（或者都被排除了）。";
            this.statusIsError = true;
            return;
        }

        lock (this.gate)
        {
            this.items = list;
            this.selected.Clear();
            this.done = 0;
            this.total = list.Count;
            this.okCount = this.deadCount = this.invalidCount = this.unreachableCount = 0;
            this.scanning = true;
            this.filter = "problems";
        }

        this.statusMessage = $"开始体检 {list.Count} 个仓库…";
        this.statusIsError = false;

        var cts = new CancellationTokenSource();
        this.cancellation = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await RepoScanner.ScanAsync(
                    list,
                    item =>
                    {
                        if (item.IsProblem)
                        {
                            lock (this.gate)
                            {
                                this.selected.Add(item.Url);
                            }
                        }
                    },
                    progress =>
                    {
                        lock (this.gate)
                        {
                            this.done = progress.Done;
                            this.total = progress.Total;
                            this.okCount = progress.Ok;
                            this.deadCount = progress.Dead;
                            this.invalidCount = progress.Invalid;
                            this.unreachableCount = progress.Unreachable;
                        }
                    },
                    cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                this.statusMessage = "扫描已取消。";
                this.statusIsError = false;
            }
            catch (Exception e)
            {
                this.statusMessage = "扫描出错：" + e.Message;
                this.statusIsError = true;
            }
            finally
            {
                int dead, invalid, unreachable;
                lock (this.gate)
                {
                    this.scanning = false;
                    dead = this.deadCount;
                    invalid = this.invalidCount;
                    unreachable = this.unreachableCount;
                }

                if (this.statusMessage?.StartsWith("扫描出错") != true)
                {
                    this.statusMessage =
                        $"体检完成：死链 {dead} · 内容不合规 {invalid} · 连接失败 {unreachable}（问题项已自动勾选）。";
                    this.statusIsError = false;
                }

                this.plugin.Config.LastScanUtc = DateTime.UtcNow;
                foreach (var item in list)
                {
                    item.FirstSeen = this.plugin.GetFirstSeen(item.Url) ?? item.FirstSeen;
                }

                this.plugin.TrackFirstSeen();
                this.plugin.SaveConfig();
            }
        });
    }

    // ------------------------------------------------------------------ 操作

    private void DisableSelected()
    {
        var live = this.plugin.Repos.ReadAll(out var error);
        if (error is not null)
        {
            this.statusMessage = "读取仓库列表失败：" + error;
            this.statusIsError = true;
            return;
        }

        var liveMap = live.ToDictionary(x => x.Url, x => x, StringComparer.Ordinal);
        var record = new UndoRecord { Action = "disable", TimeUtc = DateTime.UtcNow };
        var targets = new List<string>();

        foreach (var url in this.selected)
        {
            if (liveMap.TryGetValue(url, out var entry))
            {
                targets.Add(url);
                if (entry.IsEnabled)
                {
                    record.Entries.Add(new UndoEntry { Url = url, IsEnabled = true, Index = entry.Index });
                }
            }
        }

        if (record.Entries.Count == 0)
        {
            this.statusMessage = "所选仓库都已经处于停用状态。";
            this.statusIsError = false;
            return;
        }

        var changed = this.plugin.Repos.SetEnabled(targets, false, out error);
        if (error is not null)
        {
            this.statusMessage = "停用失败：" + error;
            this.statusIsError = true;
            return;
        }

        this.plugin.RecordUndo(record);
        this.plugin.Repos.Save(out _);
        this.plugin.Repos.TriggerReload(out _);

        this.statusMessage = $"已停用 {changed} 个仓库，配置已保存（随时可以点「撤回上次操作」恢复）。";
        this.statusIsError = false;
        this.RefreshFromLive();
    }

    private void DeleteSelected()
    {
        var backup = this.plugin.Repos.BackupRepos(out var error);
        if (string.IsNullOrEmpty(backup))
        {
            this.statusMessage = "备份失败，已取消删除：" + error;
            this.statusIsError = true;
            return;
        }

        var live = this.plugin.Repos.ReadAll(out error);
        if (error is not null)
        {
            this.statusMessage = "读取仓库列表失败：" + error;
            this.statusIsError = true;
            return;
        }

        var liveMap = live.ToDictionary(x => x.Url, x => x, StringComparer.Ordinal);
        var record = new UndoRecord
        {
            Action = "delete",
            TimeUtc = DateTime.UtcNow,
            BackupPath = backup,
        };

        foreach (var url in this.selected)
        {
            if (liveMap.TryGetValue(url, out var entry))
            {
                record.Entries.Add(new UndoEntry { Url = url, IsEnabled = entry.IsEnabled, Index = entry.Index });
            }
        }

        if (record.Entries.Count == 0)
        {
            this.statusMessage = "所选的仓库已经不在列表里了。";
            this.statusIsError = false;
            return;
        }

        var removed = this.plugin.Repos.Remove(record.Entries.Select(x => x.Url), out error);
        if (error is not null)
        {
            this.statusMessage = "删除失败：" + error;
            this.statusIsError = true;
            return;
        }

        this.plugin.RecordUndo(record);
        this.plugin.Repos.Save(out _);
        this.plugin.Repos.TriggerReload(out _);

        this.statusMessage =
            $"已删除 {removed} 个仓库链接（备份：{Path.GetFileName(backup)}）。" +
            "可以点「撤回上次操作」把链接放回原位置。";
        this.statusIsError = false;
        this.RefreshFromLive();
    }

    /// <summary>操作之后按当前配置重建列表，保留还没被删掉的条目的扫描结果。</summary>
    private void RefreshFromLive()
    {
        var live = this.plugin.Repos.ReadAll(out _);
        var liveUrls = new HashSet<string>(live.Select(x => x.Url), StringComparer.Ordinal);

        lock (this.gate)
        {
            var old = this.items.ToDictionary(x => x.Url, x => x, StringComparer.Ordinal);
            var list = new List<RepoAuditItem>(live.Count);

            foreach (var entry in live)
            {
                if (old.TryGetValue(entry.Url, out var item))
                {
                    item.IsEnabled = entry.IsEnabled;
                    item.Index = entry.Index;
                    list.Add(item);
                }
                else
                {
                    list.Add(new RepoAuditItem
                    {
                        Url = entry.Url,
                        IsEnabled = entry.IsEnabled,
                        Index = entry.Index,
                        FirstSeen = this.plugin.GetFirstSeen(entry.Url),
                    });
                }
            }

            this.items = list;
            this.selected.RemoveWhere(url => !liveUrls.Contains(url));
            this.total = list.Count;
        }
    }

    private void DrawDeleteConfirmPopup()
    {
        if (this.deleteRequested)
        {
            ImGui.OpenPopup("确认删除仓库链接###DeleteConfirm");
            this.deleteRequested = false;
        }

        if (!ImGui.BeginPopupModal("确认删除仓库链接###DeleteConfirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        var count = this.selected.Count;
        ImGui.TextWrapped($"你确定要从仓库列表里删除这 {count} 个库链接吗？");
        ImGui.Spacing();
        ImGui.TextWrapped("删除前会自动备份（完整仓库列表 + dalamudConfig.json），删除后可以随时点「撤回上次操作」恢复。");
        ImGui.TextWrapped("想保留链接、只是不想加载的话，建议改用「停用」。");
        ImGui.Spacing();

        if (ImGui.Button("确认删除", new Vector2(120, 0)))
        {
            ImGui.CloseCurrentPopup();
            this.DeleteSelected();
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(120, 0)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }
}
