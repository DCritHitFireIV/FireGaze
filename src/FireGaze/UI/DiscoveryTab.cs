using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FireGaze.Discovery;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

/// <summary>
///     「插件发现」页签（主窗口第五个页签）：整座云端词表的插件列表，可搜索、排序、点赞、投稿库链，
///     也可以逐条改进译文；改动先存在本地，攒够了点「一键提交」直接推给维护者审核。
///     用户 2026-10-06 定：原「参与翻译」独立窗口整体搬进来，不再单独开窗。
/// </summary>
internal sealed partial class DiscoveryTab
{
    /// <summary>
    ///     一条搜索结果的现状筛选。
    /// </summary>
    private enum StateFilter
    {
        All,
        Missing,
        Machine,
        User,
        Review,
    }

    private readonly Plugin plugin;
    private readonly ContributionsStore store;
    private readonly List<TranslationIndexEntry> filtered = [];
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);

    /// <summary>
    ///     上次在绘制里出错的时间（限流用，不刷屏）。
    /// </summary>
    private DateTime lastDrawErrorAt = DateTime.MinValue;

    private int drawErrorCount;

    /// <summary>
    ///     提交优先走中继（一步、匿名，成功才清空）；中继不可用时退回「打开 GitHub 提交页」的两步式，
    ///     玩家在网页按过 Submit 回来点「确认已提交」才清空待提交。
    /// </summary>
    private bool awaitingConfirm;

    /// <summary>
    ///     下栏勾选的待提交译文（键 = InternalName:Field）。
    /// </summary>
    private readonly HashSet<string> selectedRecords = new(StringComparer.Ordinal);

    /// <summary>
    ///     下栏勾选的历史提交记录（键 = 记录 ID）。
    /// </summary>
    private readonly HashSet<string> selectedHistory = new(StringComparer.Ordinal);

    /// <summary>
    ///     下栏当前页签：-1 = 待提交，0 = 历史提交（合并成一张表，内部按批次分组）。
    /// </summary>
    private int workspaceTab = -1;

    /// <summary>
    ///     历史提交那张表的行缓存（批次组头 + 记录行），避免每帧重新铺开。
    /// </summary>
    private readonly List<HistoryRow> historyRows = [];

    private bool historyRowsDirty = true;

    /// <summary>
    ///     正在看的留档条目（只读详情弹窗）。
    /// </summary>
    private ContributionRecord? viewingHistory;

    private bool deleteHistoryRequested;

    private bool clearHistoryRequested;

    /// <summary>
    ///     历史提交里的一行：要么是某批的组头，要么是这一批里的一条记录。
    /// </summary>
    private sealed class HistoryRow
    {
        public ContributionBatch? Batch { get; init; }

        public ContributionRecord? Record { get; init; }

        public bool IsHeader => Record is null;

        /// <summary>
        ///     组头文字（短版：列宽只有 150px，完整时间与已选数都放悬停里）。
        /// </summary>
        public string BatchLabel =>
            Batch is null
                ? string.Empty
                : $"{Batch.SubmittedLocal:MM-dd HH:mm} · {Batch.Contributions.Count} 条";
    }

    private bool clearRequested;

    /// <summary>
    ///     一次额外提示（比如提交时完整内容落到了哪个文件）。
    /// </summary>
    private string? extraHint;

    /// <summary>
    ///     本地上限检查没过时记下原因（保存后显示在状态行）。
    /// </summary>
    private string? ruleIssue;

    /// <summary>
    ///     重建索引时不要反复反射卫月内部：改成按条目就地重算状态，见 <see cref="RefreshEntryStates"/>。
    /// </summary>
    private bool refreshStatesRequested;

    /// <summary>
    ///     上次看到的词表版本号（<see cref="TranslationTable.Revision"/>），变了就按新词表重算每一行。
    /// </summary>
    private int seenTableRevision = -1;

    private readonly HashSet<string> expandedRepos = new(StringComparer.Ordinal);

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
    private bool rebuildRepoPending = true;
    private StateFilter stateFilter = StateFilter.All;
    private string view = "plugins";

    // ---------------- 插件发现（2026-10-06 新增） ----------------

    /// <summary>本机点赞/待重试/投稿记录（独立文件，不存在配置里）。</summary>
    private readonly DiscoveryStateStore discoveryState;

    /// <summary>中继拉到的赞/推荐统计（可能为 null，界面会退到缓存）。</summary>
    private DiscoveryStats? discoveryStats;

    private DateTime statsFetchedAt = DateTime.MinValue;
    private Task<DiscoveryStats?>? statsTask;

    /// <summary>上次拉统计失败（角标显示“统计暂不可用”，不打断使用）。</summary>
    private bool statsFetchFailed;

    private DiscoverySortMode sortMode = DiscoverySortMode.WeeklyLikes;
    private DiscoverySortContext sortContext = new();
    private readonly Dictionary<string, int> shuffleOrder = new(StringComparer.Ordinal);
    private int shuffleSeed;
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

    // 仓库视图：当前展示的分组
    private readonly List<RepoGroup> repoGroups = [];

    // 编辑弹窗
    private TranslationIndexEntry? editing;
    private List<TranslationIndexEntry> editingBatch = [];
    private string editName = string.Empty;
    private string editPunchline = string.Empty;
    private string editDescription = string.Empty;
    private bool editOpened;

    // 加库
    private string repoInput = string.Empty;
    private string? repoMessage;

    // 状态行 / 导出
    private string? statusMessage;
    private bool statusIsError;

    // 本帧内可见行的图标（只读缓存，不下载）
    private readonly Dictionary<string, ImTextureID> iconHandles = new(StringComparer.Ordinal);
    private readonly HashSet<string> iconMisses = new(StringComparer.Ordinal);

    /// <summary>
    ///     仓库视图的一行：一条库链 + 它提供的插件。
    /// </summary>
    private sealed class RepoGroup
    {
        /// <summary>分组键（官方库用哨兵；同一库链的插件共用）。</summary>
        public required string Key { get; init; }

        public required string URL { get; init; }

        public required string Short { get; init; }

        /// <summary>
        ///     这条库在不在本机的插件列表里（false = 还没加过）。
        /// </summary>
        public bool Known { get; init; }

        public bool Enabled { get; init; }

        /// <summary>
        ///     是不是卫月官方主库（Dip17）——官方插件单独一组，没有可加的库链。
        /// </summary>
        public bool IsOfficial { get; init; }

        public List<TranslationIndexEntry> Plugins { get; } = [];

        public int MissingCount => Plugins.Count(x => x.State == "missing");

        public int UserCount => Plugins.Count(x => x.HasUserTranslation);
    }

    public DiscoveryTab(Plugin plugin, ContributionsStore store)
    {
        this.plugin = plugin;
        this.store = store;
        discoveryState = new DiscoveryStateStore(plugin.ConfigDirectory);

        this.store.Changed += () =>
        {
            rebuildPending = true;
            historyRowsDirty = true;
        };
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
                Plugin.Log.Error(e, "[FireGaze] 参与翻译窗口绘制出错（已跳过这一帧）");
            }

            drawErrorCount++;
            var detail = e.GetType().Name + "：" + (e.Message ?? string.Empty);
            if (detail.Length > 140)
            {
                detail = detail[..140] + "…";
            }

            UiHelpers.ColoredWrapped(
                UiHelpers.Bad,
                $"这个窗口刚才出错了 {drawErrorCount} 次（已写进日志）：{detail}");
        }
    }

    private void DrawCore()
    {
        // ---------------- 顶部说明 ----------------
        ImGui.TextWrapped("对插件名、一行简介、插件详情的翻译做出贡献。");
        ImGui.TextDisabled("提交的译文优先于机器翻译；点「一键提交」会匿名上传给维护者审核，收录后随词表更新。");
        ImGui.TextDisabled("提交不带账号信息；网络不好时会改为打开网页提交。");
        ImGui.TextDisabled("本地改动只在你这里生效；点「从 GitHub 更新词表」不会覆盖你还没提交的译文，你改过的版本始终优先。");

        ImGui.Separator();

        // ---------------- 索引 ----------------
        // 词表被换过（比如刚点过「从 GitHub 更新词表」）就重算每行状态：
        // 插件清单是一次性快照，不重算的话会一直显示旧词表算出来的「缺译文」。
        var tableRevision = plugin.Table.Revision;
        if (seenTableRevision != tableRevision)
        {
            Plugin.Log.Debug($"[FireGaze] 词表版本变化（{seenTableRevision} → {tableRevision}）：重算参与翻译列表状态");
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

        // ---------------- 搜索 + 排序 + 筛选 ----------------
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputTextWithHint("###DiscoverySearch", "搜索插件名、作者、原文、译文…", ref search, 256))
        {
            rebuildPending = true;
            rebuildRepoPending = true;
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
                    rebuildRepoPending = true;
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
                rebuildRepoPending = true;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("重新洗牌；滚动时顺序不会变，想看新的再点一次。");
            }
        }

        DrawFilterRow();

        // ---------------- 视图切换 ----------------
        ImGui.Text("视图");
        ImGui.SameLine();
        if (ImGui.RadioButton("按插件###ViewPlugins", view == "plugins"))
        {
            view = "plugins";
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("按仓库###ViewRepos", view == "repos"))
        {
            view = "repos";
        }


        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("按仓库：一条库链一行，像仓库体检那样看哪些库缺译文多、需不需要加进来。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{index.All.Count} 个插件 · 缺译文 {index.MissingCount} 个");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("当前列出来的插件数，以及其中还缺译文的个数（跟着筛选变）。");
        }

        if (indexMayBePartial)
        {
            ImGui.SameLine();
            UiHelpers.ColoredText(UiHelpers.Muted, "· 卫月刷新插件库时抓的快照，读完会自动重读");
        }

        if (discoveryStats is null && statsFetchFailed)
        {
            ImGui.SameLine();
            UiHelpers.ColoredText(UiHelpers.Warn, "· 点赞/推荐统计暂时拉不到，先按插件名排");
        }

        // ---------------- 投稿插件库（进云端语料） ----------------
        DrawSubmitSection();

        // ---------------- 操作条（勾选 / 加库） ----------------
        DrawActionBar();

        if (!string.IsNullOrEmpty(repoMessage))
        {
            UiHelpers.ColoredWrapped(statusIsError ? UiHelpers.Bad : UiHelpers.Muted, repoMessage);
        }

        // ---------------- 上面：清单 下面：工作区 ----------------
        // 上表吃满剩下的高度（窗口拉高就多显示几行）；下栏按自己的内容给高度，
        // 两边各有下限，拖到最小窗口也不会把上表挤没（HCI 评审 F03/F05/F21）。
        var style = ImGui.GetStyle();
        var available = ImGui.GetContentRegionAvail().Y;
        var statusLines = (string.IsNullOrEmpty(statusMessage) ? 0 : 1) + (string.IsNullOrEmpty(extraHint) ? 0 : 1);
        var statusReserve = statusLines * ImGui.GetTextLineHeightWithSpacing() + 6f;
        var workspaceWanted = Math.Clamp(available * 0.34f, 168f, 320f);
        var tableRow = ImGui.GetTextLineHeight() + (style.CellPadding.Y * 2f) + 1f;
        var topMinimum = (tableRow * 4f) + 6f;   // 表头 + 至少 3 行（HCI 评审 N2）
        var topHeight = MathF.Max(topMinimum, available - workspaceWanted - statusReserve - (style.ItemSpacing.Y * 3f) - 10f);

        if (view == "repos")
        {
            DrawRepoTable(topHeight);
        }
        else
        {
            DrawPluginTable(topHeight);
        }

        ImGui.Separator();
        var workspaceHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y - statusReserve, 128f, workspaceWanted);
        DrawWorkspace(workspaceHeight);

        if (editing is not null && !editOpened)
        {
            editOpened = true;
            ImGui.OpenPopup("翻译###EditTranslation");
        }

        DrawEditPopup();

        // ---------------- 状态行（最底） ----------------
        if (!string.IsNullOrEmpty(statusMessage))
        {
            UiHelpers.ColoredWrapped(statusIsError ? UiHelpers.Bad : UiHelpers.Muted, statusMessage);
        }

        if (!string.IsNullOrEmpty(extraHint))
        {
            UiHelpers.ColoredWrapped(UiHelpers.Warn, extraHint);
        }
    }

















    // ------------------------------------------------------------------ 行



    // ------------------------------------------------------------------ 编辑弹窗










    // ------------------------------------------------------------------ 下面那一栏：改过的译文 / 历史提交记录


























    // ------------------------------------------------------------------ 加库


    // ------------------------------------------------------------------ 图标


    // ------------------------------------------------------------------ 小工具






}
