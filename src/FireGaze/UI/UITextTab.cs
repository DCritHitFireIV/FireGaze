using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.Diagnostics;
using FireGaze.RepoAudit;
using FireGaze.Translate;
using FireGaze.UIText;

namespace FireGaze.UI;

/// <summary>
///     「插件汉化」页签：一行一个插件（图标 / 标题 / 一行简介 / 状态 / 主操作），
///     点「一键汉化」就把 抽取 → 翻译 → 写入插件 → 自动重载 一次做完；翻译设置在独立窗口里。
/// </summary>
/// <remarks>
///     设计口径（2026-10-01 两路盲评后的 v2）：
///     · 主操作只有一个名字「一键汉化」（所有状态同名，避免「该点哪个」变成阅读题）；
///     · 状态徽标放在 meta 行行首、固定 x，方便竖向扫；
///     · 「还原原文」是次级文字按钮，不抢主操作；
///     · 取消只在翻译阶段可用；写入 / 重载阶段写明「此步不能取消」；
///     · 失败给下一步（重试 / 打开翻译设置），不是一句「失败了」。
/// </remarks>
internal sealed class UITextTab
{
    private enum RowFilter
    {
        All,
        Untranslated,
        Pending,
        Done,
        Failed,
        SkipList,
    }

    private enum NoteKind
    {
        Good,
        Info,
        Bad,
    }

    private sealed class RowInfo
    {
        public bool HasPack;
        public int Total;
        public int Translated;

        /// <summary>没译文、也没标「不翻」的条数（与编辑器筛选「未翻译」同口径）。</summary>
        public int Untranslated;

        public int Skipped;
        public UITextPatchStatus Patch;
        public string PatchDetail = string.Empty;
        public bool HasBackup;
        public bool EditorOpen;

        /// <summary>插件注册了主界面 / 设置界面（汉化完成后「打开」按钮靠它）。</summary>
        public bool HasMainUI;

        public bool HasConfigUI;

        /// <summary>在「不汉化」名单里（中文插件，由朋友维护）：识别为中文插件，不抽取 / 不翻译 / 不打包 / 不上传。</summary>
        public bool DoNotLocalize;

        /// <summary>译文包比已应用的补丁更新（云端下载 / 编辑校对之后）：需要再写入一次才生效。</summary>
        public bool PackNewerThanPatch;
    }

    private sealed class RowNote
    {
        public string Text = string.Empty;

        /// <summary>第二行：补充说明（比如“已翻的不会丢”），空则只画一行。</summary>
        public string? Detail;

        public NoteKind Kind;
        public bool CanOpenSettings;
        public bool CanRetry;
        public DateTime CreatedAt = DateTime.Now;
    }

    private enum RunMode
    {
        /// <summary>一键汉化：抽取 → 翻译 → 写入 → 重载。</summary>
        Full,

        /// <summary>只抽取：列出候选与统计，不翻译、不写补丁。</summary>
        ExtractOnly,
    }

    private sealed class Run
    {
        public string InternalName = string.Empty;
        public RunMode Mode = RunMode.Full;
        public string Stage = string.Empty;
        public bool CanCancel;
        public int Done;
        public int Total;
        public CancellationTokenSource Cancel = new();
        public Task Task = Task.CompletedTask;
        public bool Finished;
    }

    private readonly Plugin plugin;
    private readonly UITextEditorWindow editor;
    private readonly UITextSettingsWindow settings;
    private readonly UITextStore store;
    private readonly UITextPatchManager patches;
    private readonly UITextRunLock runs;

    private InstalledPluginsIndex? index;
    private DateTime indexAt = DateTime.MinValue;

    /// <summary>后台重建索引的任务（插件装/卸/启/停，或超过 30 秒）：好了再换上，不打断绘制。</summary>
    private Task<InstalledPluginsIndex>? indexRebuild;

    /// <summary>卫月的插件状态变化事件置位（可能是任意线程）：下一帧重建索引。</summary>
    private volatile bool installedPluginsDirty;

    private string search = string.Empty;
    private bool onlyThirdParty;

    /// <summary>只看当前已加载运行的插件（在插件管理器里禁用 / 还没加载的不列）。</summary>
    private bool onlyEnabled;
    private string expanded = string.Empty;
    private RowFilter filter = RowFilter.All;

    private Dictionary<string, RowInfo> rows = new(StringComparer.Ordinal);
    private DateTime rowsAt = DateTime.MinValue;

    /// <summary>
    ///     行内提示（成功 / 失败 / 进行中）。**后台任务线程会写它、界面线程会读它**，
    ///     所以用 <see cref="ConcurrentDictionary{TKey,TValue}" />——普通 Dictionary 在这种跨线程
    ///     读写下属于未定义行为（极端时序会抛 InvalidOperationException / 读到错条目，2026-10-02 审计）。
    /// </summary>
    private readonly ConcurrentDictionary<string, RowNote> notes = new(StringComparer.Ordinal);

    /// <summary>每行实测高度（整行点击的 Selectable 用；首帧用估算值）。</summary>
    private readonly Dictionary<string, float> rowHeights = new(StringComparer.Ordinal);

    /// <summary>正在上传译文的插件（防重复点击）。后台任务的收尾会删、界面线程会查，所以用并发字典。
    /// 键存在 = 忙；值不用。</summary>
    private readonly ConcurrentDictionary<string, bool> uploading = new(StringComparer.Ordinal);

    /// <summary>正在启用插件的插件（防重复点击）。同 uploading：后台会删、界面线程会查。</summary>
    private readonly ConcurrentDictionary<string, bool> enabling = new(StringComparer.Ordinal);

    // 云端译文（详情里展示/下载）：索引拉取状态 + 每个包的下载结果提示
    private bool cloudIndexFetching;
    private readonly Dictionary<string, string> cloudNotes = new(StringComparer.Ordinal);
    private readonly HashSet<string> cloudDownloading = new(StringComparer.Ordinal);

    // 反馈弹窗（2026-10-03 用户要求：第一行右侧常驻入口）
    private bool feedbackOpen;
    private bool feedbackNeedsOpen;
    private string feedbackText = string.Empty;
    private int feedbackCategory;
    private bool feedbackAttachLog = true;
    private bool feedbackSending;
    private string feedbackStatus = string.Empty;
    private bool feedbackStatusError;
    private string feedbackSentURL = string.Empty;

    private Run? run;
    private bool rowsDirty = true;

    /// <summary>首次点「一键汉化」时的待办：先选通道，选完才开始（免费通道还要再确认一次）。</summary>
    private sealed class PendingStart
    {
        public InstalledPluginEntry Entry = null!;
        public bool AwaitingFreeConfirm;

        /// <summary>0 = 大模型，1 = 彩云小译，2 = 免费 Google / MyMemory。</summary>
        public int Choice;

        /// <summary>当前选中通道的密钥输入（每个通道各自一份，切单选就清空——两通道共用会把 key 存成 token）。</summary>
        public string SecretInput = string.Empty;

        public string TestStatus = string.Empty;
        public bool TestOk;
        public bool TestRunning;

        /// <summary>试连的代际号：切单选 / 重开后旧请求的结果不许回写。</summary>
        public int TestGeneration;
    }

    private PendingStart? pendingStart;
    private bool pendingNeedsOpen;
    private string firstRunError = string.Empty;

    public UITextTab(
        Plugin plugin,
        UITextEditorWindow editor,
        UITextSettingsWindow settings,
        UITextStore store,
        UITextPatchManager patches,
        UITextRunLock runs)
    {
        this.plugin = plugin;
        this.editor = editor;
        this.settings = settings;
        this.store = store;
        this.patches = patches;
        this.runs = runs;
    }

    /// <summary>
    ///     卫月说「插件列表变了」（装/卸/启/停：<c>IDalamudPluginInterface.ActivePluginsChanged</c>）。
    ///     事件可能在任意线程：只置标记，重建留给绘制线程（下一帧）。——这样在安装器里手动启用后
    ///     行状态立刻跟上，不用等定期的 30 秒兜底。
    /// </summary>
    public void NotifyInstalledPluginsChanged() => this.installedPluginsDirty = true;

    private void KickIndexRebuild()
    {
        if (this.indexRebuild is null)
        {
            this.indexRebuild = Task.Run(InstalledPluginsIndex.Build);
        }
    }

    public void Draw()
    {
        this.PollRun();
        this.DrawToolbar();
        this.DrawRunningBar();
        this.DrawList();
        this.DrawPendingModals();
    }

    // ── 工具条 ───────────────────────────────────────────────────────────

    private void DrawToolbar()
    {
        ImGui.TextWrapped(
            "点「一键汉化」：翻译、写入插件、自动重载一次完成。会改插件文件，原文件自动备份，随时可「还原原文」。");
        ImGui.TextDisabled(
            "这里只管插件自己的界面，插件简介在「简介汉化」页；插件在跑任务时先停一下，重载会打断它。");

        if (ImGui.Button("刷新"))
        {
            this.index = InstalledPluginsIndex.Build();
            this.indexAt = DateTime.Now;
            this.indexRebuild = null;
            this.rowsDirty = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("重新读一遍已装插件列表（刚装 / 刚更新过插件时点它）。");
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(230);
        ImGui.InputTextWithHint("###UITextPluginSearch", "搜索插件名…", ref this.search, 128);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("插件名和内部名都能搜（内部名就是插件目录名）。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("状态");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        var filterLabels = new[] { "全部", "未汉化", "待应用", "已汉化", "失败", "不汉化" };
        var filterIndex = (int)this.filter;
        if (ImGui.Combo("###UITextFilter", ref filterIndex, filterLabels, filterLabels.Length))
        {
            this.filter = (RowFilter)filterIndex;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("按汉化状态筛选：未汉化 / 已翻译还没写入 / 已汉化 / 上次失败。");
        }

        ImGui.SameLine();
        ImGui.Checkbox("只看第三方", ref this.onlyThirdParty);

        ImGui.SameLine();
        ImGui.Checkbox("只看已启用", ref this.onlyEnabled);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("只列当前已加载运行的插件；在插件管理器里禁用、或还没加载起来的不列。\n（已装但停用的插件不用汉化；想给它打补丁时先启用。）");
        }

        ImGui.SameLine();
        if (ImGui.Button("翻译设置…"))
        {
            this.settings.IsOpen = true;
            this.settings.BringToFront();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("翻译方式、API key、灰名单口径、插件更新后是否自动重打——都在这里改。");
        }

        // 插件装/卸/启/停（卫月事件）→ 后台重建；平时 30 秒兜底刷一次（2026-10-02 用户要求：
        // 监听变化立即刷新，把定期的 15 秒级检查放成 30 秒）
        if (this.installedPluginsDirty)
        {
            this.installedPluginsDirty = false;
            this.KickIndexRebuild();
        }

        if (this.index is null)
        {
            // 首次：直接建（一次性，不走后台任务）
            this.index = InstalledPluginsIndex.Build();
            this.indexAt = DateTime.Now;
            this.rowsDirty = true;
        }
        else if (this.indexRebuild is { IsCompleted: true } rebuild)
        {
            this.indexRebuild = null;
            var built = rebuild.Status == TaskStatus.RanToCompletion ? rebuild.Result : null;
            if (built is { Available: true })
            {
                this.index = built;
                this.indexAt = DateTime.Now;
                this.rowsDirty = true;
            }
        }
        else if (this.indexRebuild is null && (DateTime.Now - this.indexAt).TotalSeconds > 30)
        {
            this.KickIndexRebuild();
        }

        if (!this.index.Available)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Bad, "读不到卫月的插件列表（" + (this.index.FailureReason ?? "未知原因") + "）");
            ImGui.NewLine();
            this.DrawFeedbackButton();
            return;
        }

        if ((DateTime.Now - this.rowsAt).TotalSeconds > 5 || this.rowsDirty)
        {
            this.RefreshRows();
        }

        var patched = this.rows.Values.Count(r => r.Patch == UITextPatchStatus.Applied && !r.DoNotLocalize);
        var attention = this.rows.Values.Count(NeedsAttention);
        ImGui.SameLine();
        ImGui.TextDisabled($"刷新于 {this.indexAt:HH:mm:ss} · 已装 {this.index.All.Count} · 已汉化 {patched} · 待处理 {attention}");

        // 反馈入口：常驻在这一行的最右边（2026-10-03 用户要求）
        this.DrawFeedbackButton();
    }

    /// <summary>插件汉化页第一行最右的「反馈…」入口（列表读不到时也会画）。</summary>
    private void DrawFeedbackButton()
    {
        const string feedbackLabel = "反馈…";
        var feedbackLeft = ImGui.GetContentRegionMax().X - UiHelpers.LabelWidth(feedbackLabel);
        if (feedbackLeft > ImGui.GetCursorPosX() + 12f)
        {
            ImGui.SameLine(feedbackLeft);
        }
        else
        {
            ImGui.NewLine();
        }

        if (ImGui.Button(feedbackLabel))
        {
            this.feedbackOpen = true;
            this.feedbackNeedsOpen = true;
            this.feedbackStatus = string.Empty;
            this.feedbackStatusError = false;
            this.feedbackSentURL = string.Empty;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("遇到问题、想提建议——直接在这里反馈。\n可以附带诊断日志（已脱敏），无需 GitHub 账号。");
        }
    }

    /// <summary>
    ///     有任务在跑时，窗口顶部一条常驻状态——滚到列表别处也看得见，还带取消。
    /// </summary>
    private void DrawRunningBar()
    {
        var run = this.run;
        if (run is null)
        {
            return;
        }

        var entry = this.index?.All.FirstOrDefault(e => string.Equals(e.InternalName, run.InternalName, StringComparison.Ordinal));
        var name = entry?.DisplayName ?? run.InternalName;

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.12f, 0.15f, 0.19f, 1f));
        if (ImGui.BeginChild("###UITextRunning", new Vector2(0, ImGui.GetFrameHeight() + 8)))
        {
            var verb = run.Mode == RunMode.ExtractOnly ? "正在抽取" : "正在汉化";
            UiHelpers.ColoredText(run.CanCancel ? UiHelpers.Info : UiHelpers.Warn, $"{verb} {name}：{run.Stage}");

            if (run.CanCancel)
            {
                ImGui.SameLine();
                if (ImGui.Button("取消###UITextRunCancel"))
                {
                    run.Cancel.Cancel();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("取消只停止翻译：已经翻好的条目会保留，之后点「一键汉化」可以接着来。");
                }
            }
            else
            {
                ImGui.SameLine();
                ImGui.TextDisabled("这一步不能取消，等它写完。");
            }
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    // ── 列表 ─────────────────────────────────────────────────────────────

    private void DrawList()
    {
        if (this.index is not { Available: true })
        {
            return;
        }

        var filterText = this.search.Trim();
        var items = this.index.All
            .Where(e => !this.onlyThirdParty || e.IsThirdParty)
            .Where(e => !this.onlyEnabled || e.IsLoaded)
            .Where(e => filterText.Length == 0
                        || e.DisplayName.Contains(filterText, StringComparison.OrdinalIgnoreCase)
                        || e.InternalName.Contains(filterText, StringComparison.OrdinalIgnoreCase))
            .Where(e => this.MatchesFilter(e))
            .ToList();

        if (items.Count == 0)
        {
            ImGui.TextDisabled(this.index.All.Count == 0 ? "没有已装插件。" : "当前筛选下没有插件。");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;

        // 按钮列按「最宽按钮组合」留宽，左列用固定宽（而不是让内容自撑）——
        // 2026-10-03 用户要求：行尾按钮必须在同一行，不要换行。
        // 左列固定宽 = 可用宽 − 按钮列 − 余量（滚动条/单元格边距）；左侧文案用 Fitted 按宽截断，
        // 不会反过来把按钮列挤窄。窗口真的不够时才由 SameLineOrWrap 兜底换行。
        var actionsWidth = ActionsColumnWidth();
        var slack = ImGui.GetStyle().ScrollbarSize + 48f;
        var pluginWidth = Math.Max(180f, ImGui.GetContentRegionAvail().X - actionsWidth - slack);
        if (!ImGui.BeginTable("###UITextPlugins", 2, flags, new Vector2(0, -1)))
        {
            return;
        }

        ImGui.TableSetupColumn("plugin", ImGuiTableColumnFlags.WidthFixed, pluginWidth);
        ImGui.TableSetupColumn("actions", ImGuiTableColumnFlags.WidthFixed, actionsWidth);

        foreach (var plugin in items)
        {
            this.DrawPluginRow(plugin);
        }

        ImGui.EndTable();
    }

    /// <summary>
    ///     按钮列要留多宽：互斥后最多 4 个按钮（主按钮 / 打开或启用 / 还原原文 / 一键上传），
    ///     前两个是固定槽宽（保证各行按钮对齐），后两个标签固定。
    /// </summary>
    private static float ActionsColumnWidth()
    {
        var width = (ImGui.GetStyle().CellPadding.X * 2f) + 8f;
        width += MainActionSlotWidth() + 12f;
        width += OpenOrEnableSlotWidth() + 12f;
        width += UiHelpers.LabelWidth("还原原文") + 12f;
        width += UiHelpers.LabelWidth("一键上传") + 10f;
        return width;
    }

    /// <summary>主按钮槽宽（写入并重载 / 一键汉化 / 重试三个名字里最宽的）。</summary>
    private static float MainActionSlotWidth() => MaxLabelWidth("写入并重载", "一键汉化", "重试");

    /// <summary>第二槽宽：「启用插件」与「打开/设置」互斥，取最宽的一个，后面的按钮才能对齐。</summary>
    private static float OpenOrEnableSlotWidth() => MaxLabelWidth("启用插件", "设置", "打开");

    private static float MaxLabelWidth(params string[] labels) => labels.Max(UiHelpers.LabelWidth);

    /// <summary>启用按钮（绿色）：未加载的插件显示；与「打开/设置」互斥，两名字共用一个固定槽宽。</summary>
    private void DrawEnableButton(InstalledPluginEntry plugin, bool busy, float width)
    {
        ImGui.BeginDisabled(busy || this.enabling.ContainsKey(plugin.InternalName));
        UiHelpers.PushEnableButton();
        var clicked = ImGui.Button("启用插件###UITextEnablePlugin", new Vector2(width, 0));
        UiHelpers.PopEnableButton();
        ImGui.EndDisabled();
        if (clicked)
        {
            this.StartEnable(plugin);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("把这个插件启用起来（与插件安装器里的「启用」同一条路）；加载后就能看到汉化效果。");
        }
    }

    private bool MatchesFilter(InstalledPluginEntry entry)
    {
        var info = this.rows.GetValueOrDefault(entry.InternalName);
        return this.filter switch
        {
            // 「不汉化」是终态，不算「未汉化」（避免两个词又被混在一起）
            RowFilter.Untranslated => info?.DoNotLocalize != true && (info is null || !info.HasPack || info.Translated == 0),
            RowFilter.Pending => info is { HasPack: true, Translated: > 0 } && info.Patch != UITextPatchStatus.Applied,
            RowFilter.Done => info is { Patch: UITextPatchStatus.Applied },
            RowFilter.Failed => info is { Patch: UITextPatchStatus.Failed } || this.notes.TryGetValue(entry.InternalName, out var n) && n.Kind == NoteKind.Bad,
            RowFilter.SkipList => info is { DoNotLocalize: true },
            _ => true,
        };
    }

    /// <summary>需要用户动手的行：失败 / 翻了没写入 / 有改动待写入（不含「不汉化」名单）。</summary>
    private static bool NeedsAttention(RowInfo r)
    {
        if (r.DoNotLocalize)
        {
            return false;
        }

        return r.Patch == UITextPatchStatus.Failed
               || (r.HasPack && r.Translated > 0 && r.Patch != UITextPatchStatus.Applied)
               || r.PackNewerThanPatch;
    }

    private void DrawPluginRow(InstalledPluginEntry plugin)
    {
        this.rows.TryGetValue(plugin.InternalName, out var info);
        var running = this.run is { } r && string.Equals(r.InternalName, plugin.InternalName, StringComparison.Ordinal);
        var busy = this.run is not null;
        var isOpen = string.Equals(this.expanded, plugin.InternalName, StringComparison.Ordinal);
        var editorOpen = this.editor.IsOpen && string.Equals(this.editor.CurrentInternalName, plugin.InternalName, StringComparison.Ordinal);

        ImGui.PushID(plugin.InternalName);
        ImGui.TableNextRow();

        // 整行点击展开（2026-10-03 用户要求）：Selectable 铺在行底层、跨所有列、允许被覆盖——
        // 点行的任何地方（图标 / 文字 / 空白）都展开收起；按钮在自己区域优先接收点击。
        var rowStartY = ImGui.GetCursorPosY();
        var rowHeight = this.rowHeights.TryGetValue(plugin.InternalName, out var knownHeight) ? knownHeight : 46f;
        ImGui.TableSetColumnIndex(0);
        if (ImGui.Selectable("##row", isOpen,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap,
                new Vector2(0, rowHeight)))
        {
            this.expanded = isOpen ? string.Empty : plugin.InternalName;
        }

        ImGui.SameLine(0, 0);

        // ── 左列：图标 + 文本块 ──
        var rowTop = ImGui.GetCursorScreenPos().Y;

        const float iconSize = 40f;
        if (!this.TryDrawIcon(plugin, iconSize))
        {
            DrawLetterIcon(plugin.DisplayName, iconSize);
        }

        ImGui.SameLine();
        ImGui.BeginGroup();
        {
            ImGui.TextUnformatted(plugin.DisplayName);
            if (plugin.IsDev)
            {
                // 开发/本地插件：卫月的 manifest.IsThirdParty 默认 false，不区分的话会显示成「[官库]」（用户实测反馈）
                ImGui.SameLine();
                ImGui.TextDisabled("[本地]");
            }
            else if (!plugin.IsThirdParty)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("[官库]");
            }

            var punchline = plugin.Punchline;
            if (string.IsNullOrWhiteSpace(punchline))
            {
                punchline = plugin.Description;
            }

            if (!string.IsNullOrWhiteSpace(punchline))
            {
                // 按左列实际宽度截断（列已固定宽；固定 96 字会在窄列里溢出、把按钮列挤窄）
                UiHelpers.Fitted(punchline.Replace('\n', ' '), punchline);
            }

            // meta 行：状态徽标在行首（固定 x，方便竖向扫） + 内部名 · 版本
            if (info is { PackNewerThanPatch: true, Patch: UITextPatchStatus.Applied })
            {
                // 「已汉化」本身是绿色终态，只有「有改动待写入」是橙色警告
                // （v4 复评 N16：整条变色会让绿=完成失效）
                UiHelpers.ColoredText(UiHelpers.Good, $"已汉化 {info.Translated} 条");
                ImGui.SameLine(0, 4);
                UiHelpers.ColoredText(UiHelpers.Warn, "· 有改动待写入");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("译文包比已写入的补丁新（下载 / 编辑过），点这一行的主按钮写入插件生效。");
                }
            }
            else
            {
                var (badge, color) = this.DescribeState(info, running);
                UiHelpers.ColoredText(color, badge);
            }
            if (info is { DoNotLocalize: true } && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("中文插件 · 不汉化：这个插件由朋友维护、本身就是中文界面，FireGaze 不抽取 / 不翻译 / 不打包 / 不上传。\n有旧补丁记录时可以点「还原原文」恢复原版。");
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"{plugin.InternalName}{(string.IsNullOrEmpty(plugin.Version) ? string.Empty : " · v" + plugin.Version)}");

            if (running && this.run is { Total: > 0 } active)
            {
                var fraction = Math.Clamp(active.Done / (float)active.Total, 0f, 1f);
                ImGui.ProgressBar(fraction, new Vector2(-1, 4), string.Empty);
            }

            if (this.notes.TryGetValue(plugin.InternalName, out var note))
            {
                this.DrawNote(plugin, note);
            }
        }

        ImGui.EndGroup();
        // 量一下这一行实际多高，下一帧 Selectable 用它（首帧先用估算值）。
        this.rowHeights[plugin.InternalName] = Math.Max(20f, ImGui.GetItemRectMax().Y - rowTop);

        // ── 右列：操作 ──
        ImGui.TableNextColumn();
        ImGui.SetCursorPosY(rowStartY + 2);
        this.DrawRowActions(plugin, info, running, busy, editorOpen, isOpen);

        // ── 展开区（另起一行，左列整幅） ──
        if (isOpen)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            this.DrawExpanded(plugin, info, busy, editorOpen);
            ImGui.TableNextColumn();
        }

        ImGui.PopID();
    }

    private void DrawRowActions(InstalledPluginEntry plugin, RowInfo? info, bool running, bool busy, bool editorOpen, bool isOpen)
    {
        if (info is { DoNotLocalize: true })
        {
            // 不汉化 ≠ 不能启用：未加载的也给它一个启用入口（与已加载插件互斥的同一槽位）
            if (!plugin.IsLoaded)
            {
                this.DrawEnableButton(plugin, busy, OpenOrEnableSlotWidth());
            }

            if (info.HasBackup)
            {
                if (!plugin.IsLoaded)
                {
                    UiHelpers.SameLineOrWrap(UiHelpers.LabelWidth("还原原文"), 12);
                }

                if (ImGui.Button("还原原文"))
                {
                    this.RestoreNow(plugin);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("把插件 DLL 还原成打补丁之前的原始文件，然后自动重载插件（界面回到英文）。\n之后还可以再「一键汉化」。");
                }
            }

            return;
        }

        if (running)
        {
            ImGui.BeginDisabled();
            ImGui.Button("一键汉化", new Vector2(MainActionSlotWidth(), 0));
            ImGui.EndDisabled();
            if (this.run is { CanCancel: true } active)
            {
                UiHelpers.SameLineOrWrap(UiHelpers.LabelWidth("取消"), 8);
                if (ImGui.Button("取消###UITextRowCancel"))
                {
                    active.Cancel.Cancel();
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("取消只停止翻译：已经翻好的条目会保留，之后点「一键汉化」可以接着来。");
                }
            }
            else if (this.run is not null)
            {
                UiHelpers.SameLineOrWrap(UiHelpers.LabelWidth("写入中…"), 8);
                ImGui.TextDisabled("写入中…");
            }
        }
        else
        {
            // 两个按钮都常驻（2026-10-03 用户要求）：未汉化时「一键汉化」主色；汉化完成后
            // 「打开/设置」变主色、「一键汉化」退回普通白底。按钮名跟着「这一步真正会做什么」走
            // （v4 复评 N1/F3/N2/F4）：失败后接着翻叫「重试」；只差写进插件叫「写入并重载」。
            var fullyLocalized = info is { HasPack: true }
                                 && (info.Patch == UITextPatchStatus.Applied || info.Total == 0)
                                 && !info.PackNewerThanPatch;

            var retryable = this.notes.TryGetValue(plugin.InternalName, out var pendingNote) && pendingNote.CanRetry;
            var writeOnly = info is { HasPack: true, PackNewerThanPatch: true, Patch: UITextPatchStatus.Applied };
            var label = retryable ? "重试" : writeOnly ? "写入并重载" : "一键汉化";
            ImGui.BeginDisabled(busy || editorOpen);
            if (!fullyLocalized)
            {
                UiHelpers.PushPrimaryButton();
            }

            if (ImGui.Button(label, new Vector2(MainActionSlotWidth(), 0)))
            {
                this.StartOneClick(plugin);
            }

            if (!fullyLocalized)
            {
                UiHelpers.PopPrimaryButton();
            }

            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(
                    (busy ? "有另一个插件正在汉化，等它跑完再点。\n" : string.Empty) +
                    (editorOpen ? "这个插件正开着编辑窗口，先关掉它（避免两份修改互相覆盖）。\n" : string.Empty) +
                    (retryable
                        ? "沿用当前通道，从没翻完的地方接着翻。\n已翻好的条目不会丢；原文件备份与「还原原文」照旧。"
                        : writeOnly
                            ? "把改动过的译文写进插件 DLL 并自动重载（不再重新翻译）。\n原文件会先备份，随时可以「还原原文」。"
                            : "翻译没翻的条目 → 写入插件 DLL → 自动重载插件。\n原文件会先备份，随时可以「还原原文」。"));
            }

            // 第二槽位：**未加载 → 启用插件；已加载 → 打开/设置**，两者互斥（用户要求：不允许两个同时亮），
            // 且用同一个固定宽度——不管哪个按钮，后面的「还原原文 / 一键上传」都能对齐。
            UiHelpers.SameLineOrWrap(OpenOrEnableSlotWidth(), 12);
            if (!plugin.IsLoaded)
            {
                this.DrawEnableButton(plugin, busy, OpenOrEnableSlotWidth());
            }
            else
            {
                this.DrawOpenPluginButton(plugin, info, primary: fullyLocalized, width: OpenOrEnableSlotWidth());
            }

            if (info is { HasBackup: true })
            {
                UiHelpers.SameLineOrWrap(UiHelpers.LabelWidth("还原原文"), 12);
                if (ImGui.Button("还原原文"))
                {
                    this.RestoreNow(plugin);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("把插件 DLL 还原成打补丁之前的原始文件，然后自动重载插件（界面回到英文）。\n之后还可以再「一键汉化」。");
                }
            }
        }

        // 「详情」按钮已去掉（2026-10-03）：点整行任意处展开收起，见 DrawRow 里的 Selectable。

        // 一键上传：把这台机器上已经翻好的译文交给社区公共库（2026-10-02 用户要求）
        if (info is { Translated: > 0 })
        {
            UiHelpers.SameLineOrWrap(UiHelpers.LabelWidth("一键上传"), 10);
            ImGui.BeginDisabled(this.uploading.ContainsKey(plugin.InternalName));
            if (ImGui.Button("一键上传"))
            {
                this.StartUpload(plugin);
            }

            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(
                    "把已经翻好的译文上传到社区公共库，让其他玩家直接用你的译文。\n" +
                    "（只含原文、译文与代码位置，不带账号信息；维护者收录后所有人「一键汉化」时能直接下载。）");
            }
        }
    }

    /// <summary>
    ///     启用未加载的插件（与插件安装器的「启用」同一条路）：写 Profile 的想要状态 + 加载。
    ///     成功后：待确认的补丁立即转正（刚加载的就是打过补丁的文件）、置脏重建索引——
    ///     主按钮应马上从「一键汉化」变成「打开」。
    /// </summary>
    private void StartEnable(InstalledPluginEntry entry)
    {
        if (!this.enabling.TryAdd(entry.InternalName, true))
        {
            return;
        }

        this.notes[entry.InternalName] = new RowNote { Kind = NoteKind.Info, Text = "正在启用插件…" };
        _ = Task.Run(async () =>
        {
            var (ok, message) = await PluginEnableBridge.EnableAsync(entry.RawPlugin).ConfigureAwait(false);
            this.enabling.TryRemove(entry.InternalName, out _);
            if (ok)
            {
                // 插件已加载：它读的就是打过补丁的文件（没打过就无所谓），待确认状态直接转正
                this.patches.MarkVerified(entry.InternalName);
                if (this.patches.HasBackup(entry))
                {
                    message += " 补丁已生效。";
                }
            }

            this.notes[entry.InternalName] = new RowNote { Kind = ok ? NoteKind.Good : NoteKind.Bad, Text = message };
            this.installedPluginsDirty = true;
            this.rowsDirty = true;
        });
    }

    /// <summary>
    ///     一键上传：把该插件包里有译文的条目打成投稿，走中继（失败回退 GitHub 提交页）。
    /// </summary>
    private void StartUpload(InstalledPluginEntry entry)
    {
        if (this.uploading.ContainsKey(entry.InternalName))
        {
            return;
        }

        if (UITextRules.IsDoNotLocalize(entry.InternalName))
        {
            this.notes[entry.InternalName] = new RowNote { Kind = NoteKind.Info, Text = "识别为中文插件（由朋友维护），不上传它的译文。" };
            return;
        }

        var pack = this.store.Load(entry.InternalName);
        var entries = pack.Entries.Where(e => e.HasTranslation && !pack.IsSkipped(e.Original)).ToList();
        var resources = pack.Resources.Where(e => e.HasTranslation).ToList();
        var attributes = pack.Attributes.Where(e => e.HasTranslation).ToList();
        var total = entries.Count + resources.Count + attributes.Count;
        if (total == 0)
        {
            this.notes[entry.InternalName] = new RowNote { Kind = NoteKind.Info, Text = "这个插件还没有可上传的译文。" };
            return;
        }

        var payload = System.Text.Json.JsonSerializer.Serialize(
            new { type = "uit-contribution", plugin = entry.InternalName, entries, resources, attributes },
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        // 标题里带一个英文 "contributions"：兼容线上旧版 Worker 的关键词校验（2026-10-02 修 HTTP 400 的根因）
        var header = $"### FireGaze contributions · 插件界面文字译文贡献\n\n- 插件：`{entry.InternalName}`\n- 条数：{total}\n\n";
        var body = header + "```json\n" + payload + "\n```\n";
        var title = $"[译文贡献] {entry.InternalName} · {total} 条";

        // 出站内容要有知情与确认（盲评 CF-04/L1）：先把「发什么、去哪、公开性」摊开，再由用户点确认。
        this.pendingUpload = new PendingUpload
        {
            Entry = entry,
            Total = total,
            Human = entries.Count(e => e.IsUserSource) + resources.Count(e => e.IsUserSource) + attributes.Count(e => e.IsUserSource),
            Title = title,
            Body = body,
        };
        this.uploadModalNeedsOpen = true;
    }

    /// <summary>待确认的上传（确认框用）：条数、人工/机器占比、标题与正文。</summary>
    private sealed class PendingUpload
    {
        public InstalledPluginEntry Entry = null!;
        public int Total;
        public int Human;
        public string Title = string.Empty;
        public string Body = string.Empty;
    }

    private PendingUpload? pendingUpload;
    private bool uploadModalNeedsOpen;

    /// <summary>确认后真正执行上传（中继优先、失败回退 GitHub 提交页——与编辑器同一条通道）。</summary>
    private void RunUpload(PendingUpload upload)
    {
        var entry = upload.Entry;
        var total = upload.Total;
        var title = upload.Title;
        var body = upload.Body;
        var storeDirectory = this.store.DirectoryPath;
        this.uploading.TryAdd(entry.InternalName, true);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await ContributeSender.SubmitAsync(entry.InternalName, total, title, body, storeDirectory).ConfigureAwait(false);
                var kind = result.Severity switch
                {
                    ContributeSendSeverity.Good => NoteKind.Good,
                    ContributeSendSeverity.Bad => NoteKind.Bad,
                    _ => NoteKind.Info,
                };
                if (kind == NoteKind.Bad)
                {
                    ActivityLog.Error("上传译文", $"{entry.InternalName}：{result.Message}");
                }
                else
                {
                    ActivityLog.Info("上传译文", $"{entry.InternalName}：{result.Message}");
                }

                await this.FinishUploadAsync(entry.InternalName, result.Message, kind).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                ActivityLog.Error("上传译文", $"{entry.InternalName}：上传回调失败", e);
                Plugin.Log?.Warning(e, "[内部文本] 上传结果回调调度失败");
            }
        });
    }

    /// <summary>上传确认框：说清发什么、去哪、公开性，默认焦点在「取消」。</summary>
    private void DrawUploadConfirmModal(PendingUpload upload)
    {
        const string Name = "上传译文到公共库###UITextUploadConfirm";
        if (this.uploadModalNeedsOpen)
        {
            ImGui.OpenPopup(Name);
            this.uploadModalNeedsOpen = false;
        }

        ImGui.SetNextWindowSizeConstraints(new Vector2(520, 0), new Vector2(660, float.MaxValue));
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal(Name, ImGuiWindowFlags.AlwaysAutoResize))
        {
            this.pendingUpload = null;
            return;
        }

        ImGui.TextWrapped($"把「{upload.Entry.DisplayName}」已经翻好的 {upload.Total} 条译文上传到社区公共库，之后其他玩家「一键汉化」时能直接用到。");
        ImGui.TextDisabled($"人工 {upload.Human} 条 · 机器 {upload.Total - upload.Human} 条；只上传原文、译文与代码位置，不带账号信息与 key。");
        ImGui.Spacing();
        ImGui.TextWrapped("不想分享的条目，可以先到「编辑校对」里改写或标「不翻」。");
        ImGui.Separator();
        if (ImGui.Button("上传", new Vector2(110, 0)))
        {
            var confirmed = upload;
            this.pendingUpload = null;
            UiHelpers.ClosePopupAndEnd();
            this.RunUpload(confirmed);
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(90, 0)))
        {
            this.pendingUpload = null;
            UiHelpers.ClosePopupAndEnd();
            return;
        }

        // 出站内容默认焦点在「取消」
        ImGui.SetItemDefaultFocus();
        ImGui.EndPopup();
    }

    /// <summary>上传任务的收尾：回主线程把行提示写上（上传是后台任务）。</summary>
    private async Task FinishUploadAsync(string internalName, string text, NoteKind kind)
    {
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            this.uploading.TryRemove(internalName, out _);
            this.notes[internalName] = new RowNote { Kind = kind, Text = text };
            this.rowsDirty = true;
        }).ConfigureAwait(false);
    }

    /// <summary>
    ///     汉化完成的插件行：按钮变「打开」——有主界面开主界面，没有主界面就开设置界面，
    ///     两者都没有就置灰（点了也不会有反应）。能不能开、开哪个都从 <see cref="PluginUiBridge" /> 现读，
    ///     和官方插件安装器的按钮同一套机制（反射 <c>LocalPlugin.DalamudInterface.LocalUiBuilder</c>；
    ///     不能强转 <c>IExposedPlugin</c>——那不是同一个对象）。
    /// </summary>
    private void DrawOpenPluginButton(InstalledPluginEntry plugin, RowInfo? info, bool primary = false, float width = 0f)
    {
        var hasMain = info?.HasMainUI ?? false;
        var hasConfig = info?.HasConfigUI ?? false;
        var canOpen = hasMain || hasConfig;

        // 有主界面就叫「打开」，只能开设置就叫「设置」；都没有就叫「打开」但置灰（点了不会有反应）
        var label = hasMain || !hasConfig ? "打开" : "设置";
        ImGui.BeginDisabled(!canOpen);
        if (primary && canOpen)
        {
            UiHelpers.PushPrimaryButton();
        }

        if (ImGui.Button(label, width > 0f ? new Vector2(width, 0) : default))
        {
            try
            {
                var opened = PluginUiBridge.Open(plugin.RawPlugin);
                if (!opened)
                {
                    this.notes[plugin.InternalName] = new RowNote { Kind = NoteKind.Info, Text = "这个插件没有可打开的界面。" };
                }
            }
            catch (Exception e)
            {
                this.notes[plugin.InternalName] = new RowNote { Kind = NoteKind.Bad, Text = "打开失败：" + e.Message };
            }
        }

        if (primary && canOpen)
        {
            UiHelpers.PopPrimaryButton();
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(!plugin.IsLoaded
                ? "插件当前没有加载，先启用它。"
                : hasMain
                    ? "打开插件的主界面（等同插件自己的打开命令）。"
                    : hasConfig
                        ? "这个插件没有主界面，打开它的设置界面。"
                        : "这个插件既没有主界面也没有设置界面，打不开。");
        }
    }

    private void DrawExpanded(InstalledPluginEntry plugin, RowInfo? info, bool busy, bool editorOpen)
    {
        ImGui.Indent(48f);

        if (info is { DoNotLocalize: true })
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - 8);
            ImGui.TextDisabled("识别为中文插件，不汉化。这个项目本身就是中文界面、由朋友维护，FireGaze 跳过它的抽取 / 翻译 / 打包 / 上传。");
            ImGui.PopTextWrapPos();
            if (info.HasBackup)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("检测到以前打过汉化补丁：点这个插件的「还原原文」即可恢复原版。");
            }

            ImGui.Unindent(48f);
            return;
        }

        var description = plugin.Description;
        if (string.IsNullOrWhiteSpace(description))
        {
            ImGui.TextDisabled("作者没有提供详细介绍。");
        }
        else
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - 8);
            ImGui.TextUnformatted(description);
            ImGui.PopTextWrapPos();
        }

        if (info is { HasPack: true })
        {
            var patchText = info.Patch switch
            {
                UITextPatchStatus.Applied => "已应用（原始文件已备份）",
                UITextPatchStatus.PendingReload => "已应用，等待重载确认",
                UITextPatchStatus.NeedsRepatch => "插件更新过，需要重打",
                UITextPatchStatus.Failed => "上次失败，已还原",
                _ => "未应用" + (info.HasBackup ? "（原始文件已备份）" : string.Empty),
            };
            ImGui.TextDisabled(
                $"共 {info.Total} 条 · 已翻译 {info.Translated} 条 · 未翻译 {info.Untranslated} 条 · 不翻 {info.Skipped} 条 ｜ 补丁：{patchText}");

            if (info.PatchDetail.Length > 0 && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(info.PatchDetail);
            }
        }
        else
        {
            ImGui.TextDisabled("还没有抽取过文本。点「一键汉化」会先抽取再翻译。");
        }

        ImGui.Spacing();
        var extracting = this.run is { Mode: RunMode.ExtractOnly } only
                         && string.Equals(only.InternalName, plugin.InternalName, StringComparison.Ordinal);
        ImGui.BeginDisabled(busy || editorOpen || extracting);
        if (ImGui.Button(info is { HasPack: true } ? "重新抽取###UITextExtract" : "抽取界面文本###UITextExtract"))
        {
            this.StartExtract(plugin);
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(extracting
                ? "正在抽取…"
                : busy || editorOpen
                    ? "先把当前任务跑完再抽取。"
                    : "只读一遍插件 DLL，列出能翻的界面文本（不翻译、不写补丁）。\n"
                      + "插件更新过、或想先看看有多少文本，就点它；结果会记进本地包，编辑器里也能接着改。\n"
                      + "不会丢已翻好的译文；原文没了的条目会自动标成「不翻」，可在编辑器里恢复。");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("编辑校对…###UITextOpenEditor"))
        {
            this.editor.OpenFor(plugin);
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(busy
                ? "先把当前任务跑完再进编辑器。"
                : "逐条改译文、标记「不翻」、重新抽取。适合想自己核对的插件。");
        }

        ImGui.SameLine();
        ImGui.TextDisabled("译文来源：" + UITextChannelFactory.Describe(this.plugin.Config));

        this.DrawCloudPacks(plugin);

        ImGui.Unindent(48f);
    }

    /// <summary>
    ///     详情里的「云端译文」：列出公共库该插件的可下载译文包（多来源时可自行选择），
    ///     点「下载并应用」把那一包并进本机（本机人工改过的永不被顶）——2026-10-02 用户要求：
    ///     给用户选择权，不让单一来源（含被污染的投稿）决定所有人看到什么。
    /// </summary>
    private void DrawCloudPacks(InstalledPluginEntry plugin)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("云端译文（公共库）");

        if (!this.plugin.Config.UITextLibraryEnabled)
        {
            ImGui.TextDisabled("译文库已关闭（翻译设置里可以打开）。");
            return;
        }

        var library = this.plugin.TextLibrary;
        var index = library.CachedIndex;
        if (index is null)
        {
            if (!this.cloudIndexFetching)
            {
                this.cloudIndexFetching = true;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await library.FetchIndexAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // 拉不到就显示提示；不影响其它功能
                    }
                    finally
                    {
                        try
                        {
                            await Plugin.Framework.RunOnFrameworkThread(() => this.cloudIndexFetching = false).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // 忽略
                        }
                    }
                });
            }

            ImGui.TextDisabled(this.cloudIndexFetching ? "正在查云端译文库…" : "云端译文库暂时拉不到（关掉详情再开一次可以重试）。");
            return;
        }

        if (!index.Plugins.TryGetValue(plugin.InternalName, out var entry))
        {
            ImGui.TextDisabled("云端还没有这个插件的译文包。");
            return;
        }

        foreach (var pack in entry.EffectivePacks())
        {
            var label = string.IsNullOrWhiteSpace(pack.Label) ? pack.ID ?? "译文包" : pack.Label!;
            var source = pack.Source switch
            {
                "user" => "玩家投稿",
                null or "" or "library" => "公共库",
                var other => other,
            };
            var count = pack.Entries + pack.Resources + pack.Attributes;
            var downloads = pack.Downloads > 0 ? $"下载 {pack.Downloads}" : "下载 —";
            var key = plugin.InternalName + "|" + (pack.File ?? pack.ID ?? "library");

            ImGui.TextUnformatted($"{label}（{source}）");
            ImGui.SameLine();
            ImGui.TextDisabled($"{count} 条 · {pack.UpdatedAt ?? "—"} · {downloads}");

            ImGui.SameLine();
            ImGui.BeginDisabled(this.cloudDownloading.Contains(key));
            if (ImGui.Button($"下载译文###cloud-{pack.ID ?? pack.File ?? "library"}"))
            {
                this.StartCloudDownload(plugin, pack);
            }

            ImGui.EndDisabled();
            if (this.cloudDownloading.Contains(key))
            {
                ImGui.SameLine();
                ImGui.TextDisabled("下载中…");
            }
            else if (this.cloudNotes.TryGetValue(key, out var note))
            {
                ImGui.SameLine();
                ImGui.TextDisabled(note);
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled("（本机改过的译文不会被覆盖；下载后点这一行的主按钮写入插件）");
    }

    /// <summary>把云端指定的译文包并进本机包（不自动打补丁——由「一键汉化」统一写盘 + 重载）。</summary>
    private void StartCloudDownload(InstalledPluginEntry plugin, UITextLibraryPack pack)
    {
        var fileName = string.IsNullOrWhiteSpace(pack.File) ? plugin.InternalName + ".json" : pack.File!;
        var key = plugin.InternalName + "|" + fileName;
        if (!this.cloudDownloading.Add(key))
        {
            return;
        }

        var packStore = this.store;
        var library = this.plugin.TextLibrary;
        var internalName = plugin.InternalName;
        var label = string.IsNullOrWhiteSpace(pack.Label) ? pack.ID ?? "译文包" : pack.Label!;
        var source = pack.Source switch
        {
            "user" => "玩家投稿",
            null or "" or "library" => "公共库",
            var other => other,
        };
        _ = Task.Run(async () =>
        {
            UITextPack? fetched = null;
            UITextFlow.MergePreview preview = default;
            string? error = null;
            try
            {
                fetched = await library.FetchPackFileAsync(fileName, CancellationToken.None).ConfigureAwait(false);
                if (fetched is not null)
                {
                    preview = UITextFlow.PreviewMerge(packStore.Load(internalName), fetched);
                }
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            try
            {
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    this.cloudDownloading.Remove(key);
                    if (fetched is null)
                    {
                        this.cloudNotes[key] = error is null ? "云端包拉不到" : "下载失败：" + error;
                        this.rowsDirty = true;
                        return;
                    }

                    this.pendingCloudApply = new PendingCloudApply
                    {
                        Entry = plugin,
                        Key = key,
                        Label = label,
                        Source = source,
                        Fetched = fetched,
                        Preview = preview,
                    };
                    this.cloudModalNeedsOpen = true;
                    this.rowsDirty = true;
                }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 忽略
            }
        });
    }

    /// <summary>待确认的云端译文应用（先看差异再点「应用」）。</summary>
    private sealed class PendingCloudApply
    {
        public InstalledPluginEntry Entry = null!;
        public string Key = string.Empty;
        public string Label = string.Empty;
        public string Source = string.Empty;
        public UITextPack Fetched = new();
        public UITextFlow.MergePreview Preview;
    }

    private PendingCloudApply? pendingCloudApply;
    private bool cloudModalNeedsOpen;

    /// <summary>云端译文的差异确认框：新增 / 覆盖机器译 / 保留玩家译 / 无变化，默认焦点在「取消」。</summary>
    private void DrawCloudApplyModal(PendingCloudApply pending)
    {
        const string Name = "应用云端译文###UITextCloudApply";
        if (this.cloudModalNeedsOpen)
        {
            ImGui.OpenPopup(Name);
            this.cloudModalNeedsOpen = false;
        }

        ImGui.SetNextWindowSizeConstraints(new Vector2(520, 0), new Vector2(660, float.MaxValue));
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal(Name, ImGuiWindowFlags.AlwaysAutoResize))
        {
            this.pendingCloudApply = null;
            return;
        }

        var preview = pending.Preview;
        ImGui.TextWrapped($"应用云端译文：{pending.Label}（{pending.Source}）");
        ImGui.TextDisabled($"新增 {preview.Added} 条 · 更新机器译文 {preview.Overwritten} 条 · 你的 {preview.Protected} 条人工译文保留不动 · 其余 {preview.Same} 条无变化");
        ImGui.Spacing();
        ImGui.TextWrapped("本机人工改过的译文不会被覆盖；应用后还要点这一行的「一键汉化」才会写进插件。");
        ImGui.Separator();
        if (ImGui.Button("应用", new Vector2(100, 0)))
        {
            var confirmed = pending;
            this.pendingCloudApply = null;
            UiHelpers.ClosePopupAndEnd();
            this.ApplyCloudPack(confirmed);
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(90, 0)))
        {
            this.cloudNotes[pending.Key] = "已取消";
            this.pendingCloudApply = null;
            UiHelpers.ClosePopupAndEnd();
            return;
        }

        ImGui.SetItemDefaultFocus();
        ImGui.EndPopup();
    }

    /// <summary>确认后真正把云端包并进本机（走 MergeLibrary 的优先级：玩家译永不被顶）。</summary>
    private void ApplyCloudPack(PendingCloudApply pending)
    {
        var key = pending.Key;
        var internalName = pending.Entry.InternalName;
        var fetched = pending.Fetched;
        var packStore = this.store;
        _ = Task.Run(async () =>
        {
            string text;
            try
            {
                var local = packStore.Load(internalName);
                var changed = local.MergeLibrary(fetched);
                text = changed > 0
                    ? packStore.Save(internalName, local, out var saveError)
                        ? $"已应用 {changed} 条，点「一键汉化」重打补丁生效"
                        : "写盘失败：" + saveError
                    : "没有变化（本机已有相同或更好的译文）";
            }
            catch (Exception e)
            {
                text = "应用失败：" + e.Message;
            }

            try
            {
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    this.cloudNotes[key] = text;
                    this.rowsDirty = true;
                }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 忽略
            }
        });
    }

    private void DrawNote(InstalledPluginEntry plugin, RowNote note)
    {
        // 成功是一次性事件：十几秒后收起；失败 / 未加载是持久状态，留到下一次操作。
        if (note.Kind == NoteKind.Good && (DateTime.Now - note.CreatedAt).TotalSeconds > 12)
        {
            this.notes.TryRemove(plugin.InternalName, out _);
            return;
        }

        var color = note.Kind switch
        {
            NoteKind.Good => UiHelpers.Good,
            NoteKind.Bad => UiHelpers.Bad,
            _ => UiHelpers.Muted,
        };

        if (note.Detail is { Length: > 0 } detail)
        {
            UiHelpers.ColoredText(color, note.Text);
            ImGui.TextDisabled(detail);
        }
        else
        {
            UiHelpers.ColoredWrapped(color, note.Text);
        }

        if (note.CanOpenSettings)
        {
            ImGui.SameLine();
            if (ImGui.Button("翻译设置…###UITextOpenSettings"))
            {
                this.settings.IsOpen = true;
                this.settings.BringToFront();
            }
        }
    }

    /// <summary>
    ///     状态徽标：进行中 / 已汉化 / 已翻译待应用 / 未汉化 / 失败。
    /// </summary>
    private (string Text, Vector4 Color) DescribeState(RowInfo? info, bool running)
    {
        if (running && this.run is { } active)
        {
            return (active.Total > 0 ? $"翻译中 {active.Done} / {active.Total} 条" : active.Stage, UiHelpers.Info);
        }

        if (info is { DoNotLocalize: true })
        {
            return ("中文插件 · 不汉化", UiHelpers.Skip);
        }

        if (info is null || !info.HasPack)
        {
            return ("未汉化", UiHelpers.Muted);
        }

        switch (info.Patch)
        {
            case UITextPatchStatus.Applied:
                return info.PackNewerThanPatch
                    ? ($"已汉化 {info.Translated} 条 · 有改动待写入", UiHelpers.Warn)
                    : ($"已汉化 {info.Translated} 条", UiHelpers.Good);
            case UITextPatchStatus.PendingReload:
                return ($"已汉化 {info.Translated} 条 · 待重载", UiHelpers.Info);
            case UITextPatchStatus.NeedsRepatch:
                return ($"已汉化 {info.Translated} 条 · 需要重打", UiHelpers.Warn);
            case UITextPatchStatus.Failed:
                return ($"上次失败 · 已翻译 {info.Translated} 条", UiHelpers.Bad);
        }

        if (info.Translated > 0)
        {
            return ($"已翻译 {info.Translated} 条 · 未应用", UiHelpers.Info);
        }

        return ($"未汉化 · 候选 {Math.Max(0, info.Total - info.Skipped)} 条", UiHelpers.Muted);
    }

    // ── 一键汉化 ─────────────────────────────────────────────────────────

    /// <summary>
    ///     只抽取（不翻译、不写补丁）：把插件 DLL 里能翻的文本列出来并记进本地包。
    /// </summary>
    private void StartExtract(InstalledPluginEntry entry)
    {
        if (this.run is not null)
        {
            return;
        }

        if (!this.runs.TryEnter(entry.InternalName, "正在抽取 " + entry.DisplayName, out var reason))
        {
            this.notes[entry.InternalName] = new RowNote
            {
                Kind = NoteKind.Info,
                Text = "另一个任务正在跑：" + reason + " 等它结束再点。",
            };
            return;
        }

        var run = new Run { InternalName = entry.InternalName, Mode = RunMode.ExtractOnly, Stage = "正在读取插件界面文本…" };
        this.run = run;
        this.notes.TryRemove(entry.InternalName, out _);
        run.Task = Task.Run(() => this.RunPipelineAsync(entry, run));
    }

    private void StartOneClick(InstalledPluginEntry entry)
    {
        if (this.run is not null)
        {
            return;
        }

        var config = this.plugin.Config;

        // 第一次点：先让用户选一次翻译方式（不替他用最慢的免费接口）
        if (!config.UITextChannelChosen)
        {
            this.OpenFirstRun(entry);
            return;
        }

        // 选了免费：先确认过一次「知道它慢」
        if (IsFreeChannel(config.UITextChannel) && !config.UITextFreeWarned)
        {
            this.pendingStart = new PendingStart { Entry = entry, AwaitingFreeConfirm = true, Choice = 2 };
            this.pendingNeedsOpen = true;
            return;
        }

        this.StartOneClickCore(entry);
    }

    private static bool IsFreeChannel(string channel) => channel is "auto" or "google" or "mymemory";

    private void OpenFirstRun(InstalledPluginEntry entry)
    {
        var config = this.plugin.Config;
        var hasLlmKey = DPAPI.UnprotectFromBase64(config.UITextLLMKeyProtected) is not null;
        var hasCaiyunKey = DPAPI.UnprotectFromBase64(config.UITextCaiyunKeyProtected) is not null;

        // 默认选一个「现在就能用」的：存了 key 才预选大模型，否则预选免费（新手没有 key，预选大模型必然卡住）
        this.pendingStart = new PendingStart
        {
            Entry = entry,
            Choice = config.UITextChannel switch
            {
                "caiyun" => 1,
                "google" or "mymemory" => 2,
                "llm" => 0,
                _ => hasLlmKey ? 0 : hasCaiyunKey ? 1 : 2,
            },
        };
        this.firstRunError = string.Empty;
        this.pendingNeedsOpen = true;
    }

    private void StartOneClickCore(InstalledPluginEntry entry)
    {
        if (this.run is not null)
        {
            return;
        }

        if (UITextRules.IsDoNotLocalize(entry.InternalName))
        {
            this.notes[entry.InternalName] = new RowNote
            {
                Kind = NoteKind.Info,
                Text = "识别为中文插件（由朋友维护），FireGaze 不汉化它。",
            };
            return;
        }

        if (!this.runs.TryEnter(entry.InternalName, "正在汉化 " + entry.DisplayName, out var reason))
        {
            this.notes[entry.InternalName] = new RowNote
            {
                Kind = NoteKind.Info,
                Text = "另一个任务正在跑：" + reason + " 等它结束再点。",
            };
            return;
        }

        var run = new Run { InternalName = entry.InternalName, Stage = "准备中…" };
        this.run = run;
        this.notes.TryRemove(entry.InternalName, out _);
        run.Task = Task.Run(() => this.RunPipelineAsync(entry, run));
    }

    private async Task RunPipelineAsync(InstalledPluginEntry entry, Run run)
    {
        var token = run.Cancel.Token;
        try
        {
            // ① 抽取（盘上是我们的补丁时自动改读原始备份）
            // 先记两个时间点：「包」是不是在补丁之后又改过（改过就要重打，别被下面的“已是最新”跳过去）
            var packTimeBefore = this.store.LastWriteTime(entry.InternalName);
            var patchedAt = this.patches.PatchedAt(entry) ?? DateTime.MinValue;
            var packTouchedSincePatch = packTimeBefore > patchedAt.AddSeconds(1);

            run.Stage = "正在读取插件界面文本…";
            var guard = await Task.Run(() => this.patches.ExtractWithGuard(entry), token).ConfigureAwait(false);
            var extraction = guard.Extraction;
            if (extraction.Error is not null)
            {
                this.FinishRun(run, new RowNote
                {
                    Kind = NoteKind.Bad,
                    Text = "读不出界面文本（插件可能加壳 / 加密）：" + extraction.Error,
                });
                return;
            }

            var pack = this.store.Load(entry.InternalName);
            var merge = UITextFlow.MergeExtraction(pack, extraction);
            if (!this.store.Save(entry.InternalName, pack, out var saveError))
            {
                this.FinishRun(run, new RowNote { Kind = NoteKind.Bad, Text = "保存译文包失败：" + saveError });
                return;
            }

            if (merge.UICount + merge.AmbiguousCount + merge.ResourceCount + merge.AttributeCount == 0)
            {
                this.FinishRun(run, new RowNote
                {
                    Kind = NoteKind.Info,
                    Text = DescribeNothingToTranslate(extraction),
                });
                return;
            }

            if (run.Mode == RunMode.ExtractOnly)
            {
                var summary = $"抽取完成：候选 {merge.UICount} 条 · 灰名单 {merge.AmbiguousCount} 条 · 已翻译 {pack.TranslatedTotal} 条";
                if (extraction.ChineseExcludedCount > 0)
                {
                    summary += $" · 已是中文 {extraction.ChineseExcludedCount} 条";
                }
                if (merge.ResourceCount > 0 || merge.AttributeCount > 0)
                {
                    summary += $" · 资源 {merge.ResourceCount}（已译 {pack.TranslatedResourceCount}）· 属性 {merge.AttributeCount}（已译 {pack.TranslatedAttributeCount}）";
                }

                if (merge.Prune.Any)
                {
                    summary += " · " + merge.Prune.Describe();
                }

                summary += "。要翻译就点「一键汉化」，要逐条看就点「编辑校对…」。";

                this.FinishRun(run, new RowNote { Kind = NoteKind.Good, Text = summary });
                this.rowsDirty = true;
                return;
            }

            // ② 翻译没翻的条目
            var targets = UITextFlow.TranslationTargets(pack, merge.Roles, this.plugin.Config.UITextTranslateGreyList);

            // ②a 先看公共译文库有没有现成的（有就不用花用户自己的 key；合并规则：玩家自己改过的永不被顶）
            var libraryChanged = 0;
            if (targets.Count > 0 && this.plugin.Config.UITextLibraryEnabled)
            {
                run.Stage = "正在查公共译文库…";
                run.CanCancel = true; // 拉库也要能取消（限时 12 秒，拉不到就会直接跳过）
                libraryChanged = await this.plugin.TextLibrary.MergeIntoAsync(pack, entry.InternalName, token).ConfigureAwait(false);
                if (libraryChanged > 0)
                {
                    this.store.Save(entry.InternalName, pack, out _);
                    targets = UITextFlow.TranslationTargets(pack, merge.Roles, this.plugin.Config.UITextTranslateGreyList);
                    Plugin.Log?.Information($"[内部文本] {entry.InternalName}：译文库补入 {libraryChanged} 条，还需翻译 {targets.Count} 条");
                }
            }

            var translatedCount = 0;
            var channelNote = string.Empty;
            if (targets.Count == 0
                && !merge.ReapplyNeeded
                && libraryChanged == 0
                && !packTouchedSincePatch
                && this.patches.StatusOf(entry, out _) == UITextPatchStatus.Applied)
            {
                // 已经是最新：不重复写文件、也不白白重载一次插件
                this.FinishRun(run, new RowNote
                {
                    Kind = NoteKind.Info,
                    Text = "已是最新：没有要翻的条目，补丁也还在。想改某条译文，去行尾「详情 → 编辑校对」。",
                });
                this.rowsDirty = true;
                return;
            }

            if (targets.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var channel = UITextChannelFactory.Create(this.plugin.Config, out var channelError);
                if (channel is null)
                {
                    ActivityLog.Error("翻译", $"{entry.InternalName}：翻译通道不可用：{channelError}");
                    this.FinishRun(run, new RowNote
                    {
                        Kind = NoteKind.Bad,
                        Text = channelError ?? "翻译通道不可用。",
                        CanOpenSettings = true,
                    });
                    return;
                }

                run.Total = targets.Count;
                run.Done = 0;
                run.CanCancel = true;
                run.Stage = $"翻译 0 / {targets.Count} 条（{channel.Name}）";

                var items = UITextFlow.BuildTranslateItems(targets);
                var result = await channel.TranslateAsync(
                    items,
                    (done, _) =>
                    {
                        run.Done = done;
                        run.Stage = $"翻译 {done} / {targets.Count} 条（{channel.Name}）";
                    },
                    token).ConfigureAwait(false);

                var (applied, rejected, unchanged) = UITextFlow.AcceptTranslations(pack, result.Translated, channel.Name);
                translatedCount = applied;
                this.store.Save(entry.InternalName, pack, out _);

                ActivityLog.Info(
                    "翻译",
                    $"{entry.InternalName}：请求 {targets.Count} 条，成功 {result.Translated.Count}、失败 {result.Failed.Count}；写入 {applied}、占位符拒绝 {rejected}、原样 {unchanged}（{channel.Name}）");
                if (result.Failed.Count > 0)
                {
                    ActivityLog.Warning(
                        "翻译",
                        $"{entry.InternalName}：{result.Failed.Count} 条失败：" +
                        string.Join(" | ", result.Failed.Take(10).Select(s => UITextText.OneLine(s, 80))));
                }

                if (result.Error is not null || applied == 0 && result.Failed.Count > 0)
                {
                    var reason = result.Error ?? $"失败 {result.Failed.Count} 条";
                    ActivityLog.Error("翻译", $"{entry.InternalName}：翻译失败：{reason}");
                    this.FinishRun(run, new RowNote
                    {
                        Kind = NoteKind.Bad,
                        Text = $"翻译失败：{reason}",
                        Detail = $"已翻好的 {pack.TranslatedCount} 条不会丢；点「重试」接着翻，或到「翻译设置」换一条通道。",
                        CanRetry = true,
                        CanOpenSettings = true,
                    });
                    this.rowsDirty = true;
                    return;
                }

                if (applied == 0 && pack.TranslatedTotal == 0)
                {
                    // 通道把候选原样返回（多是本来就无需翻译）——这不是「打补丁失败」，别按失败报
                    var why = unchanged > 0
                        ? $"{unchanged} 条候选的翻译结果与原文一致（多半本来就无需翻译）"
                        : rejected > 0
                            ? $"{rejected} 条没过占位符校验"
                            : "翻译通道没有返回内容";
                    this.FinishRun(run, new RowNote
                    {
                        Kind = NoteKind.Info,
                        Text = $"没有可应用的译文：{why}。",
                    });
                    this.rowsDirty = true;
                    return;
                }

                if (result.Notes.Count > 0)
                {
                    channelNote = "（" + string.Join("；", result.Notes) + "）";
                }
            }

            // ③ 写入 + 重载（这一步不可取消）
            run.CanCancel = false;
            run.Stage = "正在写入插件并重载…";

            var (ok, message) = await this.patches.ApplyAndReloadAsync(entry).ConfigureAwait(false);
            this.rowsDirty = true;
            if (!ok)
            {
                this.FinishRun(run, new RowNote { Kind = NoteKind.Bad, Text = message });
                return;
            }

            var extra = translatedCount > 0 ? $"本次新翻 {translatedCount} 条。{channelNote}" : string.Empty;
            if (libraryChanged > 0)
            {
                extra = $"从译文库补了 {libraryChanged} 条。" + extra;
            }

            if (translatedCount == 0 && merge.ReapplyNeeded)
            {
                var reapply = "抽取结果变了（有新增文本或控件 ID 标记被修正），已按新的写法重打。";
                extra = libraryChanged > 0 ? $"从译文库补了 {libraryChanged} 条；" + reapply : reapply;
            }

            this.FinishRun(run, new RowNote
            {
                Kind = NoteKind.Good,
                Text = message + extra,
            });
        }
        catch (OperationCanceledException)
        {
            this.rowsDirty = true;
            this.FinishRun(run, new RowNote
            {
                Kind = NoteKind.Info,
                Text = "已取消；已经翻好的条目都保留着，之后点「一键汉化」会接着来。",
            });
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 一键汉化出错");
            this.FinishRun(run, new RowNote { Kind = NoteKind.Bad, Text = "出错：" + e.Message + "（点右侧「一键汉化」可以重来一次）" });
        }
    }

    /// <summary>
    ///     什么都没得翻时把原因说清楚：是根本没抽到，还是抽到的都是中文，还是都进了命令/日志/键名这类不翻的桶。
    ///     用户 2026-10-02 要求：「没有可以翻译的要直接和用户说，是未抽取到还是中文导致跳过的」。
    /// </summary>
    private static string DescribeNothingToTranslate(UITextExtraction extraction)
    {
        var chinese = extraction.ChineseExcludedCount;
        if (chinese > 0)
        {
            var rest = extraction.Entries.Count - chinese;
            return rest > 0
                ? $"没有需要翻译的内容：抽到 {chinese} 条文本是中文（自动跳过），其余 {rest} 条是命令 / 日志 / 键名这类不翻的内容。"
                : $"没有需要翻译的内容：抽到的 {chinese} 条文本全是中文，已经不需要翻译。";
        }

        return extraction.Entries.Count > 0
            ? $"没有可翻译的界面文本：抽到 {extraction.Entries.Count} 条文本，但都是命令 / 日志 / 键名这类不翻的内容。"
            : "没有抽取到可翻译的界面文本：这个 DLL 里没有找到界面文字。";
    }

    // ── 首次运行：选翻译方式 / 免费确认 ─────────────────────────────

    // ── 反馈（2026-10-03 用户要求：插件汉化第一行右侧常驻入口） ────────────

    private void DrawFeedbackModal()
    {
        const string Name = "反馈###UITextFeedback";
        if (this.feedbackNeedsOpen)
        {
            ImGui.OpenPopup(Name);
            this.feedbackNeedsOpen = false;
        }

        ImGui.SetNextWindowSizeConstraints(new Vector2(600, 0), new Vector2(780, float.MaxValue));
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal(Name, ImGuiWindowFlags.AlwaysAutoResize))
        {
            this.feedbackOpen = false;
            return;
        }

        ImGui.TextWrapped("遇到问题或想提建议——写在这里直接提交（匿名，不需要 GitHub 账号）。");
        ImGui.TextDisabled("会带上 FireGaze / 卫月版本与时间；勾选「附带诊断日志」能帮我们定位（路径与密钥已脱敏）。");
        ImGui.Spacing();

        var categories = new[] { "问题", "建议", "其他" };
        ImGui.SetNextItemWidth(150);
        ImGui.Combo("分类###UITextFeedbackCategory", ref this.feedbackCategory, categories, categories.Length);

        // 引导贡献翻译（2026-10-03 用户要求：反馈里不再单列「汉化不对」）
        UiHelpers.ColoredWrapped(
            UiHelpers.Muted,
            "想修正或补充某个插件的译文？到「插件汉化 → 该插件 → 编辑校对」改好后，用「更多 → 提交人工译文到公共库」直接贡献——比反馈更快被收录。");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline("###UITextFeedbackText", ref this.feedbackText, 4000, new Vector2(-1, 130));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("写清楚：哪个插件 / 点了什么 / 期望什么 / 实际什么。出错的原文也可以贴进来。");
        }

        ImGui.Checkbox("附带诊断日志（推荐）", ref this.feedbackAttachLog);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"最近 {FeedbackSender.LogEntries} 条 FireGaze 日志（Info 及以上），已脱敏。");
        }

        if (this.feedbackAttachLog)
        {
            ImGui.SameLine();
            if (ImGui.TreeNode("查看将发送的内容…###UITextFeedbackPreview"))
            {
                var preview = FeedbackSender.BuildBody(
                    categories[this.feedbackCategory],
                    this.feedbackText.Length > 0 ? this.feedbackText : "（还没写内容）",
                    attachLog: true);
                ImGui.InputTextMultiline("###UITextFeedbackPreviewText", ref preview, preview.Length + 1, new Vector2(-1, 160), ImGuiInputTextFlags.ReadOnly);
                ImGui.TreePop();
            }
        }

        ImGui.Separator();
        var canSend = !this.feedbackSending && this.feedbackText.Trim().Length >= 5;
        ImGui.BeginDisabled(!canSend);
        if (ImGui.Button(this.feedbackSending ? "发送中…" : "发送", new Vector2(96, 0)))
        {
            this.StartFeedbackSend();
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !canSend)
        {
            ImGui.SetTooltip("至少写 5 个字，让维护者能看懂发生了什么。");
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(80, 0)))
        {
            this.feedbackOpen = false;
            UiHelpers.ClosePopupAndEnd();
            return;
        }

        if (this.feedbackStatus.Length > 0)
        {
            ImGui.Spacing();
            UiHelpers.ColoredWrapped(this.feedbackStatusError ? UiHelpers.Bad : UiHelpers.Good, this.feedbackStatus);
        }

        if (this.feedbackSentURL.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("进度可以在这里跟踪（不登录也能看）：");
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(this.feedbackSentURL);
            ImGui.PopTextWrapPos();
            ImGui.SameLine();
            if (ImGui.SmallButton("复制链接###UITextFeedbackCopy"))
            {
                ImGui.SetClipboardText(this.feedbackSentURL);
            }
        }

        ImGui.SetItemDefaultFocus();
        ImGui.EndPopup();
    }

    private void StartFeedbackSend()
    {
        if (this.feedbackSending)
        {
            return;
        }

        this.feedbackSending = true;
        this.feedbackStatus = "正在发送…";
        this.feedbackStatusError = false;
        var categories = new[] { "问题", "建议", "其他" };
        var category = categories[Math.Clamp(this.feedbackCategory, 0, categories.Length - 1)];
        var text = this.feedbackText.Trim();
        var attachLog = this.feedbackAttachLog;

        _ = Task.Run(async () =>
        {
            try
            {
                ActivityLog.Info("反馈", $"提交反馈（{category}，{text.Length} 字，附带日志={attachLog}）");
                var (ok, message) = await FeedbackSender.SendAsync(category, text, attachLog).ConfigureAwait(false);
                this.feedbackSending = false;
                if (ok)
                {
                    this.feedbackStatus = "已提交，感谢反馈！";
                    this.feedbackStatusError = false;
                    this.feedbackSentURL = message;
                    this.feedbackText = string.Empty;
                    ActivityLog.Info("反馈", "提交成功：" + message);
                }
                else
                {
                    this.feedbackStatus = "发送失败：" + message + "（内容还在，可以稍后重试）";
                    this.feedbackStatusError = true;
                    ActivityLog.Warning("反馈", "提交失败：" + message);
                }
            }
            catch (Exception e)
            {
                this.feedbackSending = false;
                this.feedbackStatus = "发送失败：" + e.GetBaseException().Message + "（内容还在，可以稍后重试）";
                this.feedbackStatusError = true;
                ActivityLog.Error("反馈", "提交异常", e);
            }
        });
    }

    private void DrawPendingModals()
    {
        if (this.feedbackOpen)
        {
            this.DrawFeedbackModal();
        }
        if (this.pendingCloudApply is { } cloud)
        {
            this.DrawCloudApplyModal(cloud);
        }

        if (this.pendingUpload is { } upload)
        {
            this.DrawUploadConfirmModal(upload);
        }

        if (this.pendingRestore is { } restore)
        {
            this.DrawRestoreConfirmModal(restore);
        }

        var pending = this.pendingStart;
        if (pending is null)
        {
            return;
        }

        if (pending.AwaitingFreeConfirm)
        {
            this.DrawFreeConfirmModal(pending);
        }
        else
        {
            this.DrawFirstRunModal(pending);
        }
    }

    private void DrawFirstRunModal(PendingStart pending)
    {
        const string Name = "选择翻译方式###UITextFirstRun";
        if (this.pendingNeedsOpen)
        {
            ImGui.OpenPopup(Name);
            this.pendingNeedsOpen = false;
        }

        ImGui.SetNextWindowSizeConstraints(new Vector2(560, 0), new Vector2(660, float.MaxValue));
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal(Name, ImGuiWindowFlags.AlwaysAutoResize))
        {
            // 被 Esc / 点到外面关掉 = 取消（下次点「一键汉化」再问）
            this.notes[pending.Entry.InternalName] = new RowNote { Kind = NoteKind.Info, Text = "已取消，没有开始翻译。" };
            this.pendingStart = null;
            return;
        }

        ImGui.TextWrapped("「一键汉化」需要一个翻译方式。先花半分钟选好，之后随时能在「翻译设置…」里改。");
        ImGui.Spacing();

        var config = this.plugin.Config;
        if (ImGui.RadioButton("大模型（推荐）：速度最快、质量最好，用你自己的 API key", pending.Choice == 0) && pending.Choice != 0)
        {
            pending.Choice = 0;
            pending.SecretInput = string.Empty;
            pending.TestStatus = string.Empty;
            pending.TestGeneration++;
            this.firstRunError = string.Empty;
        }

        if (pending.Choice == 0)
        {
            ImGui.Indent(24f);
            this.DrawSecretRow(pending, "llm", config.UITextLLMKeyProtected, "粘贴大模型 API key（DeepSeek 就到 platform.deepseek.com → API keys 创建一个）");
            ImGui.TextDisabled("翻译会带上随插件打包的 FF14 官方译名（地名 / 副本 / 技能 / 状态…）当术语表，专有名词更准。");
            ImGui.Unindent(24f);
        }

        if (ImGui.RadioButton("彩云小译：免费额度（新号 100 万字 / 一个月），一次能翻 50 条", pending.Choice == 1) && pending.Choice != 1)
        {
            pending.Choice = 1;
            pending.SecretInput = string.Empty;
            pending.TestStatus = string.Empty;
            pending.TestGeneration++;
            this.firstRunError = string.Empty;
        }

        if (pending.Choice == 1)
        {
            ImGui.Indent(24f);
            ImGui.TextDisabled("到「彩云科技开放平台」注册 → 应用管理里创建应用 → 页面右边「管理」→「访问控制」里复制 token 填这里。");
            this.DrawSecretRow(pending, "caiyun", config.UITextCaiyunKeyProtected, "粘贴彩云小译 token");
            ImGui.TextDisabled("FF14 官方译名术语表只在「大模型」这一档生效，免费接口带不了。");
            ImGui.Unindent(24f);
        }

        if (ImGui.RadioButton("免费 Google / MyMemory：不用 key，但很慢、随时可能被限流", pending.Choice == 2) && pending.Choice != 2)
        {
            pending.Choice = 2;
            pending.SecretInput = string.Empty;
            pending.TestStatus = string.Empty;
            pending.TestGeneration++;
            this.firstRunError = string.Empty;
        }

        if (pending.Choice == 2)
        {
            ImGui.Indent(24f);
            ImGui.TextDisabled("FF14 官方译名术语表只在「大模型」这一档生效，免费接口带不了。");
            ImGui.Unindent(24f);
        }

        if (this.firstRunError.Length > 0)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Bad, this.firstRunError);
        }

        ImGui.Spacing();
        ImGui.TextDisabled("点「开始汉化」会翻译、改写插件 DLL、并自动重载这个插件；原文件会先备份，随时能「还原原文」。");
        ImGui.Separator();

        var ready = pending.Choice switch
        {
            0 => DPAPI.UnprotectFromBase64(config.UITextLLMKeyProtected) is not null || pending.SecretInput.Trim().Length > 0,
            1 => DPAPI.UnprotectFromBase64(config.UITextCaiyunKeyProtected) is not null || pending.SecretInput.Trim().Length > 0,
            _ => true,
        };

        ImGui.BeginDisabled(!ready);
        UiHelpers.PushPrimaryButton();
        if (ImGui.Button("开始汉化", new Vector2(140, 0)))
        {
            this.ConfirmFirstRun(pending);
        }

        UiHelpers.PopPrimaryButton();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(ready ? "按上面选好的通道开始汉化。" : "先把上面选中通道的 key / token 填上。");
        }

        ImGui.SetItemDefaultFocus();

        ImGui.SameLine();
        if (ImGui.Button("翻译设置…", new Vector2(120, 0)))
        {
            this.pendingStart = null;
            ImGui.CloseCurrentPopup();
            this.notes[pending.Entry.InternalName] = new RowNote
            {
                Kind = NoteKind.Info,
                Text = "已打开翻译设置：选好通道后，回到这一行再点一次「一键汉化」就行。",
            };
            this.settings.IsOpen = true;
            this.settings.BringToFront();
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(90, 0)))
        {
            this.pendingStart = null;
            ImGui.CloseCurrentPopup();
            this.notes[pending.Entry.InternalName] = new RowNote { Kind = NoteKind.Info, Text = "已取消，没有开始翻译。" };
        }

        ImGui.EndPopup();
    }

    /// <summary>一行「已保存 / 粘贴新的」密钥输入（首次运行弹窗里用）。</summary>
    private void DrawSecretRow(PendingStart pending, string id, string currentProtected, string hint)
    {
        var existing = DPAPI.UnprotectFromBase64(currentProtected);
        if (existing is null)
        {
            ImGui.SetNextItemWidth(360);
            ImGui.InputTextWithHint("###firstrun-" + id, hint, ref pending.SecretInput, 256, ImGuiInputTextFlags.Password);
        }
        else
        {
            UiHelpers.ColoredText(UiHelpers.Good, "已保存 " + DPAPI.Mask(existing) + "；要换就直接在下面粘贴新的。");
            ImGui.SetNextItemWidth(360);
            ImGui.InputTextWithHint("###firstrun-" + id, "粘贴新的以更换", ref pending.SecretInput, 256, ImGuiInputTextFlags.Password);
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(pending.TestRunning);
        if (ImGui.Button("测试###firstrun-test-" + id))
        {
            this.StartFirstRunTest(pending);
        }

        ImGui.EndDisabled();
        if (pending.TestStatus.Length > 0)
        {
            UiHelpers.ColoredText(pending.TestOk ? UiHelpers.Good : UiHelpers.Bad, pending.TestStatus);
        }

        ImGui.TextDisabled("key 只存在本机（加密保存），不会上传，也不会随任何提交发出去。");
    }

    /// <summary>拿还没保存的那份 key/token 试翻一条，省得翻译跑起来才发现填错。</summary>
    private void StartFirstRunTest(PendingStart pending)
    {
        if (pending.TestRunning)
        {
            return;
        }

        var config = this.plugin.Config;
        var probe = new Configuration
        {
            UITextChannel = pending.Choice == 0 ? "llm" : "caiyun",
            UITextLLMProvider = config.UITextLLMProvider,
            UITextLLMBaseURL = config.UITextLLMBaseURL,
            UITextLLMModel = config.UITextLLMModel,
            UITextLLMKeyProtected = config.UITextLLMKeyProtected,
            UITextCaiyunKeyProtected = config.UITextCaiyunKeyProtected,
        };

        var input = pending.SecretInput.Trim();
        if (input.Length > 0)
        {
            var protectedValue = DPAPI.ProtectToBase64(input);
            if (pending.Choice == 0)
            {
                probe.UITextLLMKeyProtected = protectedValue;
            }
            else
            {
                probe.UITextCaiyunKeyProtected = protectedValue;
            }
        }

        var channel = UITextChannelFactory.Create(probe, out var error);
        if (channel is null)
        {
            pending.TestOk = false;
            pending.TestStatus = error ?? "通道不可用。";
            return;
        }

        pending.TestRunning = true;
        pending.TestOk = false;
        pending.TestStatus = "测试中…";
        pending.TestGeneration++;
        var generation = pending.TestGeneration;
        var items = new List<UITextTranslateItem> { new("Settings", "test") };
        var probeChannel = channel;
        _ = Task.Run(async () =>
        {
            void Write(bool ok, string status)
            {
                // 切单选 / 重开弹窗后旧的试连结果不许回写到新的那一行
                if (pending.TestGeneration == generation)
                {
                    pending.TestOk = ok;
                    pending.TestStatus = status;
                    pending.TestRunning = false;
                }
            }

            try
            {
                var result = await probeChannel.TranslateAsync(items, null, CancellationToken.None).ConfigureAwait(false);
                if (result.Translated.TryGetValue("Settings", out var text) && !string.IsNullOrWhiteSpace(text))
                {
                    Write(true, $"连接正常（{probeChannel.Name}）：Settings → {text}");
                }
                else
                {
                    Write(false, "连接失败：" + (result.Error ?? "没有返回译文"));
                }
            }
            catch (Exception e)
            {
                Write(false, "连接失败：" + e.Message);
            }
        });
    }

    private void ConfirmFirstRun(PendingStart pending)
    {
        // 只校验，不写盘：真正写配置推到「确认过免费很慢」或直接开始时（取消不能静默改通道）
        if (pending.Choice == 0
            && DPAPI.UnprotectFromBase64(this.plugin.Config.UITextLLMKeyProtected) is null
            && pending.SecretInput.Trim().Length == 0)
        {
            this.firstRunError = "还没填大模型 API key：把 key 粘到上面，或改选「免费 Google / MyMemory」。";
            return;
        }

        if (pending.Choice == 1
            && DPAPI.UnprotectFromBase64(this.plugin.Config.UITextCaiyunKeyProtected) is null
            && pending.SecretInput.Trim().Length == 0)
        {
            this.firstRunError = "还没填彩云小译 token：把 token 粘到上面，或改选「免费 Google / MyMemory」。";
            return;
        }

        this.firstRunError = string.Empty;
        if (pending.Choice == 2 && !this.plugin.Config.UITextFreeWarned)
        {
            pending.AwaitingFreeConfirm = true;
            this.pendingNeedsOpen = true;
            ImGui.CloseCurrentPopup();
            return;
        }

        this.ApplyPendingChoice(pending);
        this.pendingStart = null;
        ImGui.CloseCurrentPopup();
        this.StartOneClickCore(pending.Entry);
    }

    /// <summary>把弹窗里选好的通道写进配置（只在真正要开始时调）。</summary>
    private void ApplyPendingChoice(PendingStart pending)
    {
        var config = this.plugin.Config;
        var input = pending.SecretInput.Trim();
        if (pending.Choice == 0)
        {
            if (input.Length > 0)
            {
                var protectedValue = DPAPI.ProtectToBase64(input);
                if (protectedValue.Length > 0)
                {
                    config.UITextLLMKeyProtected = protectedValue;
                }
            }

            config.UITextChannel = "llm";
            if (string.IsNullOrWhiteSpace(config.UITextLLMBaseURL))
            {
                config.UITextLLMBaseURL = "https://api.deepseek.com/v1";
            }

            if (string.IsNullOrWhiteSpace(config.UITextLLMModel))
            {
                config.UITextLLMModel = "deepseek-flash";
            }
        }
        else if (pending.Choice == 1)
        {
            if (input.Length > 0)
            {
                var protectedValue = DPAPI.ProtectToBase64(input);
                if (protectedValue.Length > 0)
                {
                    config.UITextCaiyunKeyProtected = protectedValue;
                }
            }

            config.UITextChannel = "caiyun";
        }
        else
        {
            config.UITextChannel = "google";
        }

        config.UITextChannelChosen = true;
        this.plugin.SaveConfig();
    }

    private void DrawFreeConfirmModal(PendingStart pending)
    {
        const string Name = "免费接口会慢很多###UITextFreeConfirm";
        if (this.pendingNeedsOpen)
        {
            ImGui.OpenPopup(Name);
            this.pendingNeedsOpen = false;
        }

        ImGui.SetNextWindowSizeConstraints(new Vector2(520, 0), new Vector2(620, float.MaxValue));
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal(Name, ImGuiWindowFlags.AlwaysAutoResize))
        {
            this.notes[pending.Entry.InternalName] = new RowNote { Kind = NoteKind.Info, Text = "已取消，没有开始翻译。" };
            this.pendingStart = null;
            return;
        }

        UiHelpers.ColoredWrapped(UiHelpers.Warn, $"「{pending.Entry.DisplayName}」要走免费接口，会慢很多。");
        var estimate = this.FreeEstimate(pending);
        ImGui.TextWrapped(estimate.Length > 0
            ? $"免费接口是一条一条翻的，每个词条约 0.8 秒；{estimate}"
            : "免费接口是一条一条翻的，每个词条约 0.8 秒。");
        ImGui.TextWrapped("Google 免 key 端点随时可能限流（429），MyMemory 每天只有约 5000 词。");
        ImGui.Spacing();
        ImGui.TextDisabled("大模型（几秒翻完）和彩云小译（免费额度）都快得多；「翻译设置…」里随时能改。");
        ImGui.TextDisabled("点下去就会开始翻译、改写插件 DLL、并自动重载这个插件；原文件会先备份，随时能「还原原文」。");
        ImGui.Separator();
        if (ImGui.Button("仍要使用免费接口", new Vector2(170, 0)))
        {
            this.plugin.Config.UITextFreeWarned = true;
            this.ApplyPendingChoice(pending);
            this.pendingStart = null;
            UiHelpers.ClosePopupAndEnd();
            this.StartOneClickCore(pending.Entry);
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("改用大模型…", new Vector2(130, 0)))
        {
            pending.Choice = 0;
            pending.SecretInput = string.Empty;
            pending.TestStatus = string.Empty;
            pending.TestGeneration++;
            pending.AwaitingFreeConfirm = false;
            this.pendingNeedsOpen = true;
            UiHelpers.ClosePopupAndEnd();
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(90, 0)))
        {
            this.pendingStart = null;
            UiHelpers.ClosePopupAndEnd();
            this.notes[pending.Entry.InternalName] = new RowNote { Kind = NoteKind.Info, Text = "已取消，没有开始翻译。" };
            return;
        }

        // 昂贵动作的默认焦点放「取消」：按 Enter 不会一头撞上免费接口
        ImGui.SetItemDefaultFocus();
        ImGui.EndPopup();
    }

    /// <summary>按包里的剩余条数估一下免费通道要多久（只在有包时给）。</summary>
    private string FreeEstimate(PendingStart pending)
    {
        if (this.rows.TryGetValue(pending.Entry.InternalName, out var info) && info.HasPack)
        {
            var remaining = Math.Max(0, info.Total - info.Skipped - info.Translated);
            if (remaining > 0)
            {
                var minutes = Math.Max(1, (int)Math.Ceiling(remaining * 0.8 / 60.0));
                return $"这个插件还有 {remaining} 条要翻，大约 {minutes} 分钟。";
            }
        }

        return string.Empty;
    }

    private void FinishRun(Run run, RowNote note)
    {
        Plugin.Log?.Information($"[内部文本] 一键汉化 {run.InternalName}：{note.Text}");
        ActivityLog.Write(
            note.Kind == NoteKind.Bad ? ActivityLevel.Error : ActivityLevel.Info,
            "任务",
            $"{run.InternalName}：{note.Text}");
        this.notes[run.InternalName] = note;
        this.runs.Exit(run.InternalName);
        run.CanCancel = false;
        run.Finished = true;

        // 跑完立刻重算行状态：还原 / 汉化后，筛选（如「已汉化」）要马上反映出来，不等下一个 5 秒刷新
        this.rowsDirty = true;
    }

    private void PollRun()
    {
        var run = this.run;
        if (run is null || !run.Finished)
        {
            return;
        }

        run.Cancel.Dispose();
        this.run = null;
        this.rowsDirty = true;
    }

    /// <summary>
    ///     还原原文（自动重载）。
    /// </summary>
    /// <summary>打开还原确认框（会改插件文件并自动重载，先问一次）。</summary>
    private void RestoreNow(InstalledPluginEntry entry)
    {
        if (this.run is not null || !this.patches.HasBackup(entry))
        {
            return;
        }

        this.pendingRestore = new PendingRestore { Entry = entry };
        this.restoreModalNeedsOpen = true;
    }

    private sealed class PendingRestore
    {
        public InstalledPluginEntry Entry = null!;
    }

    private PendingRestore? pendingRestore;
    private bool restoreModalNeedsOpen;

    /// <summary>还原确认框：写清范围与后果，默认焦点在「取消」。</summary>
    private void DrawRestoreConfirmModal(PendingRestore restore)
    {
        const string Name = "还原原文###UITextRestoreConfirm";
        if (this.restoreModalNeedsOpen)
        {
            ImGui.OpenPopup(Name);
            this.restoreModalNeedsOpen = false;
        }

        ImGui.SetNextWindowSizeConstraints(new Vector2(480, 0), new Vector2(620, float.MaxValue));
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal(Name, ImGuiWindowFlags.AlwaysAutoResize))
        {
            this.pendingRestore = null;
            return;
        }

        ImGui.TextWrapped($"把「{restore.Entry.DisplayName}」还原成打补丁之前的原始文件？");
        ImGui.TextDisabled("会写入插件目录并自动重载插件，界面回到英文；之后还可以再「一键汉化」。");
        ImGui.Separator();
        if (ImGui.Button("还原", new Vector2(100, 0)))
        {
            var confirmed = restore.Entry;
            this.pendingRestore = null;
            UiHelpers.ClosePopupAndEnd();
            this.RunRestore(confirmed);
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(90, 0)))
        {
            this.pendingRestore = null;
            UiHelpers.ClosePopupAndEnd();
            return;
        }

        ImGui.SetItemDefaultFocus();
        ImGui.EndPopup();
    }

    /// <summary>确认后真正执行还原。</summary>
    private void RunRestore(InstalledPluginEntry entry)
    {
        if (this.run is not null)
        {
            return;
        }

        if (!this.runs.TryEnter(entry.InternalName, "正在还原 " + entry.DisplayName, out var reason))
        {
            this.notes[entry.InternalName] = new RowNote
            {
                Kind = NoteKind.Info,
                Text = "另一个任务正在跑：" + reason + " 等它结束再点。",
            };
            return;
        }

        var run = new Run { InternalName = entry.InternalName, Stage = "正在还原并重载…" };
        this.run = run;
        this.notes.TryRemove(entry.InternalName, out _);
        run.Task = Task.Run(async () =>
        {
            var (ok, message) = await this.patches.RestoreAndReloadAsync(entry).ConfigureAwait(false);
            this.FinishRun(run, new RowNote { Kind = ok ? NoteKind.Good : NoteKind.Bad, Text = message });
        });
    }

    // ── 图标 / 刷新 ──────────────────────────────────────────────────────

    private bool TryDrawIcon(InstalledPluginEntry entry, float size)
    {
        if (this.plugin.Icons.TryGetHandle(entry, out var handle) && !handle.IsNull)
        {
            ImGui.Image(handle, new Vector2(size, size));
            return true;
        }

        if (PluginIconLookup.TryPeekHandle(entry, out handle) && !handle.IsNull)
        {
            ImGui.Image(handle, new Vector2(size, size));
            return true;
        }

        return false;
    }

    private static void DrawLetterIcon(string name, float size)
    {
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            start,
            start + new Vector2(size, size),
            ImGui.GetColorU32(new Vector4(0.22f, 0.25f, 0.31f, 1f)),
            5f);

        var initial = string.IsNullOrEmpty(name) ? "?" : name[..1].ToUpperInvariant();
        var textSize = ImGui.CalcTextSize(initial);
        drawList.AddText(
            start + ((new Vector2(size, size) - textSize) * 0.5f),
            ImGui.GetColorU32(new Vector4(0.80f, 0.86f, 0.96f, 1f)),
            initial);

        ImGui.Dummy(new Vector2(size, size));
    }

    /// <summary>
    ///     读一遍每个插件的包摘要 + 补丁状态（每 5 秒 / 动作后刷新；不要每帧读盘）。
    /// </summary>
    private void RefreshRows()
    {
        var info = new Dictionary<string, RowInfo>(StringComparer.Ordinal);
        if (this.index is { Available: true })
        {
            foreach (var entry in this.index.All)
            {
                var row = new RowInfo
                {
                    EditorOpen = this.editor.IsOpen && string.Equals(this.editor.CurrentInternalName, entry.InternalName, StringComparison.Ordinal),
                    DoNotLocalize = UITextRules.IsDoNotLocalize(entry.InternalName),
                    PackNewerThanPatch = this.patches.PackNewerThanPatch(entry),
                };

                var pack = this.store.Load(entry.InternalName);
                if (File.Exists(Path.Combine(this.store.DirectoryPath, entry.InternalName + ".json")))
                {
                    row.HasPack = true;
                    row.Total = pack.Entries.Count + pack.Resources.Count + pack.Attributes.Count;
                    row.Translated = pack.TranslatedTotal;
                    row.Untranslated = pack.Entries.Count(e => !e.HasTranslation && !pack.IsSkipped(e.Original))
                                       + pack.Resources.Count(r => !r.HasTranslation && !pack.IsResourceSkipped(r.Container, r.Key))
                                       + pack.Attributes.Count(a => !a.HasTranslation && !pack.IsAttributeSkipped(a.Original));
                    row.Skipped = pack.Skipped.Count + pack.SkippedResources.Count + pack.SkippedAttributes.Count;
                }

                // 界面入口现读一次（行刷新 5 秒一回，不在每帧做反射）
                var (hasMainUI, hasConfigUI) = PluginUiBridge.Probe(entry.RawPlugin);
                row.HasMainUI = hasMainUI;
                row.HasConfigUI = hasConfigUI;

                row.Patch = this.patches.StatusOf(entry, out var detail);
                row.PatchDetail = detail;
                row.HasBackup = this.patches.HasBackup(entry);
                info[entry.InternalName] = row;
            }
        }

        this.rows = info;
        this.rowsAt = DateTime.Now;
        this.rowsDirty = false;
    }
}
