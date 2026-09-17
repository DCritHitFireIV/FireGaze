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

    // ---------------- 「本机已装」相关 ----------------
    private InstalledPluginsIndex? installedIndex;
    private bool installedIndexStale = true;
    private DateTime installedIndexRetryAfter = DateTime.MinValue;
    private bool onlyUnused;
    private bool listBuilt;
    private string sortKey = "status";
    private bool sortDescending;
    private bool resetSortRequested;
    private readonly Dictionary<string, ImTextureID> iconHandles = new(StringComparer.Ordinal);
    private readonly List<InstalledPluginEntry> iconPending = [];
    private int iconCursor;

    // ---------------- 图标体检（批量检测缺图标并补齐） ----------------
    private bool iconAuditRunning;
    private readonly List<InstalledPluginEntry> iconAuditPending = [];
    private readonly HashSet<string> iconAuditTried = new(StringComparer.Ordinal);
    private readonly HashSet<string> iconDead = new(StringComparer.Ordinal);
    private int iconAuditTotal;
    private int iconAuditFixed;
    private DateTime iconAuditDeadline;
    private DateTime iconAuditStartedAt;
    private string? iconAuditLastLine;
    private List<string>? iconDeadReport;
    private long statusVersion;

    public RepoAuditTab(Plugin plugin)
    {
        this.plugin = plugin;

        // 装 / 卸 / 启停插件后，本机已装索引要重算（内存操作，不联网）
        plugin.PluginInterface.ActivePluginsChanged += _ => this.installedIndexStale = true;
    }

    public void Draw()
    {
        // 打开本页就能看到库链清单（状态 = 未检查），不必先跑一次网络扫描——「本机装了没」是离线数据
        this.EnsureList();
        this.EnsureInstalledIndex();
        this.FillInstalledCounts();

        // ---------------- 说明（压到两行以内） ----------------
        ImGui.TextWrapped("扫描全部第三方仓库：检查链接是否失效、内容是否合规（与卫月同款校验）。");
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

        if (this.iconAuditRunning)
        {
            // 两个网络作业不能同时跑：否则状态行互相覆盖，体检完成句会丢
            ImGui.BeginDisabled();
            ImGui.Button("开始体检###StartScanDisabled");
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("图标检查进行中，先等它跑完或点「停止检查」");
            }
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
            var index = this.installedIndex;
            string machineGroup;
            if (index is { Available: true })
            {
                int inUse, unused;
                lock (this.gate)
                {
                    inUse = this.items.Count(x => x.InstalledCount > 0);
                    unused = this.items.Count(x => x.InstalledCount == 0);
                }

                machineGroup = $"本机：已装 {inUse} · 未装 {unused}";
            }
            else
            {
                machineGroup = "本机：数据不可用";
            }

            ImGui.TextWrapped(
                $"共 {t} 个仓库 ｜ 可用 {ok} · 死链 {dead} · 内容不合规 {invalid} · 拒绝访问 {blocked} · "
                + $"连接失败 {unreachable}" + (unknown > 0 ? $" · 未检查 {unknown}" : string.Empty)
                + $" ｜ {machineGroup} ｜ 另：已停用 {disabled}");

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
        this.FilterRadio("unreachable", "连接失败");
        this.FilterRadio("disabled", "已停用");
        this.FilterRadio("ok", "可用");

        // 第二行：与健康度正交的两个开关（本机使用情况 / 图标展开）
        var indexReady = this.installedIndex is { Available: true };

        if (!indexReady)
        {
            ImGui.BeginDisabled();
        }

        ImGui.Checkbox("只看本机未装的###OnlyUnused", ref this.onlyUnused);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(indexReady
                ? "只显示本机没装过插件的库"
                : "本机插件数据不可用，暂时不能按这个筛选");
        }

        if (!indexReady)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        var showIcons = this.plugin.Config.ShowInstalledIcons;
        if (ImGui.Checkbox("显示插件图标###ShowIcons", ref showIcons))
        {
            this.plugin.Config.ShowInstalledIcons = showIcons;
            this.plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾选后每条库链下面展开已安装插件的图标；列表每行会变高，可视的库链会变少；悬停图标条可一次看到全部名字");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("│");
        ImGui.SameLine();

        var canAudit = indexReady && !this.scanning;
        if (!canAudit)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(this.iconAuditRunning ? "停止检查###IconAudit" : "检查并下载图标###IconAudit"))
        {
            if (this.iconAuditRunning)
            {
                this.FinishIconAudit();
            }
            else
            {
                this.StartIconAudit();
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                (indexReady ? string.Empty : "本机插件数据不可用，暂时不能检查。\n")
                + "检查本机已装插件里哪些声明了图标却没下载到，并让卫月重新下一次。\n"
                + "范围：本机所有已装插件，与上面的筛选和勾选无关。\n"
                + "本版卫月不落盘缓存图标，重开游戏后会重新下载。\n"
                + "最多等 45 秒，中途可点「停止检查」。");
        }

        if (!canAudit)
        {
            ImGui.EndDisabled();
        }

        if (!indexReady)
        {
            UiHelpers.ColoredWrapped(
                UiHelpers.Warn,
                "读不到卫月的已装插件列表，可能是卫月升级改了内部字段——「已安装」这一列显示为 —，本次无法判断哪条库链没在用。");
        }

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

        // ---------------- 状态行（只在有事件结果时出现；图标检查进行中带进度条） ----------------
        if (this.iconAuditRunning && this.iconAuditTotal > 0)
        {
            ImGui.ProgressBar(
                this.iconAuditTotal == 0 ? 0f : (float)this.iconAuditTried.Count / this.iconAuditTotal,
                new Vector2(120, 0));
            ImGui.SameLine();
            UiHelpers.ColoredWrapped(UiHelpers.Muted, this.iconAuditLastLine ?? string.Empty);
        }
        else if (!string.IsNullOrEmpty(this.statusMessage))
        {
            UiHelpers.ColoredWrapped(this.statusIsError ? UiHelpers.Bad : UiHelpers.Muted, this.statusMessage);
        }

        // ---------------- 结果表 ----------------
        this.TickIconAudit();

        if (this.plugin.Config.ShowInstalledIcons)
        {
            this.PumpIconLookups();
        }

        var tableHeight = MathF.Max(120f, ImGui.GetContentRegionAvail().Y - 6f);
        var tableFlags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                         ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings;

        if (ImGui.BeginTable("###RepoRows", 5, tableFlags | ImGuiTableFlags.Sortable, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 26, 0);
            ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortAscending, 120, 1);
            ImGui.TableSetupColumn("仓库地址", ImGuiTableColumnFlags.WidthStretch, 0, 2);
            ImGui.TableSetupColumn("已安装", ImGuiTableColumnFlags.WidthFixed, 96, 3);
            ImGui.TableSetupColumn("首次记录", ImGuiTableColumnFlags.WidthFixed, 84, 4);

            // 自己逐列发表头（而不是 TableHeadersRow），才能给每列挂 tooltip
            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            ImGui.TableNextColumn();
            ImGui.TableHeader("##sel");

            ImGui.TableNextColumn();
            ImGui.TableHeader("状态");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("默认顺序：死链在最上，点列头可切换升/降。");
            }

            ImGui.TableNextColumn();
            ImGui.TableHeader("仓库地址");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("列里是库链地址；悬停具体行可看这条库一共提供多少个插件。");
            }

            ImGui.TableNextColumn();
            ImGui.TableHeader("已安装");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "本机从这条库链装了 N 个插件。离线统计，装/卸插件后自动重算。\n"
                    + "点一下按数量排行，再点一下反向；\n"
                    + "「—」= 本次读不到已装插件数据，不会当成 0。");
            }

            ImGui.TableNextColumn();
            ImGui.TableHeader("首次记录");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("FireGaze 第一次看到这条链接的时间；\n安装本插件之前就存在的库没有记录，排序时排最后。");
            }

            // 读取 ImGui 的排序状态：点列头切换升/降；点「状态」列回到默认的严重度顺序
            var specs = ImGui.TableGetSortSpecs();
            if (!specs.IsNull)
            {
                if (this.resetSortRequested)
                {
                    this.resetSortRequested = false;
                    specs.SpecsCount = 1;
                    specs.Specs[0] = new ImGuiTableColumnSortSpecs
                    {
                        ColumnUserID = 1,
                        ColumnIndex = 1,
                        SortOrder = 0,
                        SortDirection = ImGuiSortDirection.Ascending,
                    };
                    specs.SpecsDirty = true;
                }

                if (specs.SpecsCount > 0)
                {
                    var spec = specs.Specs[0];
                    this.sortKey = spec.ColumnUserID switch
                    {
                        3 => "installed",
                        4 => "firstSeen",
                        2 => "url",
                        _ => "status",
                    };
                    this.sortDescending = spec.SortDirection == ImGuiSortDirection.Descending;
                }
            }

            foreach (var item in snapshot)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                var isSelected = this.selected.Contains(item.Url);

                // 未体检的行不可勾选：没有体检结论就没有可依据的处理
                if (!item.IsSelectable)
                {
                    ImGui.BeginDisabled();
                }

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

                if (!item.IsSelectable)
                {
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("还没体检过：先点「开始体检」，再决定要不要处理这个库。");
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
                this.DrawInstalledCell(item, indexReady);

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

                // 图标条：表格没有 colspan，放进最宽的「仓库地址」列；这一行只用来放图标
                if (showIcons && item.InstalledCount > 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    this.DrawInstalledIcons(item);
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
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
                foreach (var item in snapshot.Where(x => x.IsSelectable))
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

        // 非默认排序才显示（这一行常驻内容多，避免把右侧的恢复类按钮挤下去）
        var sortText = this.sortKey switch
        {
            "installed" => this.sortDescending ? "按已安装 ↓" : "按已安装 ↑",
            "firstSeen" => this.sortDescending ? "按首次记录 ↓" : "按首次记录 ↑",
            "url" => this.sortDescending ? "按地址 ↓" : "按地址 ↑",
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
                var ok = this.plugin.TryUndoLast(out var message);
                this.SetStatus(ok ? message : "撤回失败：" + message, !ok);
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
            "unreachable" => this.items.Where(x => x.Status is RepoStatus.Unreachable),
            "disabled" => this.items.Where(x => !x.IsEnabled),
            "ok" => this.items.Where(x => x.Status is RepoStatus.Ok or RepoStatus.Empty),
            _ => this.items.Where(x => x.IsProblem),
        };

        if (!string.IsNullOrWhiteSpace(this.search))
        {
            query = query.Where(x => x.Url.Contains(this.search.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // 与健康度正交的第二个轴：只看本机没从它装过插件的库（数据不可用时该开关是禁用的）
        if (this.onlyUnused && this.installedIndex is { Available: true })
        {
            query = query.Where(x => x.InstalledCount == 0);
        }

        return this.SortItems(query);
    }

    /// <summary>
    /// 排序：默认按严重度（死链在最上，<c>UiHelpers.SeverityRank</c>）；列头可切换升/降；平序一律按 URL。
    /// 不可用（`—`）与「未记录」在升序里排最后。
    /// </summary>
    private List<RepoAuditItem> SortItems(IEnumerable<RepoAuditItem> query)
    {
        var list = query.ToList();

        Comparison<RepoAuditItem> primary = this.sortKey switch
        {
            "installed" => (a, b) => InstalledRank(a).CompareTo(InstalledRank(b)),
            "firstSeen" => (a, b) => string.Compare(FirstSeenRank(a), FirstSeenRank(b), StringComparison.Ordinal),
            "url" => (a, b) => string.Compare(a.Url, b.Url, StringComparison.OrdinalIgnoreCase),
            _ => (a, b) => UiHelpers.SeverityRank(a.Status).CompareTo(UiHelpers.SeverityRank(b.Status)),
        };

        list.Sort((a, b) =>
        {
            var result = primary(a, b);
            if (result == 0)
            {
                result = string.Compare(a.Url, b.Url, StringComparison.OrdinalIgnoreCase);
            }

            return this.sortDescending ? -result : result;
        });

        return list;

        static int InstalledRank(RepoAuditItem item) => item.InstalledCount < 0 ? int.MaxValue : item.InstalledCount;

        static string FirstSeenRank(RepoAuditItem item) => string.IsNullOrEmpty(item.FirstSeen) ? "9999" : item.FirstSeen;
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
            this.SetStatus("读取仓库列表失败：" + error, true);
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
            this.SetStatus("仓库列表是空的（或者都被排除了）。", true);
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
            this.sortKey = "status";
            this.sortDescending = false;
            this.resetSortRequested = true;
            this.listBuilt = true;
        }

        this.SetStatus($"开始体检 {list.Count} 个仓库…", false);
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
                this.SetStatus("扫描已取消。", false);
            }
            catch (Exception e)
            {
                this.SetStatus("扫描出错：" + e.Message, true);
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
            this.SetStatus("读取仓库列表失败：" + error, true);
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
            this.SetStatus("所选仓库都已经处于停用状态。", false);
            return;
        }

        var changed = this.plugin.Repos.SetEnabled(targets, false, out error);
        if (error is not null)
        {
            this.SetStatus("停用失败：" + error, true);
            return;
        }

        this.plugin.RecordUndo(record);
        this.plugin.Repos.Save(out _);
        this.plugin.Repos.TriggerReload(out _);

        this.SetStatus($"已停用 {changed} 个仓库（{DateTime.Now:HH:mm}）—— 链接保留、不再加载；可点「撤回」恢复。", false);
        this.RefreshFromLive();
    }

    private void DeleteSelected()
    {
        var backup = this.plugin.Repos.BackupRepos(out var error);
        if (string.IsNullOrEmpty(backup))
        {
            this.SetStatus("备份失败，已取消删除：" + error, true);
            return;
        }

        var live = this.plugin.Repos.ReadAll(out error);
        if (error is not null)
        {
            this.SetStatus("读取仓库列表失败：" + error, true);
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
            this.SetStatus("所选的仓库已经不在列表里了。", false);
            return;
        }

        var removed = this.plugin.Repos.Remove(record.Entries.Select(x => x.Url), out error);
        if (error is not null)
        {
            this.SetStatus("删除失败：" + error, true);
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
    // ------------------------------------------------------------------ 「本机已装」

    /// <summary>
    /// 打开页面就把库链清单建出来（状态 = 未检查，不可勾选），不必先跑一次网络扫描：
    /// 「本机装了没」是离线数据，体检只负责回填健康度。
    /// </summary>
    private void EnsureList()
    {
        if (this.scanning || this.listBuilt)
        {
            return;
        }

        var repos = this.plugin.Repos.ReadAll(out var error);
        if (error is not null)
        {
            this.SetStatus("读取仓库列表失败：" + error, true);
            return;   // 不置 listBuilt，下一帧重试
        }

        var list = repos
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .Select(entry => new RepoAuditItem
            {
                Url = entry.Url,
                IsEnabled = entry.IsEnabled,
                Index = entry.Index,
                FirstSeen = this.plugin.GetFirstSeen(entry.Url),
            })
            .ToList();

        lock (this.gate)
        {
            this.items = list;

            // 还没体检过：默认看「全部」，否则「有问题的」会是空列表（旧行为是扫描后才建列表）
            if (list.Count > 0 && list.All(x => x.Status == RepoStatus.Unknown))
            {
                this.filter = "all";
            }

            this.total = list.Count;
        }

        this.RecomputeCounters();
        this.listBuilt = true;
    }

    /// <summary>读/刷新「本机已装插件」索引；读不到时界面必须显示 `—`（不能显示假 0）。</summary>
    private void EnsureInstalledIndex()
    {
        if (!this.installedIndexStale || DateTime.Now < this.installedIndexRetryAfter)
        {
            return;
        }

        this.installedIndex = InstalledPluginsIndex.Build();
        this.installedIndexStale = false;
        this.iconHandles.Clear();
        this.iconPending.Clear();
        this.iconCursor = 0;

        if (this.installedIndex.Available)
        {
            // 预取队列：所有有来源地址的已装插件（不限可见行），每帧取一小批
            foreach (var entry in this.installedIndex.All)
            {
                if (!string.IsNullOrWhiteSpace(entry.RepositoryUrl))
                {
                    this.iconPending.Add(entry);
                }
            }
        }

        // 不可用时过几秒再试一次（卫月可能还在启动）；但界面一律按「不可用」渲染
        this.installedIndexRetryAfter = this.installedIndex.Available
            ? DateTime.MaxValue
            : DateTime.Now.AddSeconds(3);
    }

    /// <summary>把索引结果填到每一行（不可用 → -1，不参与任何「0 个」的断言）。</summary>
    private void FillInstalledCounts()
    {
        var index = this.installedIndex;

        lock (this.gate)
        {
            foreach (var item in this.items)
            {
                if (index is { Available: true })
                {
                    item.InstalledPlugins = index.TryGetInstalled(item.Url, out var plugins) ? plugins : [];
                    item.InstalledCount = item.InstalledPlugins.Count;
                }
                else
                {
                    item.InstalledPlugins = [];
                    item.InstalledCount = -1;
                }
            }
        }
    }

    /// <summary>「本机已装」单元格：一律放数字（0 也是数字）；数据不可用显示 `—`。</summary>
    private void DrawInstalledCell(RepoAuditItem item, bool indexReady)
    {
        if (!indexReady || item.InstalledCount < 0)
        {
            ImGui.TextDisabled("—");
        }
        else if (item.InstalledCount == 0)
        {
            ImGui.TextDisabled("0 个");
        }
        else
        {
            ImGui.TextUnformatted($"{item.InstalledCount} 个");
        }

        if (!ImGui.IsItemHovered())
        {
            return;
        }

        if (!indexReady)
        {
            ImGui.SetTooltip("读不到卫月的已装插件列表，可能是卫月升级改了内部字段；本次无法判断。");
            return;
        }

        var lines = new List<string>
        {
            $"本机从这条库链装了 {item.InstalledCount} 个插件。",
            "（这条库一共提供多少个插件，把鼠标放在仓库地址上看）",
        };

        if (item.InstalledPlugins.Count > 0)
        {
            var names = string.Join("、", item.InstalledPlugins.Take(8).Select(Describe));
            lines.Add(item.InstalledPlugins.Count > 8 ? $"{names}…（共 {item.InstalledPlugins.Count} 个）" : names);
        }
        else
        {
            lines.Add("只表示本机没从它装过插件：可能你在别的机器装过、它只是备用源、或插件是手动/开发版装的。");
        }

        if (item.InstalledPlugins.Any(x => this.iconDead.Contains(x.InternalName)))
        {
            lines.Add("带「图标地址失效」的插件要作者更新清单；图标只影响列表里的小图，不影响插件运行。");
        }

        ImGui.SetTooltip(string.Join('\n', lines));
        return;

        string Describe(InstalledPluginEntry entry)
        {
            // 只标「确认失效」这一类；「本机还没下到」是常态（卫月不落盘缓存），不在这里断言
            return this.iconDead.Contains(entry.InternalName)
                ? entry.DisplayName + " · 图标地址失效"
                : entry.DisplayName;
        }
    }

    /// <summary>
    /// 图标预取：每帧最多试 <paramref name="budget"/> 个（轮转）。
    /// 不用时间节流：卫月那边一下载完，下一帧就能显示出来。
    /// </summary>
    private void PumpIconLookups(int budget = 12)
    {
        for (var i = 0; i < budget && this.iconPending.Count > 0; i++)
        {
            if (this.iconCursor >= this.iconPending.Count)
            {
                this.iconCursor = 0;
            }

            var entry = this.iconPending[this.iconCursor];

            if (this.iconHandles.ContainsKey(entry.InternalName))
            {
                this.iconPending.RemoveAt(this.iconCursor);
                continue;
            }

            if (PluginIconLookup.TryGetHandle(entry, out var handle) && !handle.IsNull)
            {
                this.iconHandles[entry.InternalName] = handle;
                this.iconPending.RemoveAt(this.iconCursor);
                continue;
            }

            this.iconCursor++;   // 还没好：下次轮到它再看
        }
    }

    /// <summary>
    /// 图标条：有图标的在前、缺图标的最后（首字母占位），组内保持原顺序。
    /// 每个图标悬停显示**它自己**的名字（占位格额外说明图标未缓存）；鼠标停在空白处则一次列出全部名字，
    /// 而且顺序与图标排列**完全一致**（之前名字按原序列、图标却重排过，所以对不上）。
    /// </summary>
    private void DrawInstalledIcons(RepoAuditItem item)
    {
        var plugins = item.InstalledPlugins;
        if (plugins.Count == 0)
        {
            return;
        }

        // 画序：先有图标的（原序），再缺图标的（原序）
        var sequence = new List<(InstalledPluginEntry Entry, ImTextureID? Handle)>(plugins.Count);
        var missing = new List<(InstalledPluginEntry Entry, ImTextureID? Handle)>();

        foreach (var entry in plugins)
        {
            if (this.iconHandles.TryGetValue(entry.InternalName, out var cached) && !cached.IsNull)
            {
                sequence.Add((entry, cached));
            }
            else
            {
                missing.Add((entry, null));
            }
        }

        sequence.AddRange(missing);

        const float iconSize = 22f;
        var spacing = ImGui.GetStyle().ItemSpacing.X;

        ImGui.BeginGroup();

        var available = ImGui.GetContentRegionAvail().X;
        var maxIcons = Math.Clamp((int)((available + spacing) / (iconSize + spacing)), 1, 8);

        var drawn = 0;
        var hoveredIcon = false;

        foreach (var (entry, handle) in sequence)
        {
            if (drawn >= maxIcons)
            {
                break;
            }

            if (drawn > 0)
            {
                ImGui.SameLine();
            }

            if (handle is { } texture)
            {
                ImGui.Image(texture, new Vector2(iconSize, iconSize));
            }
            else
            {
                DrawIconPlaceholder(entry, iconSize);
            }

            if (ImGui.IsItemHovered())
            {
                hoveredIcon = true;

                var tooltip = handle is not null
                    ? entry.DisplayName
                    : this.iconDead.Contains(entry.InternalName)
                        ? entry.DisplayName + "\n图标地址已失效，需要作者更新清单"
                        : entry.DeclaresIcon
                            ? entry.DisplayName + "\n图标还没缓存到本机，插件本身已装；只影响列表里的小图，不影响插件运行"
                            : entry.DisplayName + "\n这个插件没有提供图标；只影响列表里的小图，不影响插件运行";

                ImGui.SetTooltip(tooltip);
            }

            drawn++;
        }

        var hidden = sequence.Count - drawn;
        if (hidden > 0)
        {
            if (drawn > 0)
            {
                ImGui.SameLine();
            }

            ImGui.TextDisabled($"+{hidden}");

            if (ImGui.IsItemHovered())
            {
                hoveredIcon = true;
                ImGui.SetTooltip("另有：" + string.Join("、", sequence.Skip(drawn).Select(x => x.Entry.DisplayName)));
            }
        }

        ImGui.EndGroup();

        // 停在图标条空白处：一次列出全部名字，顺序与图标排列一致
        if (!hoveredIcon && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                $"本机已装 {plugins.Count} 个：" + string.Join("、", sequence.Select(x => x.Entry.DisplayName)));
        }
    }

    /// <summary>写状态行（并推进世代号：图标检查的后台结果只在没人动过状态行时才回写）。</summary>
    private void SetStatus(string? message, bool isError)
    {
        this.statusVersion++;
        this.SetStatus(message, isError);
    }

    /// <summary>图标检查：把「作者声明了图标、但本机没缓存」的插件列出来，让卫月去重下。</summary>
    private void StartIconAudit()
    {
        var index = this.installedIndex;
        if (index is not { Available: true })
        {
            return;
        }

        var missing = index.All
            .Where(x => x.DeclaresIcon && !this.iconHandles.ContainsKey(x.InternalName))
            .ToList();

        this.iconAuditPending.Clear();
        this.iconAuditPending.AddRange(missing);
        this.iconAuditTried.Clear();
        this.iconAuditTotal = missing.Count;
        this.iconAuditFixed = 0;
        this.iconAuditStartedAt = DateTime.Now;

        if (missing.Count == 0)
        {
            this.SetStatus("图标检查：声明了图标的插件本机都已缓存，没有需要下载的。", false);
            return;
        }

        this.iconAuditRunning = true;
        this.iconAuditDeadline = DateTime.Now.AddSeconds(45);
        this.iconAuditLastLine = $"图标检查：已试 0/{missing.Count} · 补上 0";
        this.SetStatus(null, false);
    }

    /// <summary>图标检查每帧推一下（下载在卫月那边异步做，我们只轮询结果）。</summary>
    private void TickIconAudit()
    {
        // 后台探到的「地址已失效」名单，在渲染线程合并（避免跨线程改集合）
        if (this.iconDeadReport is { } report)
        {
            this.iconDeadReport = null;
            foreach (var name in report)
            {
                this.iconDead.Add(name);
            }
        }

        if (!this.iconAuditRunning)
        {
            return;
        }

        const int budget = 6;
        for (var i = 0; i < budget && this.iconAuditPending.Count > 0; i++)
        {
            var entry = this.iconAuditPending[0];
            this.iconAuditTried.Add(entry.InternalName);

            if (this.iconHandles.ContainsKey(entry.InternalName))
            {
                this.iconAuditPending.RemoveAt(0);
                this.iconAuditFixed++;
                continue;
            }

            if (PluginIconLookup.TryGetHandle(entry, out var handle) && !handle.IsNull)
            {
                this.iconHandles[entry.InternalName] = handle;
                this.iconPending.RemoveAll(x => x.InternalName == entry.InternalName);
                this.iconDead.Remove(entry.InternalName);
                this.iconAuditPending.RemoveAt(0);
                this.iconAuditFixed++;
                continue;
            }

            // 还没好：移到队尾，下一轮再看
            this.iconAuditPending.RemoveAt(0);
            this.iconAuditPending.Add(entry);
        }

        this.iconAuditLastLine =
            $"图标检查：已试 {this.iconAuditTried.Count}/{this.iconAuditTotal} · 补上 {this.iconAuditFixed}";

        if (this.iconAuditPending.Count == 0 || DateTime.Now >= this.iconAuditDeadline)
        {
            this.FinishIconAudit();
        }
    }

    /// <summary>
    /// 收尾：报「分母 + 三类归属 + 下一步」，并对仍未完成的图标地址探一次可达性。
    /// 三类分别是：作者没给地址（补不了）、没下载到（可再点一次）、地址已失效（要作者改）。
    /// </summary>
    private void FinishIconAudit()
    {
        if (!this.iconAuditRunning)
        {
            return;
        }

        this.iconAuditRunning = false;

        var leftover = this.iconAuditPending.ToList();
        this.iconAuditPending.Clear();

        var total = this.iconAuditTotal;
        var fixedCount = this.iconAuditFixed;
        var installedTotal = this.installedIndex?.All.Count ?? 0;
        var seconds = (DateTime.Now - this.iconAuditStartedAt).TotalSeconds;

        var noAddress = leftover.Count(x => x.IsThirdParty && string.IsNullOrWhiteSpace(x.IconUrl));
        var checkable = leftover
            .Where(x => !(x.IsThirdParty && string.IsNullOrWhiteSpace(x.IconUrl)))
            .ToList();

        var head = $"本次图标检查：本机已装 {installedTotal} 个插件"
                   + $"，其中 {noAddress} 个作者没给图标地址，补不了"
                   + $"；{total} 个本机没下载到，这次补上 {fixedCount} 个"
                   + (checkable.Count > 0 ? $"，{checkable.Count} 个仍未完成" : string.Empty)
                   + $"；用时 {seconds:0}s。";

        if (checkable.Count == 0)
        {
            this.SetStatus(head, false);
            return;
        }

        this.SetStatus(head + " 正在确认那几个的图标地址…", false);

        var urls = checkable
            .Where(x => x.IsThirdParty && !string.IsNullOrWhiteSpace(x.IconUrl))
            .Select(x => (Entry: x, Url: x.IconUrl!))
            .DistinctBy(x => x.Url, StringComparer.Ordinal)
            .ToList();
        var official = checkable.Count - urls.Count;

        var version = this.statusVersion;

        _ = Task.Run(async () =>
        {
            var deadNames = new List<string>();
            var failed = 0;

            foreach (var (entry, url) in urls)
            {
                var probe = await RepoScanner.ProbeUrlAsync(url, CancellationToken.None).ConfigureAwait(false);
                if (probe.Status is 404 or 410)
                {
                    deadNames.Add(entry.InternalName);
                }
                else if (!probe.Ok)
                {
                    failed++;
                }
            }

            if (version != this.statusVersion)
            {
                return;   // 期间用户做了别的动作（停用 / 删除 / 撤回 / 扫描），别覆盖人家的确认消息
            }

            var parts = new List<string>(3);
            if (deadNames.Count > 0)
            {
                parts.Add($"{deadNames.Count} 个图标地址已失效，需要插件作者更新清单");
            }

            if (failed > 0)
            {
                parts.Add($"{failed} 个没下完，可以再点一次");
            }

            if (official > 0)
            {
                parts.Add($"{official} 个是官方库插件，稍后自己会下好");
            }

            this.iconDeadReport = deadNames;
            this.SetStatus(head + (parts.Count > 0 ? " " + string.Join("；", parts) + "。" : string.Empty), false);
        });
    }

    private static void DrawIconPlaceholder(InstalledPluginEntry entry, float size)
    {
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRect(
            start,
            start + new Vector2(size, size),
            ImGui.GetColorU32(new Vector4(0.45f, 0.45f, 0.45f, 1f)),
            4f);

        var initial = string.IsNullOrEmpty(entry.DisplayName) ? "?" : entry.DisplayName[..1].ToUpperInvariant();
        var textSize = ImGui.CalcTextSize(initial);
        drawList.AddText(
            start + ((new Vector2(size, size) - textSize) * 0.5f),
            ImGui.GetColorU32(UiHelpers.Muted),
            initial);

        ImGui.Dummy(new Vector2(size, size));
    }

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
        int noInstalled, withInstalled, withInstalledTotal;
        bool indexAvailable;
        List<RepoAuditItem> preview;
        lock (this.gate)
        {
            count = this.selected.Count;
            unreachable = this.items.Count(x => this.selected.Contains(x.Url) && x.Status == RepoStatus.Unreachable);
            indexAvailable = this.installedIndex is { Available: true };
            var chosen = this.items.Where(x => this.selected.Contains(x.Url)).ToList();
            noInstalled = chosen.Count(x => x.InstalledCount == 0);
            withInstalled = chosen.Count(x => x.InstalledCount > 0);
            withInstalledTotal = chosen.Where(x => x.InstalledCount > 0).Sum(x => x.InstalledCount);
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

        // 「本机没在用」不等于「可以删」；反过来，删掉有插件的库会让那些插件失去更新来源
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
                    $"其中 {noInstalled} 条本机没有从它装过插件——不代表这个库没用：可能你在别的机器装过、它只是备用源、或插件是手动/开发版装的。");
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
