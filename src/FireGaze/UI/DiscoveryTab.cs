using System.Collections.Concurrent;
using Dalamud.Bindings.ImGui;
using FireGaze.Discovery;
using FireGaze.Translate;

namespace FireGaze.UI;

/// <summary>
///     「插件发现」页签（主窗口第五个页签）：整座云端词表的插件列表（含官方主库），
///     可以搜索、按六种方式排序、点赞、把没加过的库加进来、投稿新库。
///     样式对齐「插件汉化」：一行一张卡片（图标 + 标题 + 一行简介 + 元信息），点整行展开详情。
///     用户 2026-10-06 定：原「参与翻译」独立窗口不再保留，翻译贡献/编辑那套全部去掉。
/// </summary>
internal sealed partial class DiscoveryTab
{
    private readonly Plugin plugin;
    private readonly List<TranslationIndexEntry> filtered = [];

    /// <summary>
    ///     上次在绘制里出错的时间（限流用，不刷屏）。
    /// </summary>
    private DateTime lastDrawErrorAt = DateTime.MinValue;

    private int drawErrorCount;

    /// <summary>
    ///     重建索引时不要反复反射卫月内部：改成按条目就地重算状态，见 <see cref="RefreshEntryStates"/>。
    /// </summary>
    private bool refreshStatesRequested;

    /// <summary>
    ///     上次看到的词表版本号（<see cref="TranslationTable.Revision"/>），变了就按新词表重算每一行。
    /// </summary>
    private int seenTableRevision = -1;

    private TranslationIndex? index;
    private Task<TranslationIndex>? buildTask;
    private DateTime retryAfter = DateTime.MinValue;

    /// <summary>
    ///     这份索引是不是在卫月刷新插件库期间抓的（可能漏插件）。刷新完会自动重建。
    /// </summary>
    private bool indexMayBePartial;

    /// <summary>
    ///     当前这次建索引开始时，卫月是不是正在刷新插件库。
    /// </summary>
    private bool buildStartedWhileReposBusy;

    /// <summary>
    ///     因为卫月在刷新插件库而推迟建索引的起始时刻（等太久就照建，见 Index.cs）。
    /// </summary>
    private DateTime? waitingForReposSince;

    private string search = string.Empty;
    private bool rebuildPending = true;

    // ---------------- 插件发现 ---------------- 

    /// <summary>本机点赞/待重试/投稿记录（独立文件，不存在配置里）。</summary>
    private readonly DiscoveryStateStore discoveryState;

    /// <summary>中继拉到的赞/推荐统计（可能为 null，界面会退到缓存）。</summary>
    private DiscoveryStats? discoveryStats;

    private DateTime statsFetchedAt = DateTime.MinValue;
    private Task<DiscoveryStats?>? statsTask;

    /// <summary>上次拉统计失败（角标显示“统计暂不可用”，不打断使用）。</summary>
    private bool statsFetchFailed;

    private DiscoverySortMode sortMode = DiscoverySortMode.WeeklyLikes;
    private readonly DiscoverySortContext sortContext = new();
    private readonly Dictionary<string, int> shuffleOrder = new(StringComparer.Ordinal);
    private DateTime lastPendingRetry = DateTime.MinValue;

    /// <summary>后台点赞成功后的云端真值（UI 线程取用，避免后台改字典）。</summary>
    private readonly ConcurrentQueue<(string Plugin, int Total, int Weekly)> likeResults = new();

    /// <summary>当前展开详情的那一行（内部名）；空 = 没展开。</summary>
    private string? expandedEntry;

    /// <summary>每行实测高度（整行点击的 Selectable 用；首帧用估算值）。</summary>
    private readonly Dictionary<string, float> rowHeights = new(StringComparer.Ordinal);

    /// <summary>筛选：隐藏官方主库插件（用户 2026-10-06 定：允许隐藏）。</summary>
    private bool hideOfficial;

    /// <summary>投稿区状态。</summary>
    private string submitInput = string.Empty;
    private bool submitAddToLibrary = true;
    private string? submitMessage;
    private bool submitIsError;
    private bool submitBusy;

    /// <summary>待「顺手加到我的库」的地址（后台检测完回到 UI 线程再加）。</summary>
    private string? submitAddPending;

    // 状态行
    private string? statusMessage;
    private bool statusIsError;

    // 本帧内可见行的图标（只读缓存，不下载）
    private readonly Dictionary<string, ImTextureID> iconHandles = new(StringComparer.Ordinal);
    private readonly HashSet<string> iconMisses = new(StringComparer.Ordinal);

    public DiscoveryTab(Plugin plugin)
    {
        this.plugin = plugin;
        discoveryState = new DiscoveryStateStore(plugin.ConfigDirectory);
    }

    /// <summary>
    ///     绘制入口只做一件事：兜住异常。绘制路径上任何一处抛异常都不该把整张页签（乃至游戏）带下去
    ///     —— 2026-09-22 就因为 UiHelpers 里一处 Math.Clamp 抛了 ArgumentException，一开这个界面就报错。
    ///     现在最多每 5 秒写一次日志，并在页签里留一行提示。
    /// </summary>
    public void Draw()
    {
        try
        {
            DrawCore();
        }
        catch (Exception e)
        {
            var now = DateTime.UtcNow;
            if (now - lastDrawErrorAt > TimeSpan.FromSeconds(5))
            {
                lastDrawErrorAt = now;
                Plugin.Log.Error(e, "[FireGaze] 插件发现页签绘制出错（已跳过这一帧）");
            }

            drawErrorCount++;
            var detail = e.GetType().Name + "：" + (e.Message ?? string.Empty);
            if (detail.Length > 140)
            {
                detail = detail[..140] + "…";
            }

            UiHelpers.ColoredWrapped(
                UiHelpers.Bad,
                $"这个页签刚才出错了 {drawErrorCount} 次（已写进日志）：{detail}");
        }
    }

    private void DrawCore()
    {
        // ---------------- 顶部说明 ----------------
        ImGui.TextWrapped("整座云端插件库（含官方主库）：搜索、排序、点赞，把没加过的库加进来。");
        ImGui.TextDisabled("和本机装了哪些插件无关；每个插件每周可点赞一次，点一行展开详情。");

        ImGui.Separator();

        // ---------------- 索引 ----------------
        // 词表被换过（比如刚点过「从 GitHub 更新词表」）就重算每行：插件清单是一次性快照。
        var tableRevision = plugin.Table.Revision;
        if (seenTableRevision != tableRevision)
        {
            Plugin.Log.Debug($"[FireGaze] 词表版本变化（{seenTableRevision} → {tableRevision}）：重算插件发现列表状态");
            seenTableRevision = tableRevision;
            RefreshIndexSoon();
        }

        RefreshEntryStates();
        EnsureIndex();

        // 点赞/推荐统计（中继）与本机失败重试：不阻塞绘制，成功/失败都在后台。
        EnsureDiscoveryStats();
        RetryPendingReport();
        DrainLikeResults();
        FlushSubmitAdd();

        if (index is null)
        {
            ImGui.TextDisabled("正在读取插件库…");
            if (waitingForReposSince is not null)
            {
                ImGui.TextDisabled("卫月正在刷新插件库，等它读完再统计（通常几秒）。");
            }

            return;
        }

        if (!index.Available)
        {
            UiHelpers.ColoredWrapped(
                UiHelpers.Warn,
                $"读不到卫月的插件库列表（{index.FailureReason}）。可能是卫月升级改了内部字段——本页暂时不可用。");
            ImGui.Spacing();
            if (ImGui.Button("重试###RetryIndex"))
            {
                retryAfter = DateTime.MinValue;
                index = null;
            }

            return;
        }

        // ---------------- 搜索 + 排序（第一行） ----------------
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputTextWithHint("###DiscoverySearch", "搜索插件名、作者、一行简介、详情…", ref search, 256))
        {
            rebuildPending = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("全字段搜索：插件名（原文+译文）、作者、一行简介、详情；大小写不敏感。");
        }

        ImGui.SameLine();
        ImGui.Text("排序");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("###DiscoverySort", SortLabel(sortMode)))
        {
            foreach (var mode in Enum.GetValues<DiscoverySortMode>())
            {
                if (ImGui.Selectable(SortLabel(mode), mode == sortMode))
                {
                    sortMode = mode;
                    rebuildPending = true;
                }
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("默认：本周点赞降序，同赞按插件名。\n推荐排行 = 从云端把库加进自己库的次数。");
        }

        if (sortMode == DiscoverySortMode.Random)
        {
            ImGui.SameLine();
            if (ImGui.Button("换一批###DiscoveryShuffle"))
            {
                shuffleOrder.Clear();
                rebuildPending = true;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("重新洗牌；滚动时顺序不会变，想看新的再点一次。");
            }
        }

        // ---------------- 筛选 + 计数（第二行，避免挤成一团） ----------------
        if (ImGui.Checkbox("隐藏主库###DiscoveryHideOfficial", ref hideOfficial))
        {
            rebuildPending = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("隐掉卫月官方主库（Dip17）里的插件，只看第三方；官方库本来就在安装器里，不用从这里加。");
        }

        ImGui.SameLine();
        var showDisabled = plugin.Config.ContributeShowDisabled;
        if (ImGui.Checkbox("连着已停用的库###DiscoveryDisabled", ref showDisabled))
        {
            plugin.Config.ContributeShowDisabled = showDisabled;
            plugin.SaveConfig();
            rebuildPending = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾上后连已停用仓库里的插件也列出来；这类插件当前不会出现在安装器里。\n"
                             + "还没加进你库里的仓库不受这个开关影响 —— 那是整座云库。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{index.All.Count} 个插件");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("云端词表里的插件总数（含官方主库；筛选只影响显示）。");
        }

        if (indexMayBePartial)
        {
            ImGui.SameLine();
            UiHelpers.ColoredText(UiHelpers.Muted, "· 正在等卫月读完插件库");
        }

        if (discoveryStats is null && statsFetchFailed)
        {
            ImGui.SameLine();
            UiHelpers.ColoredText(UiHelpers.Warn, "· 统计暂不可用");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("点赞/推荐统计暂时拉不到（中继不可用），先按插件名排；\n点过的赞会先记在本机，稍后自动同步。");
            }
        }

        var lastAdd = LastAddRecord();
        if (lastAdd is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"撤回添加（{lastAdd.Count}）###DiscoveryUndoAdd"))
            {
                UndoLastAdd();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"把刚加进来的 {lastAdd.Count} 条库从列表里移除（{lastAdd.TimeUTC.ToLocalTime():HH:mm} 那次添加）");
            }
        }

        // ---------------- 投稿插件库（进云端语料） ----------------
        DrawSubmitSection();

        // ---------------- 列表（吃满剩余高度） ----------------
        var statusReserve = string.IsNullOrEmpty(statusMessage) ? 0f : ImGui.GetTextLineHeightWithSpacing() + 6f;
        var listHeight = MathF.Max(120f, ImGui.GetContentRegionAvail().Y - statusReserve);
        DrawList(listHeight);

        // ---------------- 状态行（最底） ----------------
        if (!string.IsNullOrEmpty(statusMessage))
        {
            UiHelpers.ColoredWrapped(statusIsError ? UiHelpers.Bad : UiHelpers.Muted, statusMessage);
        }
    }
}
