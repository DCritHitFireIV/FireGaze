using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

/// <summary>
/// 「参与翻译」独立窗口：搜索全部第三方插件的原文与译文，可以逐条改进、也可以给还没有译文的插件补上；
/// 改动先存在本地（配置目录），攒够了点「一键提交」直接推给维护者审核。
/// 入口：「简介汉化」页「从 GitHub 更新词表」右边的小按钮。
/// </summary>
internal sealed class ContributeWindow : Window
{
    /// <summary>一条搜索结果的现状筛选。</summary>
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

    /// <summary>上次在绘制里出错的时间（限流用，不刷屏）。</summary>
    private DateTime lastDrawErrorAt = DateTime.MinValue;

    private int drawErrorCount;

    /// <summary>下栏勾选的待提交译文（键 = InternalName:Field）。</summary>
    private readonly HashSet<string> selectedRecords = new(StringComparer.Ordinal);

    /// <summary>下栏勾选的历史提交记录（键 = 记录 Id）。</summary>
    private readonly HashSet<string> selectedHistory = new(StringComparer.Ordinal);

    /// <summary>下栏当前页签：-1 = 待提交，0 = 历史提交（合并成一张表，内部按批次分组）。</summary>
    private int workspaceTab = -1;

    /// <summary>历史提交那张表的行缓存（批次组头 + 记录行），避免每帧重新铺开。</summary>
    private readonly List<HistoryRow> historyRows = [];

    private bool historyRowsDirty = true;

    /// <summary>正在看的留档条目（只读详情弹窗）。</summary>
    private ContributionRecord? viewingHistory;

    private bool deleteHistoryRequested;

    private bool clearHistoryRequested;

    /// <summary>历史提交里的一行：要么是某批的组头，要么是这一批里的一条记录。</summary>
    private sealed class HistoryRow
    {
        public ContributionBatch? Batch { get; init; }

        public ContributionRecord? Record { get; init; }

        public bool IsHeader => this.Record is null;

        /// <summary>组头文字（短版：列宽只有 150px，完整时间与已选数都放悬停里）。</summary>
        public string BatchLabel =>
            this.Batch is null
                ? string.Empty
                : $"{this.Batch.SubmittedLocal:MM-dd HH:mm} · {this.Batch.Contributions.Count} 条";
    }

    private bool clearRequested;

    /// <summary>正在推送中（防止连点）。</summary>
    private volatile bool submitBusy;

    /// <summary>一次额外提示（比如提交时完整内容落到了哪个文件）。</summary>
    private string? extraHint;

    /// <summary>本地上限检查没过时记下原因（保存后显示在状态行）。</summary>
    private string? ruleIssue;

    /// <summary>重建索引时不要反复反射卫月内部：改成按条目就地重算状态，见 <see cref="RefreshEntryStates"/>。</summary>
    private bool refreshStatesRequested;

    /// <summary>上次看到的词表版本号（<see cref="TranslationTable.Revision"/>），变了就按新词表重算每一行。</summary>
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

    /// <summary>仓库视图的一行：一条库链 + 它提供的插件。</summary>
    private sealed class RepoGroup
    {
        public required string Url { get; init; }

        public required string Short { get; init; }

        public bool Enabled { get; init; }

        public bool IsOfficial { get; init; }

        public List<TranslationIndexEntry> Plugins { get; } = [];

        public int MissingCount => this.Plugins.Count(x => x.State == "missing");

        public int UserCount => this.Plugins.Count(x => x.HasUserTranslation);
    }

    public ContributeWindow(Plugin plugin, ContributionsStore store)
        : base("参与翻译###FireGazeContribute")
    {
        this.plugin = plugin;
        this.store = store;

        this.Size = new Vector2(880, 680);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 460),
        };

        this.store.Changed += () =>
        {
            this.rebuildPending = true;
            this.historyRowsDirty = true;
        };
    }

    /// <summary>
    /// 绘制入口只做一件事：兜住异常。绘制路径上任何一处抛异常都不该把整张窗口（乃至游戏）带下去
    /// —— 2026-09-22 就因为 UiHelpers 里一处 Math.Clamp 抛了 ArgumentException，一开这个窗口就报错。
    /// 现在最多每 5 秒写一次日志，并在窗口里留一行提示。
    /// </summary>
    public override void Draw()
    {
        try
        {
            this.DrawCore();
        }
        catch (Exception e)
        {
            var now = DateTime.UtcNow;
            if (now - this.lastDrawErrorAt > TimeSpan.FromSeconds(5))
            {
                this.lastDrawErrorAt = now;
                Plugin.Log.Error(e, "[FireGaze] 参与翻译窗口绘制出错（已跳过这一帧）");
            }

            this.drawErrorCount++;
            UiHelpers.ColoredWrapped(
                UiHelpers.Bad,
                this.drawErrorCount <= 1
                    ? "这个窗口刚才出错了一次（已写进日志）；换页签或重开窗口可以继续用。"
                    : $"这个窗口又出错了 {this.drawErrorCount} 次（已写进日志）。");
        }
    }

    private void DrawCore()
    {
        // ---------------- 顶部说明 ----------------
        ImGui.TextWrapped("对插件名、一行简介、插件详情的翻译做出贡献。");
        ImGui.TextDisabled("提交的译文优先于机器翻译；点「一键提交」会把这一批直接发给维护者审核，通过后随词表更新。");
        ImGui.TextDisabled("所有提交都是以匿名形式提交，不会带上你的账号信息。");
        ImGui.TextDisabled("本地改动只在你这里生效；点「从 GitHub 更新词表」不会覆盖你还没提交的译文，你改过的版本始终优先。");

        ImGui.Separator();

        // ---------------- 索引 ----------------
        // 词表被换过（比如刚点过「从 GitHub 更新词表」）就重算每行状态：
        // 插件清单是一次性快照，不重算的话会一直显示旧词表算出来的「缺译文」。
        var tableRevision = this.plugin.Table.Revision;
        if (this.seenTableRevision != tableRevision)
        {
            Plugin.Log.Debug($"[FireGaze] 词表版本变化（{this.seenTableRevision} → {tableRevision}）：重算参与翻译列表状态");
            this.seenTableRevision = tableRevision;
            this.RefreshIndexSoon();
        }

        this.RefreshEntryStates();
        this.EnsureIndex();

        if (this.index is null)
        {
            ImGui.TextDisabled("正在读取插件库…");
            return;
        }

        if (!this.index.Available)
        {
            UiHelpers.ColoredWrapped(
                UiHelpers.Warn,
                $"读不到卫月的插件库列表（{this.index.FailureReason}）。可能是卫月升级改了内部字段——本页暂时不可用。");
            ImGui.Spacing();
            if (ImGui.Button("重试###RetryIndex"))
            {
                this.retryAfter = DateTime.MinValue;
                this.index = null;
            }

            return;
        }

        // ---------------- 搜索 + 范围 + 筛选 ----------------
        ImGui.SetNextItemWidth(280);
        if (ImGui.InputTextWithHint("###ContributeSearch", "搜索插件名、原文、译文…", ref this.search, 256))
        {
            this.rebuildPending = true;
            this.rebuildRepoPending = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("搜索范围");
        foreach (var (label, value, set) in new (string, bool, Action<bool>)[]
                 {
                     ("插件名", this.scopeName, v => this.scopeName = v),
                     ("一行简介", this.scopePunchline, v => this.scopePunchline = v),
                     ("插件详情", this.scopeDescription, v => this.scopeDescription = v),
                 })
        {
            ImGui.SameLine();
            var check = value;
            if (ImGui.Checkbox(label + "###Scope" + label, ref check))
            {
                set(check);
                this.rebuildPending = true;
                this.rebuildRepoPending = true;
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("至少勾一项；三项全不勾 = 按插件名搜。");
        }

        this.DrawFilterRow();

        // ---------------- 视图切换 ----------------
        ImGui.Text("视图");
        ImGui.SameLine();
        if (ImGui.RadioButton("按插件###ViewPlugins", this.view == "plugins"))
        {
            this.view = "plugins";
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("按仓库###ViewRepos", this.view == "repos"))
        {
            this.view = "repos";
        }


        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("按仓库：一条库链一行，像仓库体检那样看哪些库缺译文多、需不需要加进来。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{this.index.All.Count} 个插件 · 缺译文 {this.index.MissingCount} 个");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("当前列出来的插件数，以及其中还缺译文的个数（跟着筛选变）。");
        }

        // ---------------- 操作条（勾选 / 加库） ----------------
        this.DrawActionBar();

        if (!string.IsNullOrEmpty(this.repoMessage))
        {
            UiHelpers.ColoredWrapped(this.statusIsError ? UiHelpers.Bad : UiHelpers.Muted, this.repoMessage);
        }

        // ---------------- 上面：清单 下面：工作区 ----------------
        // 上表吃满剩下的高度（窗口拉高就多显示几行）；下栏按自己的内容给高度，
        // 两边各有下限，拖到最小窗口也不会把上表挤没（HCI 评审 F03/F05/F21）。
        var style = ImGui.GetStyle();
        var available = ImGui.GetContentRegionAvail().Y;
        var statusLines = (string.IsNullOrEmpty(this.statusMessage) ? 0 : 1) + (string.IsNullOrEmpty(this.extraHint) ? 0 : 1);
        var statusReserve = statusLines * ImGui.GetTextLineHeightWithSpacing() + 6f;
        var workspaceWanted = Math.Clamp(available * 0.34f, 168f, 320f);
        var tableRow = ImGui.GetTextLineHeight() + (style.CellPadding.Y * 2f) + 1f;
        var topMinimum = (tableRow * 4f) + 6f;   // 表头 + 至少 3 行（HCI 评审 N2）
        var topHeight = MathF.Max(topMinimum, available - workspaceWanted - statusReserve - (style.ItemSpacing.Y * 3f) - 10f);

        if (this.view == "repos")
        {
            this.DrawRepoTable(topHeight);
        }
        else
        {
            this.DrawPluginTable(topHeight);
        }

        ImGui.Separator();
        var workspaceHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y - statusReserve, 128f, workspaceWanted);
        this.DrawWorkspace(workspaceHeight);

        if (this.editing is not null && !this.editOpened)
        {
            this.editOpened = true;
            ImGui.OpenPopup("翻译###EditTranslation");
        }

        this.DrawEditPopup();

        // ---------------- 状态行（最底） ----------------
        if (!string.IsNullOrEmpty(this.statusMessage))
        {
            UiHelpers.ColoredWrapped(this.statusIsError ? UiHelpers.Bad : UiHelpers.Muted, this.statusMessage);
        }

        if (!string.IsNullOrEmpty(this.extraHint))
        {
            UiHelpers.ColoredWrapped(UiHelpers.Warn, this.extraHint);
        }
    }

    /// <summary>筛选行 + 几个显示开关。</summary>
    private void DrawFilterRow()
    {
        this.FilterRadio(StateFilter.All, "全部");
        this.FilterRadio(StateFilter.Missing, "缺译文");
        this.FilterRadio(StateFilter.Machine, "机器译");
        this.FilterRadio(StateFilter.User, "我提交过");
        this.FilterRadio(StateFilter.Review, "待复核");

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "缺译文 = 上游给了内容、但还没有译文。\n"
                + "上游本来就空着的字段不算缺译，但你可以自己补一份。\n"
                + "上游原文本身就是中文的也不算缺译（那是国服/汉化分支的简介）。\n"
                + "待复核 = 上游原文改过、译文还没跟上。");
        }

        ImGui.SameLine();
        var showIcons = this.plugin.Config.ShowIconsInContribute;
        if (ImGui.Checkbox("显示图标###ContributeIcons", ref showIcons))
        {
            this.plugin.Config.ShowIconsInContribute = showIcons;
            this.plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("只显示本地已经有缓存的图标，不会为此下载任何东西。");
        }

        ImGui.SameLine();
        var showDisabled = this.plugin.Config.ContributeShowDisabled;
        if (ImGui.Checkbox("连着已停用的库###ContributeDisabled", ref showDisabled))
        {
            this.plugin.Config.ContributeShowDisabled = showDisabled;
            this.plugin.SaveConfig();
            this.rebuildPending = true;
            this.rebuildRepoPending = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾上后连已停用仓库里的插件也列出来；这类插件当前不会出现在安装器里。");
        }
    }

    /// <summary>插件视图：一行一个插件。</summary>
    private void DrawPluginTable(float tableHeight)
    {
        this.RebuildFiltered();

        var showIconColumn = this.plugin.Config.ShowIconsInContribute;
        var columns = showIconColumn ? 8 : 7;   // ##sel + 状态 + [图标] + 插件名 + 来源库 + 原文 + 译文 + 操作

        if (!ImGui.BeginTable(
                "###ContributeRows",
                columns,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings,
                new Vector2(0, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize | ImGuiTableColumnFlags.NoSort, 26, 0);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 72, 1);
        if (showIconColumn)
        {
            ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 26, 2);
        }

        ImGui.TableSetupColumn("插件名", ImGuiTableColumnFlags.WidthFixed, 186, 3);
        ImGui.TableSetupColumn("来源库", ImGuiTableColumnFlags.WidthFixed, 118, 4);
        ImGui.TableSetupColumn("原文", ImGuiTableColumnFlags.WidthStretch, 0, 5);
        ImGui.TableSetupColumn("译文", ImGuiTableColumnFlags.WidthFixed, 118, 6);
        ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 58, 7);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TableHeader("##sel");

        ImGui.TableNextColumn();
        ImGui.TableHeader("状态");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "缺译 = 上游给了内容、但还没有译文；机器译 = 译文全部来自机器；\n"
                + "你译 = 有你贡献过的字段；带 ! 表示原文改过、需要复核。");
        }

        if (showIconColumn)
        {
            ImGui.TableNextColumn();
            ImGui.TableHeader("##icon");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("插件名");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("点插件名打开详情：三个字段的原文与你当前的译文。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("来源库");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("插件所在的库链；点一下复制地址，右键可在浏览器打开。\n「官方库」= 卫月官方主库（Dip17）。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("原文");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("悬停看全文；点插件名可在弹窗里看完整原文。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("译文");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("你当前的译文完成度；上游本来没提供的字段不算缺译，但你可以自己补。");
        }

        ImGui.TableNextColumn();

        var clipper = new ImGuiListClipper();
        clipper.Begin(this.filtered.Count);

        while (clipper.Step())
        {
            this.iconMisses.Clear();

            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                if (i < 0 || i >= this.filtered.Count)
                {
                    continue;
                }

                this.DrawRow(this.filtered[i], showIconColumn);
            }
        }

        ImGui.EndTable();
    }

    /// <summary>仓库视图：一行一条库链（点开展开它提供的插件）。</summary>
    private void DrawRepoTable(float tableHeight)
    {
        this.RebuildRepoGroups();

        var showIconColumn = this.plugin.Config.ShowIconsInContribute;
        var columns = showIconColumn ? 7 : 6;

        if (!ImGui.BeginTable(
                "###ContributeRepos",
                columns,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings,
                new Vector2(0, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 26, 0);
        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 72, 1);
        if (showIconColumn)
        {
            ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 26, 2);
        }

        ImGui.TableSetupColumn("仓库", ImGuiTableColumnFlags.WidthFixed, 210, 3);
        ImGui.TableSetupColumn("插件数", ImGuiTableColumnFlags.WidthFixed, 190, 4);
        ImGui.TableSetupColumn("说明", ImGuiTableColumnFlags.WidthStretch, 0, 5);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 104, 6);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TableHeader("##sel");

        ImGui.TableNextColumn();
        ImGui.TableHeader("状态");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("这条库是启用还是停用；官方主库单独标出。");
        }

        if (showIconColumn)
        {
            ImGui.TableNextColumn();
            ImGui.TableHeader("##icon");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("仓库");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("点仓库名展开它提供的插件；右键可复制 / 在浏览器打开 / 加到我的库。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("插件数");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("这条库提供了多少个插件，其中多少个缺译文、多少个有你贡献的译文。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("说明");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("已启用 / 已停用 / 官方主库；停用的库可以就地点「启用」。");
        }

        ImGui.TableNextColumn();

        foreach (var group in this.repoGroups)
        {
            this.DrawRepoGroupRow(group, showIconColumn);
        }

        ImGui.EndTable();
    }

    private void DrawRepoGroupRow(RepoGroup group, bool showIconColumn)
    {
        var id = group.Url.Length == 0 ? "official" : group.Url;
        var expanded = this.expandedRepos.Contains(id);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        var allSelected = group.Plugins.Count > 0 && group.Plugins.All(x => this.selected.Contains(x.InternalName));
        if (ImGui.Checkbox("##selrepo-" + id, ref allSelected))
        {
            foreach (var plugin in group.Plugins)
            {
                if (allSelected)
                {
                    this.selected.Add(plugin.InternalName);
                }
                else
                {
                    this.selected.Remove(plugin.InternalName);
                }
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾上这条库里的全部插件");
        }

        ImGui.TableNextColumn();
        if (group.IsOfficial)
        {
            UiHelpers.ColoredText(UiHelpers.Info, "官方库");
        }
        else if (group.Enabled)
        {
            UiHelpers.ColoredText(UiHelpers.Good, "启用");
        }
        else
        {
            UiHelpers.ColoredText(UiHelpers.Muted, "停用");
        }

        if (showIconColumn)
        {
            ImGui.TableNextColumn();
        }

        // 仓库名：小按钮，点一下展开 / 收起
        ImGui.TableNextColumn();
        if (ImGui.SmallButton((expanded ? "\u25bc " : "\u25b6 ") + group.Short + "###grp-" + id))
        {
            if (expanded)
            {
                this.expandedRepos.Remove(id);
            }
            else
            {
                this.expandedRepos.Add(id);
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                (group.Url.Length == 0 ? "卫月官方主库（Dip17）" : group.Url)
                + "\n点一下展开它提供的插件；右键可复制 / 加库");
        }

        if (ImGui.BeginPopupContextItem("##ctxrepo-" + id))
        {
            if (group.Url.Length > 0)
            {
                if (ImGui.MenuItem("复制链接"))
                {
                    ImGui.SetClipboardText(group.Url);
                    this.SetStatus("已复制库链地址", isError: false);
                }

                if (ImGui.MenuItem("在浏览器打开"))
                {
                    OpenInBrowser(group.Url);
                }

                if (ImGui.MenuItem("加到我的库（待确认）"))
                {
                    this.repoInput = group.Url;
                    this.SetStatus("地址已填到操作条的输入框，点「添加到我的库」确认", isError: false);
                }
            }

            ImGui.EndPopup();
        }

        ImGui.TableNextColumn();
        ImGui.Text($"{group.Plugins.Count} 个");
        ImGui.SameLine();
        if (group.MissingCount > 0)
        {
            UiHelpers.ColoredText(UiHelpers.Bad, $"· 缺译 {group.MissingCount}");
        }
        else
        {
            UiHelpers.ColoredText(UiHelpers.Muted, "· 无缺译");
        }

        if (group.UserCount > 0)
        {
            ImGui.SameLine();
            UiHelpers.ColoredText(UiHelpers.Good, $"· 你译 {group.UserCount}");
        }

        ImGui.TableNextColumn();
        ImGui.TextDisabled(
            group.IsOfficial
                ? "卫月官方主库"
                : group.Enabled
                    ? "已经在你的库里"
                    : "这条库已停用");

        // 启用 / 加库按钮独占最后一列：列宽拖窄也不会把按钮挤到下一行、把行高顶起来
        ImGui.TableNextColumn();
        if (!group.IsOfficial && !group.Enabled && !string.IsNullOrWhiteSpace(group.Url))
        {
            if (ImGui.SmallButton("启用###en-" + id))
            {
                this.plugin.Repos.SetEnabled([group.Url], true, out _);
                this.plugin.Repos.Save(out _);
                this.plugin.Repos.TriggerReload(out _);
                this.plugin.TrackFirstSeen();
                this.rebuildPending = true;
                this.rebuildRepoPending = true;
                this.SetStatus("已启用这条库；卫月会重新抓取它的插件", isError: false);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("保留链接、把它重新启用；启用后卫月会去抓它的插件");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("加库###add-" + id))
            {
                this.repoInput = group.Url;
                this.SetStatus("地址已填到操作条的输入框，点「添加到我的库」确认", isError: false);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("把这条库加进你的第三方插件列表（会先自动备份）");
            }
        }

        if (!expanded)
        {
            return;
        }

        foreach (var plugin in group.Plugins)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            var picked = this.selected.Contains(plugin.InternalName);
            if (ImGui.Checkbox("##selp-" + plugin.InternalName, ref picked))
            {
                if (picked)
                {
                    this.selected.Add(plugin.InternalName);
                }
                else
                {
                    this.selected.Remove(plugin.InternalName);
                }
            }

            ImGui.TableNextColumn();
            var (label, color) = StatusOf(plugin);
            UiHelpers.ColoredText(color, label);

            if (showIconColumn)
            {
                ImGui.TableNextColumn();
                this.PeekIcon(plugin);
                if (this.iconHandles.TryGetValue(plugin.InternalName, out var handle) && !handle.IsNull)
                {
                    ImGui.Image(handle, new Vector2(18, 18));
                }
            }

            ImGui.TableNextColumn();
            ImGui.Indent(ImGui.GetFontSize());
            if (ImGui.SmallButton(plugin.DisplayName + "###rname-" + plugin.InternalName))
            {
                this.BeginEdit(plugin);
            }

            ImGui.Unindent(ImGui.GetFontSize());

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("点开详情，改三个字段");
            }

            ImGui.TableNextColumn();
            ImGui.TextDisabled($"{plugin.TranslatedFields}/{plugin.TotalFields + plugin.TemplateFields}");

            ImGui.TableNextColumn();
            UiHelpers.Fitted(
                FirstNonEmpty(plugin.OriginalPunchline, plugin.OriginalDescription),
                plugin.OriginalPunchline + "\n\n" + plugin.OriginalDescription);

            ImGui.TableNextColumn();
            if (ImGui.SmallButton("改进…###redit-" + plugin.InternalName))
            {
                this.BeginEdit(plugin);
            }
        }
    }

    /// <summary>选中项的操作条：批量翻译 + 加库。</summary>
    private void DrawActionBar()
    {
        var selectedCount = this.selected.Count;
        if (selectedCount == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"翻译所选（{selectedCount}）###BatchEdit"))
        {
            this.BeginBatchEdit();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("把勾选的插件逐个打开详情，改完一个下一个；每次保存都会存到本地。");
        }

        if (selectedCount == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("全选当前###SelectAll"))
        {
            if (this.view == "repos")
            {
                foreach (var group in this.repoGroups)
                {
                    foreach (var plugin in group.Plugins)
                    {
                        this.selected.Add(plugin.InternalName);
                    }
                }
            }
            else
            {
                foreach (var entry in this.filtered)
                {
                    this.selected.Add(entry.InternalName);
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("清空勾选###ClearSel"))
        {
            this.selected.Clear();
        }

        // ---- 批量加库：把勾选插件所在的库一次加进来 ----
        ImGui.SameLine();
        ImGui.TextDisabled("│");
        ImGui.SameLine();

        var addable = this.SelectedReposToAdd();
        if (addable.Count == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"添加到我的库（{addable.Count}）###AddSelectedRepos"))
        {
            this.AddSelectedRepositories(addable);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(addable.Count == 0
                ? "先在左边勾选插件（勾的插件所在库会被一次加进来）；\n已经在你的库里的会跳过。"
                : $"把勾选的插件所在的 {addable.Count} 条库加进你的第三方插件列表；\n添加前会自动备份一份仓库列表。");
        }

        if (addable.Count == 0)
        {
            ImGui.EndDisabled();
        }

        // ---- 撤回上一次「添加」 ----
        ImGui.SameLine();
        var lastAdd = this.LastAddRecord();
        if (lastAdd is null)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"撤回添加（{lastAdd?.Count ?? 0}）###UndoAdd"))
        {
            this.UndoLastAdd();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(lastAdd is null
                ? "还没有刚添加过的库；每次「添加到我的库」后都可以在这里一键撤回。"
                : $"把刚加进来的 {lastAdd.Count} 条库从列表里移除（{lastAdd.TimeUtc.ToLocalTime():HH:mm} 那次添加）");
        }

        if (lastAdd is null)
        {
            ImGui.EndDisabled();
        }

        // ---- 单条：粘地址添加 ----
        ImGui.SameLine();
        ImGui.TextDisabled("│");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(230);
        ImGui.InputTextWithHint("###AddRepoUrl", "或粘一条仓库地址…", ref this.repoInput, 512);
        ImGui.SameLine();
        var canAddUrl = this.repoInput.Trim().Length > 0;
        if (!canAddUrl)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("添加这条###AddRepoUrlBtn"))
        {
            this.AddRepository();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("把这一条库加进你的第三方插件列表（pluginmaster.json 地址）；\n添加前会自动备份，加错了用右边的「撤回添加」。");
        }

        if (!canAddUrl)
        {
            ImGui.EndDisabled();
        }
    }

    /// <summary>勾选的插件里，有哪些库链是本机还没有的（可以一次加进来）。</summary>
    private List<string> SelectedReposToAdd()
    {
        var result = new List<string>();
        if (this.selected.Count == 0 || this.index is not { Available: true })
        {
            return result;
        }

        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repo in this.plugin.Repos.ReadAll(out _))
        {
            if (!string.IsNullOrWhiteSpace(repo.Url))
            {
                existing.Add(repo.Url);
            }
        }

        foreach (var entry in this.index.All)
        {
            if (!this.selected.Contains(entry.InternalName) || string.IsNullOrWhiteSpace(entry.RepositoryUrl))
            {
                continue;
            }

            if (existing.Contains(entry.RepositoryUrl) || result.Contains(entry.RepositoryUrl))
            {
                continue;
            }

            result.Add(entry.RepositoryUrl);
        }

        return result;
    }

    /// <summary>最近一次「添加库」的撤回记录（不是最近的就不给撤）。</summary>
    private UndoRecord? LastAddRecord()
    {
        var history = this.plugin.Config.UndoHistory;
        if (history.Count == 0)
        {
            return null;
        }

        var last = history[^1];
        return last.Action == "add" ? last : null;
    }

    private void AddSelectedRepositories(List<string> urls)
    {
        if (urls.Count == 0)
        {
            return;
        }

        var ok = this.plugin.AddThirdPartyRepositories(urls, out var message);
        this.SetStatus(message, !ok);
        this.rebuildPending = true;
        this.rebuildRepoPending = true;
    }

    private void UndoLastAdd()
    {
        var ok = this.plugin.TryUndoLast(out var message);
        this.SetStatus(message, !ok);
        this.rebuildPending = true;
        this.rebuildRepoPending = true;
    }

    private void EnsureIndex()
    {
        if (this.buildTask is not null)
        {
            if (!this.buildTask.IsCompleted)
            {
                return;
            }

            var finished = this.buildTask;
            this.buildTask = null;
            this.index = finished.Status == TaskStatus.RanToCompletion ? finished.Result : null;
            this.rebuildPending = true;
            this.rebuildRepoPending = true;
            if (this.index is { Available: false })
            {
                this.retryAfter = DateTime.UtcNow.AddSeconds(10);
            }

            return;
        }

        if (this.index is { Available: true })
        {
            return;
        }

        if (DateTime.UtcNow < this.retryAfter)
        {
            return;
        }

        var table = this.plugin.SnapshotTable();
        this.buildTask = Task.Run(() => TranslationIndex.Build(table));
    }

    private void RebuildFiltered()
    {
        if (!this.rebuildPending)
        {
            return;
        }

        this.rebuildPending = false;
        this.filtered.Clear();

        if (this.index is not { Available: true })
        {
            return;
        }

        var query = this.search.Trim().ToLowerInvariant();

        // 三个范围全不勾 = 按插件名搜
        var anyScope = this.scopeName || this.scopePunchline || this.scopeDescription;

        foreach (var entry in this.index.All)
        {
            if (!this.plugin.Config.ContributeShowDisabled)
            {
                // 只看已启用的库（官方主库没有 RepositoryUrl，永远算启用）
                if (entry.RepositoryUrl is not null && !entry.RepositoryEnabled)
                {
                    continue;
                }
            }

            var pass = this.stateFilter switch
            {
                StateFilter.Missing => entry.State == "missing",
                StateFilter.Machine => entry.State == "machine",
                StateFilter.User => entry.HasUserTranslation,
                StateFilter.Review => entry.HasReview,
                _ => true,
            };

            if (!pass)
            {
                continue;
            }

            if (query.Length > 0 && !this.Match(entry, query, anyScope))
            {
                continue;
            }

            this.filtered.Add(entry);
        }
    }

    /// <summary>仓库视图的分组：按库链把筛选后的插件归类（官方主库单独一组）。</summary>
    private void RebuildRepoGroups()
    {
        if (!this.rebuildRepoPending)
        {
            return;
        }

        this.rebuildRepoPending = false;
        this.repoGroups.Clear();

        this.RebuildFiltered();
        foreach (var plugin in this.filtered)
        {
            var key = plugin.RepositoryUrl ?? string.Empty;
            var group = this.repoGroups.FirstOrDefault(x => string.Equals(x.Url, key, StringComparison.Ordinal));
            if (group is null)
            {
                group = new RepoGroup
                {
                    Url = key,
                    Short = key.Length == 0 ? "官方主库" : RepoShort(key),
                    Enabled = plugin.RepositoryEnabled,
                    IsOfficial = plugin.IsOfficial,
                };
                this.repoGroups.Add(group);
            }

            group.Plugins.Add(plugin);
        }

        // 缺译多的排前面
        this.repoGroups.Sort((a, b) =>
        {
            var byMissing = b.MissingCount.CompareTo(a.MissingCount);
            return byMissing != 0 ? byMissing : string.Compare(a.Short, b.Short, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>按勾选的搜索范围匹配关键词（三个范围全不勾时只看插件名）。</summary>
    private bool Match(TranslationIndexEntry entry, string query, bool anyScope)
    {
        if (anyScope)
        {
            if (this.scopeName && entry.DisplayName.ToLowerInvariant().Contains(query, StringComparison.Ordinal))
            {
                return true;
            }

            if (this.scopePunchline && entry.OriginalPunchline.ToLowerInvariant().Contains(query, StringComparison.Ordinal))
            {
                return true;
            }

            if (this.scopeDescription && entry.OriginalDescription.ToLowerInvariant().Contains(query, StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        return entry.DisplayName.ToLowerInvariant().Contains(query, StringComparison.Ordinal);
    }

    private void InvalidateIndex()
    {
        // 译文变了：重建搜索结果（不重建反射索引，够快）
        this.rebuildPending = true;
        this.rebuildRepoPending = true;
    }

    /// <summary>
    /// 改过译文后刷新受影响的条目状态（缺译 / 机器译 / 你译、完成度）。
    ///
    /// 早先的做法是在后台重新跑一遍 <see cref="TranslationIndex.Build"/>：那会从**后台线程**反射遍历
    /// 卫月的仓库/清单列表，而卫月自己的仓库重载（<c>ReloadAllReposAsync</c>）也会在别的线程动同一批集合，
    /// 撞上就会读到正在被改动的集合。改成就地重算：只动我们自己的缓存，不再碰卫月的内部结构。
    /// </summary>
    private void RefreshIndexSoon()
    {
        this.InvalidateIndex();
        this.refreshStatesRequested = true;
    }

    /// <summary>把每一行看得见的状态按当前词表重算（不反射卫月；一帧最多跑一次）。</summary>
    private void RefreshEntryStates()
    {
        if (!this.refreshStatesRequested)
        {
            return;
        }

        this.refreshStatesRequested = false;
        if (this.index is not { Available: true })
        {
            return;
        }

        foreach (var entry in this.index.All)
        {
            this.plugin.Table.TryGet(entry.InternalName, out var current);
            entry.RefreshFrom(current);
        }
    }

    // ------------------------------------------------------------------ 行

    private void DrawRow(TranslationIndexEntry entry, bool showIconColumn)
    {
        ImGui.TableNextRow();

        // 勾选
        ImGui.TableNextColumn();
        var picked = this.selected.Contains(entry.InternalName);
        if (ImGui.Checkbox("##sel-" + entry.InternalName, ref picked))
        {
            if (picked)
            {
                this.selected.Add(entry.InternalName);
            }
            else
            {
                this.selected.Remove(entry.InternalName);
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾上后可以用「翻译所选」逐个改，方便集中处理几条。");
        }

        // 状态
        ImGui.TableNextColumn();
        var (label, color) = StatusOf(entry);
        UiHelpers.ColoredText(color, label);
        if (ImGui.IsItemHovered())
        {
            var lines = new List<string>();
            if (entry.MissingFields.Count > 0)
            {
                lines.Add("缺：" + string.Join("、", entry.MissingFields.Select(FieldLabel)));
            }
            else
            {
                lines.Add("上游给的字段都有译文");
            }

            if (entry.ContributableFields.Count > 0)
            {
                lines.Add("上游没提供、你可以自己补：" + string.Join("、", entry.ContributableFields.Select(FieldLabel)));
            }

            if (entry.HasReview)
            {
                lines.Add("有字段的原文改过，建议复核");
            }

            ImGui.SetTooltip(string.Join("\n", lines));
        }

        // 图标
        if (showIconColumn)
        {
            ImGui.TableNextColumn();
            this.PeekIcon(entry);
            if (this.iconHandles.TryGetValue(entry.InternalName, out var handle) && !handle.IsNull)
            {
                ImGui.Image(handle, new Vector2(18, 18));
            }
        }

        // 插件名（小按钮，点开详情）
        ImGui.TableNextColumn();
        if (ImGui.SmallButton(entry.DisplayName + "###name-" + entry.InternalName))
        {
            this.BeginEdit(entry);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(entry.DisplayName + "\n" + entry.InternalName + "\n点开详情，改三个字段");
        }

        // 来源库
        ImGui.TableNextColumn();
        if (entry.IsOfficial)
        {
            UiHelpers.ColoredText(UiHelpers.Info, "官方库");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("卫月官方主库（Dip17）里的插件");
            }
        }
        else if (entry.RepositoryUrl is null)
        {
            ImGui.TextDisabled("—");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("读不到来源地址");
            }
        }
        else
        {
            if (ImGui.SmallButton(RepoShort(entry.RepositoryUrl) + "###repo-" + entry.InternalName))
            {
                ImGui.SetClipboardText(entry.RepositoryUrl);
                this.SetStatus("已复制库链地址", isError: false);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(entry.RepositoryUrl + "\n点一下复制；右键可加入我的库");
            }

            if (ImGui.BeginPopupContextItem("##ctxrepo-" + entry.InternalName))
            {
                if (ImGui.MenuItem("复制链接"))
                {
                    ImGui.SetClipboardText(entry.RepositoryUrl);
                }

                if (ImGui.MenuItem("在浏览器打开"))
                {
                    OpenInBrowser(entry.RepositoryUrl);
                }

                if (ImGui.MenuItem("加到我的库（待确认）"))
                {
                    this.repoInput = entry.RepositoryUrl;
                    this.SetStatus("地址已填到操作条的输入框，点「添加」确认", isError: false);
                }

                ImGui.EndPopup();
            }
        }

        // 原文
        ImGui.TableNextColumn();
        var preview = FirstNonEmpty(entry.OriginalPunchline, entry.OriginalDescription);
        UiHelpers.Fitted(preview, entry.OriginalPunchline + "\n\n" + entry.OriginalDescription);

        // 译文
        ImGui.TableNextColumn();
        var current = entry.Entry?.Punchline is { HasTranslation: true } p
            ? p.Translated
            : entry.Entry?.Name is { HasTranslation: true } n ? n.Translated : string.Empty;
        if (string.IsNullOrWhiteSpace(current))
        {
            ImGui.TextDisabled("—");
        }
        else
        {
            UiHelpers.Fitted(current, current);
        }

        if (ImGui.IsItemHovered())
        {
            var total = entry.TotalFields + entry.TemplateFields;
            ImGui.SetTooltip($"完成度 {entry.TranslatedFields}/{total}（{entry.Completion * 100:0}%）");
        }

        // 操作
        ImGui.TableNextColumn();
        if (ImGui.SmallButton((entry.State == "missing" ? "补上…" : "改进…") + "###edit-" + entry.InternalName))
        {
            this.BeginEdit(entry);
        }
    }

    /// <summary>状态列的文字与颜色（表格与仓库视图共用）。</summary>
    private static (string Label, Vector4 Color) StatusOf(TranslationIndexEntry entry)
    {
        var label = entry.State switch
        {
            "missing" => "缺译",
            "user" => "你译",
            _ => "机器译",
        };

        if (entry.HasReview)
        {
            label += " !";
        }

        var color = entry.State switch
        {
            "missing" => UiHelpers.Bad,
            "user" => UiHelpers.Good,
            _ => UiHelpers.Info,
        };

        return (label, color);
    }

    // ------------------------------------------------------------------ 编辑弹窗

    private void BeginEdit(TranslationIndexEntry entry)
    {
        this.editingBatch.Clear();
        this.ApplyEditing(entry);
    }

    private void ApplyEditing(TranslationIndexEntry entry)
    {
        this.editing = entry;
        this.editOpened = false;

        // 下面那一栏已经有这条译文时，直接进原来那份改（不新建、不堆历史版本）
        this.editName = this.Pending(entry, "Name") ?? entry.Entry?.Name?.Translated ?? string.Empty;
        this.editPunchline = this.Pending(entry, "Punchline") ?? entry.Entry?.Punchline?.Translated ?? string.Empty;
        this.editDescription = this.Pending(entry, "Description") ?? entry.Entry?.Description?.Translated ?? string.Empty;
    }

    /// <summary>下栏（待提交）里这条字段的译文；没有就是 null。</summary>
    private string? Pending(TranslationIndexEntry entry, string field)
        => this.store.Find(entry.InternalName, field)?.Translated;

    /// <summary>批量翻译：把勾选的插件排成一列，改完一个自动接下一个。</summary>
    private void BeginBatchEdit()
    {
        var queue = this.index?.All.Where(x => this.selected.Contains(x.InternalName)).ToList() ?? [];
        if (queue.Count == 0)
        {
            return;
        }

        this.editingBatch = queue;
        this.ApplyEditing(queue[0]);
    }

    /// <summary>批量模式下切到下一个（没有下一个就关掉弹窗）。</summary>
    private void AdvanceBatch()
    {
        var current = this.editing;
        if (current is null || this.editingBatch.Count == 0)
        {
            this.editing = null;
            ImGui.CloseCurrentPopup();
            return;
        }

        var index = this.editingBatch.IndexOf(current);
        if (index < 0 || index + 1 >= this.editingBatch.Count)
        {
            this.editing = null;
            this.editingBatch.Clear();
            ImGui.CloseCurrentPopup();
            return;
        }

        this.ApplyEditing(this.editingBatch[index + 1]);
    }

    private void DrawEditPopup()
    {
        if (this.editing is null)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(700, 560), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal("翻译###EditTranslation", ImGuiWindowFlags.NoSavedSettings))
        {
            return;
        }

        var entry = this.editing;
        ImGui.Text(entry.DisplayName);
        ImGui.SameLine();
        ImGui.TextDisabled(entry.InternalName);
        if (entry.IsOfficial)
        {
            ImGui.SameLine();
            UiHelpers.ColoredText(UiHelpers.Info, "官方库");
        }
        else if (entry.RepositoryUrl is not null)
        {
            ImGui.SameLine();
            UiHelpers.Fitted(entry.RepositoryUrl, entry.RepositoryUrl);
        }

        if (this.editingBatch.Count > 0)
        {
            var done = this.editingBatch.IndexOf(entry) + 1;
            ImGui.SameLine();
            ImGui.TextDisabled($"· 批量 {done}/{this.editingBatch.Count}");
        }

        ImGui.Separator();

        this.DrawField("插件名", "Name", entry.OriginalName, ref this.editName);
        this.DrawField("一行简介", "Punchline", entry.OriginalPunchline, ref this.editPunchline);
        this.DrawField("插件详情", "Description", entry.OriginalDescription, ref this.editDescription);

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
            this.SaveEdit();
        }

        if (this.editingBatch.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("跳过这个###SkipEdit", new Vector2(120, 0)))
            {
                this.AdvanceBatch();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("取消###CancelEdit", new Vector2(120, 0)))
        {
            this.editing = null;
            this.editingBatch.Clear();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"待提交 {this.store.Count} 条");

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
        var entry = this.editing;
        if (entry is null)
        {
            return;
        }

        var changed = 0;
        this.ruleIssue = null;
        changed += this.Commit(entry, "Name", entry.OriginalName, this.editName);
        changed += this.Commit(entry, "Punchline", entry.OriginalPunchline, this.editPunchline);
        changed += this.Commit(entry, "Description", entry.OriginalDescription, this.editDescription);

        if (this.ruleIssue is not null)
        {
            this.SetStatus($"有字段没存上（规则拦下）：{this.ruleIssue}", isError: true);
        }
        else if (changed > 0)
        {
            this.plugin.Table.SaveToConfigDirectory(out var error);
            this.plugin.ApplyTranslations();
            this.RefreshIndexSoon();
            this.SetStatus(
                error is null
                    ? $"已存到本地 {changed} 处译文；攒够后在下面一条提交"
                    : $"已记下 {changed} 处译文，但写盘失败：{error}",
                error is not null);
        }
        else
        {
            this.SetStatus("没有改动", isError: false);
        }

        if (this.editingBatch.Count > 0)
        {
            this.AdvanceBatch();
            return;
        }

        this.editing = null;
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
            this.ruleIssue = $"{FieldLabel(field)}：{reason}";
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
        var (previousText, previousSource) = this.plugin.Table.GetTranslation(entry.InternalName, field);
        var existing = this.store.Find(entry.InternalName, field);

        this.plugin.Table.MarkUserTranslation(entry.InternalName, field, original, next);
        this.store.AddOrReplace(new ContributionRecord
        {
            Id = $"{entry.InternalName}:{field}:{DateTime.Now:yyyyMMddHHmmss}",
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

    // ------------------------------------------------------------------ 下面那一栏：改过的译文 / 历史提交记录

    /// <summary>
    /// 下半栏（像编辑器下方的工作区）：页签 = 待提交 + 每一批历史提交（命名用日期，悬停看具体时间）。
    /// 待提交里可以直接改 / 删 / 清空 / 撤回；历史快照只读。
    /// </summary>
    private void DrawWorkspace(float height)
    {
        if (ImGui.BeginChild("###ContributeWorkspace", new Vector2(0, height)))
        {
            var pendingCount = this.store.Count;
            var historyCount = this.store.History.Sum(x => x.Contributions.Count);

            if (ImGui.BeginTabBar("###ContributeWorkspaceTabs", ImGuiTabBarFlags.None))
            {
                var pendingLabel = pendingCount > 0 ? $"待提交（{pendingCount}）###ws-pending" : "待提交###ws-pending";
                if (ImGui.BeginTabItem(pendingLabel))
                {
                    this.workspaceTab = -1;
                    ImGui.EndTabItem();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("你改过、还没提交的译文；可直接改或删。");
                }

                // 历史提交：合并成一个页签（之前一批一个页签，条数一多就排满了）；
                // 批次信息在表格里用组头体现，见 DrawHistoryTable。
                var historyLabel = historyCount > 0 ? $"历史提交（{historyCount}）###ws-history" : "历史提交###ws-history";
                if (ImGui.BeginTabItem(historyLabel))
                {
                    this.workspaceTab = 0;
                    ImGui.EndTabItem();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(this.store.History.Count == 0
                        ? "还没有提交过；提交后每次都会在这台机器上留一份，方便回看、加回待提交或删掉。"
                        : $"本机留档：{this.store.History.Count} 批 / {historyCount} 条；可整批或逐条加回待提交，也可以删掉。");
                }

                ImGui.EndTabBar();
            }

            this.DrawWorkspaceButtons();

            if (this.workspaceTab < 0 || this.store.History.Count == 0)
            {
                this.workspaceTab = -1;
                this.DrawPendingTable();
            }
            else
            {
                this.DrawHistoryTable();
            }
        }

        ImGui.EndChild();
    }

    /// <summary>下栏按钮组：待提交一套（提交/删除/清空/撤回）、历史提交一套（加回/删除/清空），末尾统一画确认框。</summary>
    private void DrawWorkspaceButtons()
    {
        if (this.workspaceTab >= 0)
        {
            this.DrawHistoryButtons();
        }
        else
        {
            this.DrawPendingButtons();
        }

        this.DrawWorkspaceDialogs();
    }

    /// <summary>待提交那一栏的按钮。</summary>
    private void DrawPendingButtons()
    {
        var pending = this.store.Count;
        var selectedCount = this.selectedRecords.Count;

        if (pending == 0 || this.submitBusy)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(this.submitBusy ? "正在提交…###SubmitContrib" : $"一键提交全部（{pending}）###SubmitContrib"))
        {
            this.SubmitContributions();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(
                pending == 0
                    ? "先在下面攒几条译文（上面表格里点「补上… / 改进…」）"
                    : "把「待提交」这一栏全部发给维护者审核（推送）；\n推送成功才会清空待提交，并在这台机器的历史提交里留一份。\n"
                      + "推送失败时内容一条不动，并导出到本地文件。");
        }

        if (pending == 0 || this.submitBusy)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (selectedCount == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"删除勾选（{selectedCount}）###DeleteContrib"))
        {
            this.DeleteSelectedContributions();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(selectedCount == 0
                ? "勾选下面表格里要删的条目；删掉后词表会恢复成改之前的样子，也可以用「撤回」找回。"
                : $"删掉勾选的 {selectedCount} 条，并把词表恢复成改之前的样子");
        }

        if (selectedCount == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (ImGui.Button("清空待提交###ClearContrib"))
        {
            ImGui.OpenPopup("清空待提交###ConfirmClear");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("把「待提交」整栏清掉（有二次确认，而且可以用「撤回」找回）");
        }

        ImGui.SameLine();
        if (!this.store.CanUndo)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("撤回删除###UndoContrib"))
        {
            this.UndoContributions();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(this.store.CanUndo ? "把上一次删除 / 清空恢复回来" : "还没有可撤回的删除或清空");
        }

        if (!this.store.CanUndo)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(selectedCount > 0
            ? $"已选 {selectedCount} 条 · 这一栏是你改过、还没提交的译文"
            : "这一栏是你改过、还没提交的译文；提交后会挪进「历史提交」");
    }

    /// <summary>历史提交那一栏的按钮：整批/逐条加回待提交、删除所选、清空。</summary>
    private void DrawHistoryButtons()
    {
        var selected = this.SelectedHistoryRecords().Count;
        var total = this.store.History.Sum(x => x.Contributions.Count);

        if (selected == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"移回待提交（{selected}）###RecallHistory"))
        {
            this.RecallSelectedHistory();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(selected == 0
                ? "先在上面勾几条（或勾某一批的组头）；加回后可以从「待提交」里改了再交一次"
                : $"把勾选的 {selected} 条从历史提交移回「待提交」：\n历史提交里不再保留这几条（是移动，不是复制）");
        }

        if (selected == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (selected == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button($"删除所选（{selected}）###DeleteHistory"))
        {
            ImGui.OpenPopup("删除所选###ConfirmDeleteHistory");
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(selected == 0
                ? "先勾几条要删的记录"
                : $"从这台机器的历史提交里删掉这 {selected} 条（有二次确认）");
        }

        if (selected == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        if (total == 0)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("清空历史提交###ClearHistory"))
        {
            ImGui.OpenPopup("清空历史提交###ConfirmClearHistory");
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("把这台机器记的提交历史整个清掉（有二次确认）；已经交出去的译文不受影响");
        }

        if (total == 0)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(selected > 0
            ? $"已选 {selected} 条 · 共 {total} 条 · {this.store.History.Count} 批（记在这台机器上）"
            : $"共 {total} 条 · {this.store.History.Count} 批（记在这台机器上）");

        this.DrawHistoryDetailPopup();
    }

    /// <summary>下栏的确认框：清空待提交 / 删除所选历史 / 清空历史提交（默认焦点都在「取消」）。</summary>
    private void DrawWorkspaceDialogs()
    {
        ImGui.SetNextWindowSize(new Vector2(460, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("清空待提交###ConfirmClear", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("清空后待提交这一栏就空了，词表会恢复成你改之前的样子（之后可以用「撤回删除」找回）。确定吗？");
            ImGui.Spacing();
            if (ImGui.Button("取消", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();
            ImGui.SameLine();
            if (ImGui.Button("清空", new Vector2(120, 0)))
            {
                this.clearRequested = true;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (this.clearRequested)
        {
            this.clearRequested = false;
            this.ClearContributions();
        }

        var selected = this.SelectedHistoryRecords();
        ImGui.SetNextWindowSize(new Vector2(500, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("删除所选###ConfirmDeleteHistory", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"从这台机器的历史提交里删掉勾选的 {selected.Count} 条？");
            ImGui.TextDisabled("已经交出去的译文不受影响，游戏里的译文也不会变；删掉后本机无法恢复。");
            ImGui.Spacing();
            this.DrawHistorySamples(selected);
            ImGui.Spacing();
            if (ImGui.Button("取消", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();
            ImGui.SameLine();
            if (ImGui.Button("删除", new Vector2(120, 0)))
            {
                this.deleteHistoryRequested = true;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (this.deleteHistoryRequested)
        {
            this.deleteHistoryRequested = false;
            this.DeleteSelectedHistory();
        }

        var total = this.store.History.Sum(x => x.Contributions.Count);
        ImGui.SetNextWindowSize(new Vector2(460, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("清空历史提交###ConfirmClearHistory", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped($"把这台机器上记的 {total} 条提交历史（{this.store.History.Count} 批）全部删掉？");
            ImGui.TextDisabled("已经交出去的译文不受影响，游戏里的译文也不会变；删掉后本机无法恢复。");
            ImGui.Spacing();
            if (ImGui.Button("取消", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.SetItemDefaultFocus();
            ImGui.SameLine();
            if (ImGui.Button("全部清空", new Vector2(120, 0)))
            {
                this.clearHistoryRequested = true;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        if (this.clearHistoryRequested)
        {
            this.clearHistoryRequested = false;
            this.ClearHistoryRecords();
        }
    }

    /// <summary>确认框里列前几条样本，方便核对到底删哪些。</summary>
    private void DrawHistorySamples(IReadOnlyList<ContributionRecord> records)
    {
        foreach (var record in records.Take(3))
        {
            var text = record.Translated.Replace('\n', ' ');
            if (text.Length > 40)
            {
                text = text[..40] + "…";
            }

            ImGui.TextDisabled($"{record.TimeLocal:MM-dd HH:mm} · {record.DisplayName} / {FieldLabel(record.Field)}：「{text}」");
        }

        if (records.Count > 3)
        {
            ImGui.TextDisabled($"等 {records.Count} 条");
        }
    }

    /// <summary>待提交列表：勾选 + 行内「改」「删」。</summary>
    private void DrawPendingTable()
    {
        var records = this.store.Records;
        if (records.Count == 0)
        {
            ImGui.TextDisabled("还没改过任何译文。在上面点插件名或「补上…」，改完就会出现在这里。");
            return;
        }

        var tableHeight = MathF.Max(80f, ImGui.GetContentRegionAvail().Y - 4f);
        if (!ImGui.BeginTable(
                "###PendingRows",
                5,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings,
                new Vector2(0, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 26, 0);
        ImGui.TableSetupColumn("插件", ImGuiTableColumnFlags.WidthFixed, 170, 1);
        ImGui.TableSetupColumn("字段", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 76, 2);
        ImGui.TableSetupColumn("译文", ImGuiTableColumnFlags.WidthStretch, 0, 3);
        ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 56, 4);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TableHeader("##sel");
        ImGui.TableNextColumn();
        ImGui.TableHeader("插件");
        ImGui.TableNextColumn();
        ImGui.TableHeader("字段");
        ImGui.TableNextColumn();
        ImGui.TableHeader("译文");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("悬停看全文；点「改」重新编辑（同一个插件一个字段只会有一条）");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("操作");

        var clipper = new ImGuiListClipper();
        clipper.Begin(records.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var record = records[i];
                var key = RecordKey(record);
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                var picked = this.selectedRecords.Contains(key);
                if (ImGui.Checkbox("##rec-" + key, ref picked))
                {
                    if (picked)
                    {
                        this.selectedRecords.Add(key);
                    }
                    else
                    {
                        this.selectedRecords.Remove(key);
                    }
                }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton(record.DisplayName + "###recname-" + key))
                {
                    this.EditExistingContribution(record);   // 点名字也能进详情，跟上面一致
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(record.DisplayName + "\n" + record.InternalName + "\n点开详情，改这一条译文");
                }

                ImGui.TableNextColumn();
                ImGui.TextDisabled(FieldLabel(record.Field));

                ImGui.TableNextColumn();
                UiHelpers.Fitted(record.Translated.Replace('\n', ' '), record.Translated);

                ImGui.TableNextColumn();
                if (ImGui.SmallButton("改###editrec-" + key))
                {
                    this.EditExistingContribution(record);
                }
            }
        }

        ImGui.EndTable();
    }

    /// <summary>历史提交：合并成一张表，内部按批次分组（组头 = 提交时间 + 条数 + 整批勾选）。</summary>
    private void DrawHistoryTable()
    {
        this.EnsureHistoryRows();

        if (this.historyRows.Count == 0)
        {
            ImGui.TextDisabled("还没有提交过。点「一键提交全部」之后，每次都会在这台机器上留一份，方便回看、加回待提交或删掉。");
            return;
        }

        var tableHeight = MathF.Max(60f, ImGui.GetContentRegionAvail().Y - 4f);
        if (!ImGui.BeginTable(
                "###HistoryRows",
                5,   // ##sel + 插件 + 字段 + 译文 + 操作
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings,
                new Vector2(0, tableHeight)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 26, 0);
        ImGui.TableSetupColumn("插件", ImGuiTableColumnFlags.WidthFixed, 170, 1);
        ImGui.TableSetupColumn("字段", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 76, 2);
        ImGui.TableSetupColumn("译文", ImGuiTableColumnFlags.WidthStretch, 0, 3);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, 60, 4);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        ImGui.TableHeader("##sel");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾选要加回待提交或要删掉的记录；批次那一行的勾选框 = 整批全选。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("插件");
        ImGui.TableNextColumn();
        ImGui.TableHeader("字段");
        ImGui.TableNextColumn();
        ImGui.TableHeader("译文");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("这一条当时交出去的译文；悬停看全文，点「查看…」看完整内容。");
        }

        ImGui.TableNextColumn();
        ImGui.TableHeader("操作");

        var clipper = new ImGuiListClipper();
        clipper.Begin(this.historyRows.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                if (i < 0 || i >= this.historyRows.Count)
                {
                    continue;
                }

                var row = this.historyRows[i];
                if (row.IsHeader)
                {
                    this.DrawHistoryHeaderRow(row);
                }
                else
                {
                    this.DrawHistoryRecordRow(row);
                }
            }
        }

        ImGui.EndTable();
    }

    /// <summary>把历史提交铺成「组头 + 记录」的平表（供 clipper 用）；只在数据变过时重建。</summary>
    private void EnsureHistoryRows()
    {
        if (!this.historyRowsDirty)
        {
            return;
        }

        this.historyRowsDirty = false;
        this.historyRows.Clear();

        for (var i = this.store.History.Count - 1; i >= 0; i--)   // 最新的一批在最上面
        {
            var batch = this.store.History[i];
            this.historyRows.Add(new HistoryRow { Batch = batch });
            foreach (var record in batch.Contributions)
            {
                this.historyRows.Add(new HistoryRow { Batch = batch, Record = record });
            }
        }
    }

    /// <summary>批次那一行：整批勾选（部分选中时显半选）+ 提交时间与条数。</summary>
    private void DrawHistoryHeaderRow(HistoryRow row)
    {
        var batch = row.Batch!;
        var total = batch.Contributions.Count;
        var selected = batch.Contributions.Count(x => this.selectedHistory.Contains(x.Id));

        ImGui.TableNextRow();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(0.11f, 0.15f, 0.20f, 1f)));

        ImGui.TableNextColumn();
        var flags = selected == total && total > 0 ? 3 : selected > 0 ? 1 : 0;
        if (ImGui.CheckboxFlags("##batch-" + batch.SubmittedLocal.Ticks, ref flags, 3))
        {
            var select = flags != 0;
            foreach (var record in batch.Contributions)
            {
                if (select)
                {
                    this.selectedHistory.Add(record.Id);
                }
                else
                {
                    this.selectedHistory.Remove(record.Id);
                }
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"这一批 {total} 条（{batch.TimeLabel} 提交）\n点一下 = 全选 / 全不选这一批");
        }

        ImGui.TableNextColumn();
        ImGui.TextDisabled(row.BatchLabel);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"{batch.TimeLabel} 提交 · 共 {total} 条");
        }

        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
    }

    private void DrawHistoryRecordRow(HistoryRow row)
    {
        var record = row.Record!;
        var picked = this.selectedHistory.Contains(record.Id);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (ImGui.Checkbox("##his-" + record.Id, ref picked))
        {
            if (picked)
            {
                this.selectedHistory.Add(record.Id);
            }
            else
            {
                this.selectedHistory.Remove(record.Id);
            }
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(record.DisplayName);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(record.DisplayName + "\n" + record.InternalName);
        }

        ImGui.TableNextColumn();
        ImGui.TextDisabled(FieldLabel(record.Field));

        ImGui.TableNextColumn();
        UiHelpers.Fitted(record.Translated.Replace('\n', ' '), record.Translated);

        ImGui.TableNextColumn();
        if (ImGui.SmallButton("查看…###hisview-" + record.Id))
        {
            this.viewingHistory = record;
            ImGui.OpenPopup("留档详情###HistoryDetail");
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("看全文（只读）；要改就先「加入待提交」");
        }
    }

    /// <summary>留档条目的只读详情：看全文与两个时间，要改就先加入待提交。</summary>
    private void DrawHistoryDetailPopup()
    {
        if (!ImGui.BeginPopup("留档详情###HistoryDetail"))
        {
            return;
        }

        var record = this.viewingHistory;
        if (record is null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        ImGui.TextUnformatted(record.DisplayName + " · " + FieldLabel(record.Field));
        ImGui.TextDisabled(record.InternalName);
        ImGui.Separator();
        ImGui.TextDisabled($"写于 {record.TimeLocal:yyyy-MM-dd HH:mm}   ·   提交于 {this.HistorySubmittedAt(record):yyyy-MM-dd HH:mm}");
        ImGui.Spacing();
        ImGui.TextDisabled("交给维护者的译文");
        var text = record.Translated;
        ImGui.InputTextMultiline(
            "###hisdetail",
            ref text,
            4096,
            new Vector2(460, ImGui.GetTextLineHeight() * 6),
            ImGuiInputTextFlags.ReadOnly);
        ImGui.Spacing();
        if (ImGui.Button("移回待提交", new Vector2(140, 0)))
        {
            this.RecallRecords([record]);
            ImGui.CloseCurrentPopup();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("从历史提交移回「待提交」，可以改了再交一次（历史提交里不再保留这一条）");
        }

        ImGui.SameLine();
        if (ImGui.Button("关闭", new Vector2(120, 0)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    /// <summary>这条留档是第几批交的（找不到就退回它自己的写入时间）。</summary>
    private DateTime HistorySubmittedAt(ContributionRecord record)
    {
        foreach (var batch in this.store.History)
        {
            if (batch.Contributions.Contains(record))
            {
                return batch.SubmittedLocal;
            }
        }

        return record.TimeLocal;
    }

    private void RecallSelectedHistory() => this.RecallRecords(this.SelectedHistoryRecords());

    /// <summary>把留档里的记录放回「待提交」（是移动：留档里不再保留），并写回词表。</summary>
    private void RecallRecords(IReadOnlyList<ContributionRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        // 记下「现在的值」：以后删掉这条草稿时会恢复成加回来之前的样子
        foreach (var record in records)
        {
            var (text, source) = this.plugin.Table.GetTranslation(record.InternalName, record.Field);
            record.Previous = text;
            record.PreviousSource = source;
            record.TimeLocal = DateTime.Now;
        }

        var recalled = this.store.RecallFromHistory(records);
        this.selectedHistory.Clear();

        foreach (var record in recalled)
        {
            this.plugin.Table.MarkUserTranslation(record.InternalName, record.Field, record.Original, record.Translated);
        }

        if (recalled.Count > 0)
        {
            this.plugin.Table.SaveToConfigDirectory(out _);
            this.plugin.ApplyTranslations();
        }

        this.RefreshIndexSoon();
        this.SetStatus(
            recalled.Count == 0
                ? "这几条已经不在历史提交里了"
                : $"已把 {recalled.Count} 条放回「待提交」；历史提交里不再保留这几条",
            recalled.Count == 0);
    }

    private void DeleteSelectedHistory()
    {
        var removed = this.store.RemoveFromHistory(this.SelectedHistoryRecords());
        this.selectedHistory.Clear();
        this.SetStatus($"已从历史提交里删掉 {removed} 条（这台机器上的记录；已交出去的译文不受影响）", isError: false);
    }

    private void ClearHistoryRecords()
    {
        var count = this.store.ClearHistory();
        this.selectedHistory.Clear();
        this.SetStatus($"已清空历史提交（{count} 条，这台机器上的记录）", isError: false);
    }

    private List<ContributionRecord> SelectedHistoryRecords()
    {
        var result = new List<ContributionRecord>();
        foreach (var batch in this.store.History)
        {
            foreach (var record in batch.Contributions)
            {
                if (this.selectedHistory.Contains(record.Id))
                {
                    result.Add(record);
                }
            }
        }

        return result;
    }

    private static string RecordKey(ContributionRecord record) => record.InternalName + ":" + record.Field;

    /// <summary>从下面那一栏打开已有译文：直接进原来的那份改，不新建。</summary>
    private void EditExistingContribution(ContributionRecord record)
    {
        var entry = this.index?.All.FirstOrDefault(
            x => string.Equals(x.InternalName, record.InternalName, StringComparison.Ordinal));
        if (entry is null)
        {
            this.SetStatus($"{record.DisplayName} 现在不在你的插件库里，只能删掉这条", isError: true);
            return;
        }

        this.BeginEdit(entry);
    }

    private void DeleteSelectedContributions()
    {
        var deleted = 0;
        foreach (var key in this.selectedRecords.ToList())
        {
            var separator = key.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var internalName = key[..separator];
            var field = key[(separator + 1)..];
            var record = this.store.Remove(internalName, field);
            if (record is null)
            {
                continue;
            }

            this.plugin.Table.SetTranslation(internalName, field, record.Previous, record.PreviousSource);
            deleted++;
        }

        this.selectedRecords.Clear();
        if (deleted > 0)
        {
            this.plugin.Table.SaveToConfigDirectory(out _);
            this.plugin.ApplyTranslations();
            this.InvalidateIndex();
        }

        this.SetStatus($"已删除 {deleted} 条待提交译文；词表已恢复成改之前的样子（可用「撤回」找回）", isError: false);
    }

    private void ClearContributions()
    {
        var count = this.store.Count;
        foreach (var record in this.store.Records.ToList())
        {
            this.plugin.Table.SetTranslation(record.InternalName, record.Field, record.Previous, record.PreviousSource);
        }

        this.store.ClearAll();
        this.selectedRecords.Clear();
        this.plugin.Table.SaveToConfigDirectory(out _);
        this.plugin.ApplyTranslations();
        this.InvalidateIndex();
        this.SetStatus($"已清空 {count} 条待提交译文（可用「撤回」找回）", isError: false);
    }

    private void UndoContributions()
    {
        var restored = this.store.Undo(out var message);
        foreach (var record in restored)
        {
            this.plugin.Table.MarkUserTranslation(record.InternalName, record.Field, record.Original, record.Translated);
        }

        if (restored.Count > 0)
        {
            this.plugin.Table.SaveToConfigDirectory(out _);
            this.plugin.ApplyTranslations();
        }

        this.RefreshIndexSoon();
        this.SetStatus(message, restored.Count == 0);
    }

    /// <summary>
    /// 一键提交：把这一批译文直接推给维护者审核（server3 酱）。
    /// 推送是同步的：**只有推送成功才归档 + 清空**；失败就原样留着并导出到本地，绝不谎报提交成功。
    /// </summary>
    private void SubmitContributions()
    {
        if (this.store.Count == 0 || this.submitBusy)
        {
            return;
        }

        this.submitBusy = true;
        var count = this.store.Count;
        var title = $"FireGaze 翻译贡献 {DateTime.Now:yyyy-MM-dd HH:mm}（{count} 条）";
        var markdown = this.BuildSubmitMarkdown();
        var url = this.plugin.Config.PushUrl;

        _ = Task.Run(async () =>
        {
            var (ok, message) = await PushNotifier
                .SendAsync(url, title, markdown, CancellationToken.None)
                .ConfigureAwait(false);

            this.submitBusy = false;

            if (!ok)
            {
                // 没推成功：内容一条不动，顺手导出一份文件，玩家可以自己贴给维护者
                var path = this.store.SaveExportFile();
                this.extraHint = path is null
                    ? $"{message}；内容还在「待提交」里，可以稍后再点一次。"
                    : $"{message}；已把这一批导出到 {path}，内容还在「待提交」里。";
                this.SetStatus("推送失败（待提交没有动）", isError: true);
                return;
            }

            this.store.ArchiveSubmission();
            this.workspaceTab = 0;   // 提交后切到「历史提交」，让玩家看到刚才那一批
            this.selectedRecords.Clear();
            this.extraHint = null;
            this.SetStatus($"已提交 {count} 条：{message}；可在「历史提交」页签里回看", isError: false);
        });
    }

    /// <summary>推送给维护者的正文：人看的清单 + 可直接落盘的 JSON。</summary>
    private string BuildSubmitMarkdown()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"# FireGaze 翻译贡献（{this.store.Count} 条）");
        builder.AppendLine();
        builder.AppendLine($"- 提交时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine($"- 条数：{this.store.Count}");
        builder.AppendLine("- 说明：以下译文由玩家提交，请以 user 来源写入词表；机器翻译不得覆盖。");
        builder.AppendLine();
        builder.AppendLine("| 插件 | 字段 | 译文 |");
        builder.AppendLine("|---|---|---|");
        foreach (var record in this.store.Records)
        {
            var text = record.Translated.Replace('\n', ' ').Replace("|", "\\|");
            if (text.Length > 80)
            {
                text = text[..80] + "…";
            }

            builder.AppendLine($"| {record.InternalName} | {FieldLabel(record.Field)} | {text} |");
        }

        builder.AppendLine();
        builder.AppendLine("```json");
        builder.AppendLine(this.store.BuildJson());
        builder.AppendLine("```");
        return builder.ToString();
    }

    // ------------------------------------------------------------------ 加库

    private void AddRepository()
    {
        var url = this.repoInput.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            this.repoMessage = "这看起来不是一条 http(s) 地址";
            this.statusIsError = true;
            return;
        }

        var added = this.plugin.AddThirdPartyRepository(url, out var message);
        this.repoMessage = message;
        this.statusIsError = !added;
        if (added)
        {
            this.repoInput = string.Empty;
        }
    }

    // ------------------------------------------------------------------ 图标

    private void PeekIcon(TranslationIndexEntry entry)
    {
        if (!entry.DeclaresIcon || this.iconHandles.ContainsKey(entry.InternalName) ||
            this.iconMisses.Contains(entry.InternalName))
        {
            return;
        }

        // 只用本地已缓存的（卫月内存缓存或我们的落盘缓存），绝不触发下载
        var installed = new InstalledPluginEntry
        {
            InternalName = entry.InternalName,
            DisplayName = entry.DisplayName,
            IconUrl = entry.IconUrl,
            RawPlugin = entry.RawPlugin!,
            Manifest = entry.Manifest,
            IsThirdParty = entry.IsThirdParty,
        };

        if (this.plugin.Icons.TryGetHandle(installed, out var cached) && !cached.IsNull)
        {
            this.iconHandles[entry.InternalName] = cached;
        }
        else if (PluginIconLookup.TryPeekHandle(installed, out var handle) && !handle.IsNull)
        {
            this.iconHandles[entry.InternalName] = handle;
        }
        else
        {
            this.iconMisses.Add(entry.InternalName);
        }
    }

    // ------------------------------------------------------------------ 小工具

    private void FilterRadio(StateFilter value, string label)
    {
        ImGui.SameLine();
        if (ImGui.RadioButton(label + "###state" + label, this.stateFilter == value))
        {
            this.stateFilter = value;
            this.rebuildPending = true;
        }
    }

    private void SetStatus(string message, bool isError)
    {
        this.statusMessage = message;
        this.statusIsError = isError;
    }

    private static string FieldLabel(string field) => field switch
    {
        "Name" => "插件名",
        "Punchline" => "一行简介",
        _ => "插件详情",
    };

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    /// <summary>库链地址的短名（GitHub raw 地址显示成 owner/repo）。</summary>
    private static string RepoShort(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.Trim('/');
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && uri.Host.Contains("githubusercontent", StringComparison.OrdinalIgnoreCase))
            {
                var take = Math.Min(2, segments.Length);
                var start = Math.Max(0, segments.Length - take - 1);
                return string.Join("/", segments.Skip(start).Take(take));
            }

            return uri.Host;
        }
        catch
        {
            return UiHelpers.Shorten(url, 24);
        }
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }
}
