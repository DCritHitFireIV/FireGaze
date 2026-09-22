using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;

namespace FireGaze.UI;

/// <summary>
///     「仓库体检」页：扫描全部第三方仓库 → 展示问题 → 停用 / 删除（带备份与撤回）。
/// </summary>
internal sealed partial class RepoAuditTab
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

    // ---------------- 「已安装」相关 ----------------
    private InstalledPluginsIndex? installedIndex;
    private bool installedIndexStale = true;
    private Task<InstalledPluginsIndex>? installedIndexBuild;
    private DateTime installedIndexRetryAfter = DateTime.MinValue;
    private bool onlyUnused;
    private bool onlyWithPlugins;
    private bool listBuilt;
    private DateTime listRetryAfter = DateTime.MinValue;
    private string sortKey = "status";
    private bool sortDescending;
    private bool resetSortRequested;
    private readonly Dictionary<string, ImTextureID> iconHandles = new(StringComparer.Ordinal);

    /// <summary>
    ///     本帧已经查过、确认"缓存里没有"的图标（避免同一帧反复反射；每帧清空）。
    /// </summary>
    private readonly HashSet<string> iconPeekMisses = new(StringComparer.Ordinal);

    /// <summary>
    ///     本轮 Draw 计时（超过 50ms 会在日志里点名，方便定位是谁在卡）。
    /// </summary>
    private readonly System.Diagnostics.Stopwatch drawWatch = new();
    private DateTime lastSlowDrawLog = DateTime.MinValue;

    // ---------------- 每帧缓存（1000+ 个库时别每帧重算） ----------------
    private List<RepoAuditItem> snapshotCache = [];
    private bool snapshotDirty = true;
    private DateTime snapshotNextAllowed = DateTime.MinValue;
    private bool installedCountsDirty = true;
    private int problemCountCache;
    private int installedInUseCache;
    private int installedUnusedCache;

    // ---------------- 图标检查 / 下载（先查，再由用户决定下不下） ----------------
    private readonly List<InstalledPluginEntry> iconMissing = [];
    private readonly List<InstalledPluginEntry> iconWaiting = [];
    private readonly List<InstalledPluginEntry> iconInFlight = [];
    private readonly HashSet<string> iconDead = new(StringComparer.Ordinal);

    /// <summary>
    ///     后台下载线程完成的队列（UI 线程每帧取；不直接碰 List）。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<InstalledPluginEntry> iconReady = new();

    /// <summary>
    ///     后台下载失败的队列。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<InstalledPluginEntry> iconFailed = new();

    /// <summary>
    ///     同时在飞几个（用户要求比以前快：8）。
    /// </summary>
    private const int IconConcurrency = 8;

    /// <summary>
    ///     两次发动之间至少隔多久（毫秒）。
    /// </summary>
    private const int IconKickMs = 100;

    /// <summary>
    ///     收尾期阈值：待发队列空了、在飞不超过这么多个时，给它们一个短限时。
    /// </summary>
    private const int IconTailMax = 3;

    /// <summary>
    ///     收尾期最多再等这么久（别为了最后 1～2 个图标干等一分钟）。
    /// </summary>
    private static readonly TimeSpan IconTailGrace = TimeSpan.FromSeconds(4);

    /// <summary>
    ///     至少已经发动过这么多个才启用收尾期（一两个图标的小批量不适用，免得把正常的慢请求也砍了）。
    /// </summary>
    private const int IconTailMinRequested = 4;

    private bool iconCheckDone;
    private bool iconDownloadRunning;
    private int iconDownloadTotal;
    private int iconDownloadRequested;
    private int iconDownloadGot;
    private int iconDownloadFailed;
    private DateTime iconDownloadDeadline;
    private DateTime iconDownloadStartedAt;
    private DateTime iconTailDeadline;
    private DateTime nextIconKick;
    private string? iconDownloadLine;

    /// <summary>
    ///     最近一次「检查缺图标」的完整名单（状态行悬停时展开）。
    /// </summary>
    private List<string>? iconMissingNames;
    private List<string>? iconDeadReport;
    private long statusVersion;

    public RepoAuditTab(Plugin plugin)
    {
        this.plugin = plugin;

        // 装 / 卸 / 启停插件后，已安装索引要重算（内存操作，不联网）
        plugin.PluginInterface.ActivePluginsChanged += _ => installedIndexStale = true;
    }

    public void Draw()
    {
        drawWatch.Restart();

        // 打开本页就能看到库链清单（状态 = 未检查），不必先跑一次网络扫描——「装了没」是离线数据
        EnsureList();
        EnsureInstalledIndex();
        FillInstalledCounts();
        var tSetup = drawWatch.ElapsedMilliseconds;

        // ---------------- 说明（压到两行以内） ----------------
        ImGui.TextWrapped("扫描全部第三方仓库，检查链接是否失效、链接是否合规（与卫月同款校验）");
        ImGui.TextDisabled("链接不合规表示安装器无法识别该仓库，可能导致插件列表残缺或排版错乱。");

        // ---------------- 扫描控制 ----------------
        // 图标下载进行中时，体检按钮就地置灰（别再画第二个同名按钮）
        var scanBlocked = iconDownloadRunning;
        if (scanBlocked)
        {
            ImGui.BeginDisabled();
        }

        if (scanning)
        {
            if (ImGui.Button("取消扫描###CancelScan"))
            {
                try
                {
                    cancellation?.Cancel();
                }
                catch
                {
                    // ignore
                }
            }
        }
        else if (ImGui.Button("开始体检###StartScan"))
        {
            StartScan();
        }

        if (scanBlocked)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("图标下载进行中，先等它跑完或点「停止下载」");
            }
        }

        ImGui.SameLine();
        var includeDisabled = plugin.Config.ScanIncludeDisabled;
        if (ImGui.Checkbox("扫描包含已停用仓库###IncDisabled", ref includeDisabled))
        {
            plugin.Config.ScanIncludeDisabled = includeDisabled;
            plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾选后连已停用的库一起扫描（该设置会被保存）");
        }

        // ---------------- 进度 / 统计 ----------------
        int d, t, ok, dead, invalid, blocked, unreachable, disabled, unknown;
        bool scan;
        lock (gate)
        {
            d = done;
            t = total;
            ok = okCount;
            dead = deadCount;
            invalid = invalidCount;
            blocked = blockedCount;
            unreachable = unreachableCount;
            disabled = disabledCount;
            unknown = unknownCount;
            scan = scanning;
        }

        if (scan && t > 0)
        {
            ImGui.ProgressBar((float)d / t, new Vector2(220, 0), $"{d} / {t}");
            ImGui.SameLine();
            ImGui.TextUnformatted($"可用 {ok} · 死链 {dead} · 不合规 {invalid} · 拒绝 {blocked} · 连接失败 {unreachable}");
        }
        else if (t > 0)
        {
            var index = installedIndex;
            string machineGroup;
            if (index is { Available: true })
            {
                machineGroup = $"已安装 {installedInUseCache} · 未安装 {installedUnusedCache}";
            }
            else
            {
                machineGroup = "插件数据不可用";
            }

            ImGui.TextWrapped(
                $"共 {t} 个仓库 ｜ 可用 {ok} · 死链 {dead} · 链接不合规 {invalid} · 拒绝访问 {blocked} · "
                + $"连接失败 {unreachable}" + (unknown > 0 ? $" · 未检查 {unknown}" : string.Empty)
                + $" ｜ {machineGroup} ｜ 已停用 {disabled}");

            if (plugin.Config.LastScanUTC != default)
            {
                ImGui.TextDisabled(
                    $"上次体检：{plugin.Config.LastScanUTC.ToLocalTime():yyyy-MM-dd HH:mm} · 用时 {lastScanDuration.TotalSeconds:0}s");
            }
        }
        else
        {
            ImGui.TextDisabled("还没有体检过。点「开始体检」扫描一次。");
        }

        ImGui.Separator();

        // ---------------- 筛选 + 搜索 ----------------
        ImGui.Text("显示");
        FilterRadio("problems", "有问题的");
        FilterRadio("all", "全部");
        FilterRadio("unreachable", "连接失败");
        FilterRadio("disabled", "已停用");
        FilterRadio("ok", "可用");

        // 第二行：与健康度正交的两个开关（使用情况 / 图标展开）
        var indexReady = installedIndex is { Available: true };

        if (!indexReady)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Checkbox("只看有插件的###OnlyWithPlugins", ref onlyWithPlugins) && onlyWithPlugins)
        {
            onlyUnused = false;
            snapshotDirty = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(indexReady
                ? "只显示装了插件的库，适合逐条看图标缺不缺"
                : "插件数据不可用，暂时不能按这个筛选");
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("只看未安装的###OnlyUnused", ref onlyUnused) && onlyUnused)
        {
            onlyWithPlugins = false;
            snapshotDirty = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(indexReady
                ? "只显示没装过插件的库"
                : "插件数据不可用，暂时不能按这个筛选");
        }

        if (!indexReady)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        var showIcons = plugin.Config.ShowInstalledIcons;
        if (ImGui.Checkbox("显示插件图标###ShowIcons", ref showIcons))
        {
            plugin.Config.ShowInstalledIcons = showIcons;
            plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾选后每条库链下面展开已安装插件的图标；列表每行会变高，可视的库链会变少；悬停图标条可一次看到全部名字");
        }

        ImGui.SameLine();
        var iconCacheEnabled = plugin.Config.IconCacheEnabled;
        if (ImGui.Checkbox("启用图标缓存###IconCache", ref iconCacheEnabled))
        {
            plugin.Config.IconCacheEnabled = iconCacheEnabled;
            plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "把下到的图标存到本地（配置目录 /icons），重开游戏不用重下；\n"
                + "并分批挂回卫月的图标缓存，插件安装器里也能直接用本地图。\n"
                + "关掉后回到旧行为：只借卫月内存缓存，每次重启都要重新下载。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("│");
        ImGui.SameLine();

        var canCheck = indexReady && !scanning && !iconDownloadRunning;
        if (!canCheck)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(iconCheckDone ? "重新检查缺图标###IconCheck" : "检查缺图标###IconCheck"))
        {
            RunIconCheck();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                (indexReady ? string.Empty : "插件数据不可用，暂时不能检查。\n")
                + "哪些已装插件声明了图标、但本地（含落盘缓存）还没有。\n"
                + "范围是已安装的插件，与上面的筛选和勾选无关；这一步不下载任何东西。");
        }

        if (!canCheck)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();

        // 第二步：看过清单后由用户决定下不下
        var canDownload = indexReady && !scanning && iconCheckDone;
        if (!canDownload)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(iconDownloadRunning
                ? "停止下载###IconDownload"
                : $"下载图标（{iconMissing.Count}）###IconDownload"))
        {
            if (iconDownloadRunning)
            {
                FinishIconDownload();
            }
            else
            {
                StartIconDownload();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                (indexReady ? string.Empty : "插件数据不可用，暂时不能下载。\n")
                + (iconCheckDone ? string.Empty : "先点「检查缺图标」，拿到清单再决定。\n")
                + "把上一步查出来缺的那些图标下下来（直连 + 镜像竞速，同时最多 8 个）。\n"
                + "下到的图标会存到本地（配置目录 /icons），重开游戏不用重下，\n"
                + "插件安装器里也能直接用本地图。\n"
                + "最多等 120 秒；只剩最后几个时会收尾（再等 4 秒就报结果）。中途可点「停止下载」。");
        }

        if (!canDownload)
        {
            ImGui.EndDisabled();
        }

        if (installedIndex is { Available: false })
        {
            UiHelpers.ColoredWrapped(
                UiHelpers.Warn,
                "读不到卫月的插件列表，可能是卫月升级改了内部字段——「已安装」这一列显示为 —，这次没法判断哪条库链没在用。");
        }

        // ---------------- 搜索（贴着下面的列表） ----------------
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputTextWithHint("###RepoSearch", "搜索仓库地址…", ref search, 128))
        {
            snapshotDirty = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("按 URL 子串过滤列表（例如输入 \"Atmo\" 只看该作者的库）");
        }

        List<RepoAuditItem> snapshot;
        int selectedCount;
        int selectedHidden;
        lock (gate)
        {
            snapshot = Filtered();
            selectedCount = selected.Count;
            var visible = new HashSet<string>(snapshot.Select(x => x.URL), StringComparer.Ordinal);
            selectedHidden = selected.Count(url => !visible.Contains(url));
        }

        var tFilter = drawWatch.ElapsedMilliseconds;

        // ---------------- 操作工具条 ----------------
        DrawActionBar(selectedCount, selectedHidden, snapshot);

        // ---------------- 状态行（只在有事件结果时出现；图标检查进行中带进度条） ----------------
        if (iconDownloadRunning && iconDownloadTotal > 0)
        {
            ImGui.ProgressBar(
                (float)iconDownloadGot / iconDownloadTotal,
                new Vector2(120, 0));
            ImGui.SameLine();
            UiHelpers.ColoredWrapped(UiHelpers.Muted, iconDownloadLine ?? string.Empty);
        }
        else if (!string.IsNullOrEmpty(statusMessage))
        {
            UiHelpers.ColoredWrapped(statusIsError ? UiHelpers.Bad : UiHelpers.Muted, statusMessage);

            // 缺图标名单在状态行里只能给前几个，悬停看全部（IC2-06）
            if (iconMissingNames is { Count: > 0 } names && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"缺图标 {names.Count} 个：\n" + string.Join("、", names));
            }
        }

        // ---------------- 结果表 ----------------
        TickIconDownload();

        var tableHeight = MathF.Max(120f, ImGui.GetContentRegionAvail().Y - 6f);
        var tableFlags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                         ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings;

        if (ImGui.BeginTable("###RepoRows", 5, tableFlags | ImGuiTableFlags.Sortable, new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize | ImGuiTableColumnFlags.NoSort, 26, 0);
            ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortAscending, 120, 1);
            ImGui.TableSetupColumn("仓库地址", ImGuiTableColumnFlags.WidthStretch, 0, 2);
            ImGui.TableSetupColumn("本库已装", ImGuiTableColumnFlags.WidthFixed, 96, 3);
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
            ImGui.TableHeader("本库已装");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "从这条库链装了 N 个插件。离线统计，装/卸插件后自动重算。\n"
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
                if (resetSortRequested)
                {
                    resetSortRequested = false;
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
                    var newKey = spec.ColumnUserID switch
                    {
                        3 => "installed",
                        4 => "firstSeen",
                        2 => "url",
                        _ => "status",
                    };
                    var newDescending = spec.SortDirection == ImGuiSortDirection.Descending;

                    if (newKey != sortKey || newDescending != sortDescending)
                    {
                        sortKey = newKey;
                        sortDescending = newDescending;
                        snapshotDirty = true;
                    }
                }
            }

            // 行裁剪：1000+ 个库时只画看得见的那几行（否则每帧几千个 ImGui 项，必卡）
            var clipper = new ImGuiListClipper();
            clipper.Begin(snapshot.Count);

            while (clipper.Step())
            {
                iconPeekMisses.Clear();

                for (var rowIndex = clipper.DisplayStart; rowIndex < clipper.DisplayEnd; rowIndex++)
                {
                    var item = snapshot[rowIndex];

                    // 只对看得见的行做只读检查（不下载）
                    PeekVisibleIcons(item);

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                var isSelected = selected.Contains(item.URL);

                // 未体检的行不可勾选：没有体检结论就没有可依据的处理
                if (!item.IsSelectable)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.Checkbox("##sel-" + item.URL, ref isSelected))
                {
                    if (isSelected)
                    {
                        selected.Add(item.URL);
                    }
                    else
                    {
                        selected.Remove(item.URL);
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
                    ImGui.SetTooltip(StatusTooltip(item));
                }

                ImGui.TableNextColumn();
                if (!item.IsEnabled)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Muted);
                }

                UiHelpers.Fitted(item.URL, item.URL + (string.IsNullOrEmpty(item.Note) ? string.Empty : "\n" + item.Note));
                if (!item.IsEnabled)
                {
                    ImGui.PopStyleColor();
                }

                if (ImGui.BeginPopupContextItem("##ctx-" + item.URL))
                {
                    if (ImGui.MenuItem("复制链接"))
                    {
                        ImGui.SetClipboardText(item.URL);
                    }

                    if (ImGui.MenuItem("在浏览器打开"))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = item.URL, UseShellExecute = true });
                        }
                        catch
                        {
                            // ignore
                        }
                    }

                    ImGui.EndPopup();
                }

                ImGui.TableNextColumn();
                DrawInstalledCell(item, indexReady);

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
                    DrawInstalledIcons(item);
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    }
                }
            }

            ImGui.EndTable();
        }

        DrawDeleteConfirmPopup();

        // 自计时：本页一帧超过 50ms 就在日志里点名（定位卡顿用，最多每 5 秒报一次）；带三段细分
        drawWatch.Stop();
        var tTotal = drawWatch.ElapsedMilliseconds;
        if (tTotal > 50
            && DateTime.Now - lastSlowDrawLog > TimeSpan.FromSeconds(5))
        {
            lastSlowDrawLog = DateTime.Now;
            Plugin.Log.Warning(
                $"[FireGaze] 仓库体检页这一帧用了 {tTotal}ms（{items.Count} 个库"
                + $" · 准备 {tSetup}ms / 控件 {tFilter - tSetup}ms / 表格 {tTotal - tFilter}ms）");
        }
    }







    // ------------------------------------------------------------------ 统计


    // ------------------------------------------------------------------ 扫描


    // ------------------------------------------------------------------ 操作



    /// <summary>
    ///     操作之后按当前配置重建列表，保留还在的条目的扫描结果，并重算统计。
    /// </summary>
    // ------------------------------------------------------------------ 「已安装」















}
