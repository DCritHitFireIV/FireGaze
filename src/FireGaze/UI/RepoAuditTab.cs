using System.Diagnostics;
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
    private int blockedCount;
    private int unreachableCount;
    private int disabledCount;
    private int unknownCount;
    private string? statusMessage;
    private bool statusIsError;
    private string filter = "problems";
    private string search = string.Empty;
    private bool deleteRequested;
    private TimeSpan lastScanDuration;

    public RepoAuditTab(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        // ---------------- 说明（压到两行以内） ----------------
        ImGui.TextWrapped("扫描全部第三方仓库（默认含已停用）：检查链接是否失效、内容是否合法仓库文件（与卫月同款校验）。");
        ImGui.TextDisabled("内容不合规 = 安装器无法识别该仓库：可能导致插件列表残缺或排版错乱。");

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
        else if (ImGui.Button("开始体检###StartScan"))
        {
            this.StartScan();
        }

        ImGui.SameLine();
        var includeDisabled = this.plugin.Config.ScanIncludeDisabled;
        if (ImGui.Checkbox("扫描范围含已停用###IncDisabled", ref includeDisabled))
        {
            this.plugin.Config.ScanIncludeDisabled = includeDisabled;
            this.plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾选后连已停用的库一起扫描（该设置会被保存）");
        }

        // 搜索框放在扫描行，给筛选行留出空间（中文字号下筛选行很宽）
        ImGui.SameLine();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(170);
        ImGui.InputTextWithHint("###RepoSearch", "搜索仓库地址…", ref this.search, 128);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("按 URL 子串过滤列表（例如输入 \"Atmo\" 只看该作者的库）");
        }

        // ---------------- 进度 / 统计 ----------------
        int d, t, ok, dead, invalid, blocked, unreachable, disabled, unknown;
        bool scan;
        lock (this.gate)
        {
            d = this.done;
            t = this.total;
            ok = this.okCount;
            dead = this.deadCount;
            invalid = this.invalidCount;
            blocked = this.blockedCount;
            unreachable = this.unreachableCount;
            disabled = this.disabledCount;
            unknown = this.unknownCount;
            scan = this.scanning;
        }

        if (scan && t > 0)
        {
            ImGui.ProgressBar((float)d / t, new Vector2(220, 0), $"{d} / {t}");
            ImGui.SameLine();
            ImGui.TextUnformatted($"可用 {ok} · 死链 {dead} · 不合规 {invalid} · 拒绝 {blocked} · 连接失败 {unreachable}");
        }
        else if (t > 0)
        {
            ImGui.TextWrapped(
                $"共 {t} 个仓库：可用 {ok} · 死链 {dead} · 内容不合规 {invalid} · 拒绝访问 {blocked} · " +
                $"连接失败 {unreachable} · 已停用 {disabled}" + (unknown > 0 ? $" · 未检查 {unknown}" : string.Empty));
            if (this.plugin.Config.LastScanUtc != default)
            {
                ImGui.TextDisabled(
                    $"上次体检：{this.plugin.Config.LastScanUtc.ToLocalTime():yyyy-MM-dd HH:mm} · 用时 {this.lastScanDuration.TotalSeconds:0}s");
            }
        }
        else
        {
            ImGui.TextDisabled("还没有体检过。点「开始体检」扫描一次。");
        }

        ImGui.Separator();

        // ---------------- 筛选 + 搜索 ----------------
        ImGui.Text("显示");
        this.FilterRadio("problems", "有问题的");
        this.FilterRadio("all", "全部");
        this.FilterRadio("deadinvalid", "死链 + 不合规");
        this.FilterRadio("unreachable", "连接失败");
        this.FilterRadio("disabled", "已停用");
        this.FilterRadio("ok", "可用");

        List<RepoAuditItem> snapshot;
        int selectedCount;
        int selectedHidden;
        lock (this.gate)
        {
            snapshot = this.Filtered();
            selectedCount = this.selected.Count;
            var visible = new HashSet<string>(snapshot.Select(x => x.Url), StringComparer.Ordinal);
            selectedHidden = this.selected.Count(url => !visible.Contains(url));
        }

        // ---------------- 操作工具条 ----------------
        this.DrawActionBar(selectedCount, selectedHidden, snapshot);

        // ---------------- 状态行（只在有事件结果时出现） ----------------
        if (!string.IsNullOrEmpty(this.statusMessage))
        {
            UiHelpers.ColoredWrapped(this.statusIsError ? UiHelpers.Bad : UiHelpers.Muted, this.statusMessage);
        }

        // ---------------- 结果表 ----------------
        var tableHeight = MathF.Max(120f, ImGui.GetContentRegionAvail().Y - 6f);
        var tableFlags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                         ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings;

        if (ImGui.BeginTable("###RepoRows", 4, tableFlags, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed, 32);
            ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 140);
            ImGui.TableSetupColumn("仓库地址", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("首次记录", ImGuiTableColumnFlags.WidthFixed, 96);
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
                UiHelpers.ColoredText(UiHelpers.StatusColor(item.Status), item.StatusText);
                if (!item.IsEnabled)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("· 停用");
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(this.StatusTooltip(item));
                }

                ImGui.TableNextColumn();
                if (!item.IsEnabled)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Muted);
                }

                UiHelpers.Truncated(item.Url, 56, item.Url + (string.IsNullOrEmpty(item.Note) ? string.Empty : "\n" + item.Note));
                if (!item.IsEnabled)
                {
                    ImGui.PopStyleColor();
                }

                if (ImGui.BeginPopupContextItem("##ctx-" + item.Url))
                {
                    if (ImGui.MenuItem("复制链接"))
                    {
                        ImGui.SetClipboardText(item.Url);
                    }

                    if (ImGui.MenuItem("在浏览器打开"))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = item.Url, UseShellExecute = true });
                        }
                        catch
                        {
                            // ignore
                        }
                    }

                    ImGui.EndPopup();
                }

                ImGui.TableNextColumn();
                if (string.IsNullOrEmpty(item.FirstSeen))
                {
                    ImGui.TextDisabled("未记录");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("卫月不记录仓库添加时间。\nFireGaze 只会记录自己第一次看到该链接的时间；\n安装本插件之前就存在的库没有记录。");
                    }
                }
                else
                {
                    ImGui.TextUnformatted(item.FirstSeen.Length >= 16 ? item.FirstSeen[5..16] : item.FirstSeen);
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip(item.FirstSeen);
                    }
                }
            }

            ImGui.EndTable();
        }

        this.DrawDeleteConfirmPopup();
    }

    /// <summary>
    /// 操作工具条：第一行是选择类动作（停用 │ 删除… + 全选/清空），删除用红色并与停用拉开距离；
    /// 第二行左侧是动态选择摘要，右侧是恢复类动作（撤回 / 备份目录，永远渲染）。
    /// </summary>
    private void DrawActionBar(int selectedCount, int selectedHidden, List<RepoAuditItem> snapshot)
    {
        var canAct = !this.scanning && selectedCount > 0;
        var filteredCount = snapshot.Count;

        if (!canAct)
        {
            ImGui.BeginDisabled();
        }

        ImGui.Button($"停用所选（{selectedCount}）###DisableSelected");
        if (ImGui.IsItemClicked() && canAct)
        {
            this.DisableSelected();
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
            this.deleteRequested = true;
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

        var canSelect = !this.scanning;
        if (!canSelect)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"全选当前（{filteredCount}）###SelectFiltered"))
        {
            lock (this.gate)
            {
                foreach (var item in snapshot)
                {
                    this.selected.Add(item.Url);
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
            lock (this.gate)
            {
                this.selected.Clear();
            }
        }

        if (!canSelect)
        {
            ImGui.EndDisabled();
        }

        // ---------------- 第二行：选择摘要 + 恢复动作 ----------------
        var undo = this.plugin.Config.UndoHistory.Count > 0 ? this.plugin.Config.UndoHistory[^1] : null;

        var hint = selectedCount == 0
            ? $"结果 {snapshot.Count} 行 · 已选 0 —— 勾选列表行，或用「全选当前」"
            : $"结果 {snapshot.Count} 行 · 已选 {selectedCount}（共 {this.ProblemCount()} 个问题项"
              + (selectedHidden > 0 ? $"，其中 {selectedHidden} 项不在当前筛选内）" : "）");

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
                var ok = this.plugin.TryUndoLast(out var message);
                this.statusMessage = ok ? message : "撤回失败：" + message;
                this.statusIsError = !ok;
                this.RefreshFromLive();
            }

            if (ImGui.IsItemHovered())
            {
                var lines = new List<string>
                {
                    $"备份：{Path.GetFileName(undo.BackupPath ?? "（无）")}",
                    $"剩余可撤回：{this.plugin.Config.UndoHistory.Count} 步",
                };
                lines.AddRange(undo.Entries.Take(3).Select(x => "样本：" + UiHelpers.Shorten(x.Url, 60)));
                ImGui.SetTooltip(string.Join('\n', lines));
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("打开备份目录###OpenBackups"))
        {
            this.plugin.OpenBackupDirectory();
        }
    }

    private string StatusTooltip(RepoAuditItem item) => item.Status switch
    {
        RepoStatus.Invalid =>
            "内容不合规：这个链接返回的不是仓库 JSON（常见原因：填了 GitHub 网页地址而不是 raw 地址）。\n"
            + "后果：该库的插件会全部加载不出来，还可能导致插件列表残缺或排版错乱。\n"
            + (item.Note ?? string.Empty),
        RepoStatus.Dead => "链接已失效（404 / 410）。\n" + (item.Note ?? string.Empty),
        RepoStatus.Unreachable =>
            "连接失败：超时 / 证书 / 服务器错误，不一定是死链（可能是网络问题）。\n"
            + "建议先「重新体检」确认，再决定是否停用。\n" + (item.Note ?? string.Empty),
        RepoStatus.Blocked => "服务器拒绝访问：可能是私有仓库、限流或需要登录。\n" + (item.Note ?? string.Empty),
        _ => item.Note ?? string.Empty,
    };

    private int ProblemCount()
    {
        lock (this.gate)
        {
            return this.items.Count(x => x.IsProblem);
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
            "unreachable" => this.items.Where(x => x.Status is RepoStatus.Unreachable),
            "disabled" => this.items.Where(x => !x.IsEnabled),
            "ok" => this.items.Where(x => x.Status is RepoStatus.Ok or RepoStatus.Empty),
            _ => this.items.Where(x => x.IsProblem),
        };

        if (!string.IsNullOrWhiteSpace(this.search))
        {
            query = query.Where(x => x.Url.Contains(this.search.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderBy(x => UiHelpers.SeverityRank(x.Status))
            .ThenBy(x => x.Url, StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------------ 统计

    private void RecomputeCounters()
    {
        lock (this.gate)
        {
            this.okCount = this.items.Count(x => x.Status is RepoStatus.Ok or RepoStatus.Empty);
            this.deadCount = this.items.Count(x => x.Status == RepoStatus.Dead);
            this.invalidCount = this.items.Count(x => x.Status == RepoStatus.Invalid);
            this.blockedCount = this.items.Count(x => x.Status == RepoStatus.Blocked);
            this.unreachableCount = this.items.Count(x => x.Status == RepoStatus.Unreachable);
            this.unknownCount = this.items.Count(x => x.Status == RepoStatus.Unknown);
            this.disabledCount = this.items.Count(x => !x.IsEnabled);
            this.total = this.items.Count;
        }
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
            this.okCount = this.deadCount = this.invalidCount = this.blockedCount = 0;
            this.unreachableCount = this.disabledCount = this.unknownCount = 0;
            this.scanning = true;
            this.filter = "problems";
        }

        this.statusMessage = $"开始体检 {list.Count} 个仓库…";
        this.statusIsError = false;
        var startedAt = DateTime.UtcNow;

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
                        // 只默认勾选「死链 + 内容不合规」；连接失败可能是网络问题，不默认纳入
                        if (item.IsAutoSelected)
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
                            this.blockedCount = progress.Blocked;
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
                this.lastScanDuration = DateTime.UtcNow - startedAt;

                int dead, invalid, unreachable;
                lock (this.gate)
                {
                    this.scanning = false;
                    dead = this.deadCount;
                    invalid = this.invalidCount;
                    unreachable = this.unreachableCount;
                }

                if (this.statusMessage?.StartsWith("开始体检") == true)
                {
                    this.statusMessage =
                        $"体检完成（用时 {this.lastScanDuration.TotalSeconds:0}s）：已自动勾选 {dead + invalid} 个死链/不合规项"
                        + (unreachable > 0 ? $"；另有 {unreachable} 个「连接失败」需人工确认（可能是网络问题，未勾选）。" : "。");
                    this.statusIsError = false;
                }

                this.plugin.Config.LastScanUtc = DateTime.UtcNow;
                foreach (var item in list)
                {
                    item.FirstSeen = this.plugin.GetFirstSeen(item.Url) ?? item.FirstSeen;
                }

                this.plugin.TrackFirstSeen();
                this.plugin.SaveConfig();
                this.RecomputeCounters();
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

        this.statusMessage = $"已停用 {changed} 个仓库（{DateTime.Now:HH:mm}）—— 链接保留、不再加载；可点「撤回」恢复。";
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
            $"已删除 {removed} 个链接（{DateTime.Now:HH:mm}），备份：{Path.GetFileName(backup)}；" +
            "可点「撤回」把链接放回原位置。";
        this.statusIsError = false;
        this.RefreshFromLive();
    }

    /// <summary>操作之后按当前配置重建列表，保留还在的条目的扫描结果，并重算统计。</summary>
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
        }

        this.RecomputeCounters();
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

        int count, unreachable;
        List<RepoAuditItem> preview;
        lock (this.gate)
        {
            count = this.selected.Count;
            unreachable = this.items.Count(x => this.selected.Contains(x.Url) && x.Status == RepoStatus.Unreachable);
            preview = this.items
                .Where(x => this.selected.Contains(x.Url))
                .OrderBy(x => UiHelpers.SeverityRank(x.Status))
                .Take(5)
                .ToList();
        }

        ImGui.TextWrapped($"你确定要从仓库列表里删除这 {count} 个库链接吗？");
        ImGui.Spacing();

        foreach (var item in preview)
        {
            ImGui.TextDisabled("· " + UiHelpers.Shorten(item.Url, 64));
        }

        if (count > preview.Count)
        {
            ImGui.TextDisabled($"…… 等共 {count} 条");
        }

        if (unreachable > 0)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Warn, $"注意：其中 {unreachable} 条是「连接失败」——可能是网络问题而不是死链。");
        }

        ImGui.Spacing();
        ImGui.TextWrapped("删除前会自动备份（完整仓库列表 + dalamudConfig.json），删除后可以随时点「撤回」恢复。");
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
