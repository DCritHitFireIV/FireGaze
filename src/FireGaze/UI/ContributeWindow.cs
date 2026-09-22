using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

/// <summary>
///     「参与翻译」独立窗口：搜索全部第三方插件的原文与译文，可以逐条改进、也可以给还没有译文的插件补上；
///     改动先存在本地（配置目录），攒够了点「一键提交」直接推给维护者审核。
///     入口：「简介汉化」页「从 GitHub 更新词表」右边的小按钮。
/// </summary>
internal sealed partial class ContributeWindow : Window
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
    ///     提交分两步：点「一键提交」只打开 GitHub 提交页；玩家在网页按过 Submit 回来点「确认已提交」才清空待提交。
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
    private string search = string.Empty;
    private bool rebuildPending = true;
    private bool rebuildRepoPending = true;
    private StateFilter stateFilter = StateFilter.All;
    private string view = "plugins";

    // 仓库视图：当前展示的分组
    private readonly List<RepoGroup> repoGroups = [];

    // 搜索范围：名称 / 一行简介 / 详情
    private bool scopeName = true;
    private bool scopePunchline = true;
    private bool scopeDescription = true;

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
        public required string URL { get; init; }

        public required string Short { get; init; }

        public bool Enabled { get; init; }

        public bool IsOfficial { get; init; }

        public List<TranslationIndexEntry> Plugins { get; } = [];

        public int MissingCount => Plugins.Count(x => x.State == "missing");

        public int UserCount => Plugins.Count(x => x.HasUserTranslation);
    }

    public ContributeWindow(Plugin plugin, ContributionsStore store)
        : base("参与翻译###FireGazeContribute")
    {
        this.plugin = plugin;
        this.store = store;

        Size = new Vector2(880, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 460),
        };

        this.store.Changed += () =>
        {
            rebuildPending = true;
            historyRowsDirty = true;
        };
    }

    /// <summary>
    ///     绘制入口只做一件事：兜住异常。绘制路径上任何一处抛异常都不该把整张窗口（乃至游戏）带下去
    ///     —— 2026-09-22 就因为 UiHelpers 里一处 Math.Clamp 抛了 ArgumentException，一开这个窗口就报错。
    ///     现在最多每 5 秒写一次日志，并在窗口里留一行提示。
    /// </summary>
    public override void Draw()
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
        ImGui.TextDisabled("提交的译文优先于机器翻译；点「一键提交」会把这一批填进 GitHub 的提交页，维护者收录后随词表更新。");
        ImGui.TextDisabled("所有提交都是以匿名形式提交，不会带上你的账号信息。");
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

        if (index is null)
        {
            ImGui.TextDisabled("正在读取插件库…");
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

        // ---------------- 搜索 + 范围 + 筛选 ----------------
        ImGui.SetNextItemWidth(280);
        if (ImGui.InputTextWithHint("###ContributeSearch", "搜索插件名、原文、译文…", ref search, 256))
        {
            rebuildPending = true;
            rebuildRepoPending = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("搜索范围");
        foreach (var (label, value, set) in new (string, bool, Action<bool>)[]
                 {
                     ("插件名", scopeName, v => scopeName = v),
                     ("一行简介", scopePunchline, v => scopePunchline = v),
                     ("插件详情", scopeDescription, v => scopeDescription = v),
                 })
        {
            ImGui.SameLine();
            var check = value;
            if (ImGui.Checkbox(label + "###Scope" + label, ref check))
            {
                set(check);
                rebuildPending = true;
                rebuildRepoPending = true;
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("至少勾一项；三项全不勾 = 按插件名搜。");
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
