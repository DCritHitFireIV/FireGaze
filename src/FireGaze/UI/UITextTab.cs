using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;
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
        public int Skipped;
        public UITextPatchStatus Patch;
        public string PatchDetail = string.Empty;
        public bool HasBackup;
        public bool EditorOpen;

        /// <summary>插件注册了主界面 / 设置界面（汉化完成后「打开」按钮靠它）。</summary>
        public bool HasMainUI;

        public bool HasConfigUI;
    }

    private sealed class RowNote
    {
        public string Text = string.Empty;
        public NoteKind Kind;
        public bool CanOpenSettings;
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
    private string search = string.Empty;
    private bool onlyThirdParty;
    private string expanded = string.Empty;
    private RowFilter filter = RowFilter.All;

    private Dictionary<string, RowInfo> rows = new(StringComparer.Ordinal);
    private DateTime rowsAt = DateTime.MinValue;

    private readonly Dictionary<string, RowNote> notes = new(StringComparer.Ordinal);
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
        var filterLabels = new[] { "全部", "未汉化", "待应用", "已汉化", "失败" };
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
        if (ImGui.Button("翻译设置…"))
        {
            this.settings.IsOpen = true;
            this.settings.BringToFront();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("翻译方式、API key、灰名单口径、插件更新后是否自动重打——都在这里改。");
        }

        if (this.index is null)
        {
            this.index = InstalledPluginsIndex.Build();
            this.indexAt = DateTime.Now;
            this.rowsDirty = true;
        }

        if (!this.index.Available)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Bad, "读不到卫月的插件列表（" + (this.index.FailureReason ?? "未知原因") + "）");
            return;
        }

        if ((DateTime.Now - this.rowsAt).TotalSeconds > 5 || this.rowsDirty)
        {
            this.RefreshRows();
        }

        var patched = this.rows.Values.Count(r => r.Patch == UITextPatchStatus.Applied);
        ImGui.SameLine();
        ImGui.TextDisabled($"已装 {this.index.All.Count} · 已汉化 {patched} · {this.indexAt:HH:mm:ss} 读取");
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
        if (!ImGui.BeginTable("###UITextPlugins", 2, flags, new Vector2(0, -1)))
        {
            return;
        }

        ImGui.TableSetupColumn("plugin", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("actions", ImGuiTableColumnFlags.WidthFixed, 220);

        foreach (var plugin in items)
        {
            this.DrawPluginRow(plugin);
        }

        ImGui.EndTable();
    }

    private bool MatchesFilter(InstalledPluginEntry entry)
    {
        var info = this.rows.GetValueOrDefault(entry.InternalName);
        return this.filter switch
        {
            RowFilter.Untranslated => info is null || !info.HasPack || info.Translated == 0,
            RowFilter.Pending => info is { HasPack: true, Translated: > 0 } && info.Patch != UITextPatchStatus.Applied,
            RowFilter.Done => info is { Patch: UITextPatchStatus.Applied },
            RowFilter.Failed => info is { Patch: UITextPatchStatus.Failed } || this.notes.TryGetValue(entry.InternalName, out var n) && n.Kind == NoteKind.Bad,
            _ => true,
        };
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

        // ── 左列：图标 + 文本块 ──
        ImGui.TableNextColumn();
        var rowStartY = ImGui.GetCursorPosY();

        const float iconSize = 40f;
        if (!this.TryDrawIcon(plugin, iconSize))
        {
            DrawLetterIcon(plugin.DisplayName, iconSize);
        }

        ImGui.SameLine();
        ImGui.BeginGroup();
        {
            ImGui.TextUnformatted(plugin.DisplayName);
            if (!plugin.IsThirdParty)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("[官方]");
            }

            var punchline = plugin.Punchline;
            if (string.IsNullOrWhiteSpace(punchline))
            {
                punchline = plugin.Description;
            }

            if (!string.IsNullOrWhiteSpace(punchline))
            {
                UiHelpers.Truncated(punchline.Replace('\n', ' '), 96, punchline);
            }

            // meta 行：状态徽标在行首（固定 x，方便竖向扫） + 内部名 · 版本
            var (badge, color) = this.DescribeState(info, running);
            UiHelpers.ColoredText(color, badge);
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
        if (running)
        {
            ImGui.BeginDisabled();
            ImGui.Button("一键汉化");
            ImGui.EndDisabled();
        }
        else
        {
            // 还没汉化好的插件给主色「一键汉化」；汉化完成的变「打开」（2026-10-02 用户要求：
            // 汉化完就想直接看效果，而不是台上一直摆着个「一键汉化」）。
            var fullyLocalized = info is { HasPack: true }
                                 && (info.Patch == UITextPatchStatus.Applied || info.Total == 0);
            if (!fullyLocalized)
            {
                ImGui.BeginDisabled(busy || editorOpen);
                UiHelpers.PushPrimaryButton();
                if (ImGui.Button("一键汉化"))
                {
                    this.StartOneClick(plugin);
                }

                UiHelpers.PopPrimaryButton();
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(
                        (busy ? "有另一个插件正在汉化，等它跑完再点。\n" : string.Empty) +
                        (editorOpen ? "这个插件正开着编辑窗口，先关掉它（避免两份修改互相覆盖）。\n" : string.Empty) +
                        "翻译没翻的条目 → 写入插件 DLL → 自动重载插件。\n原文件会先备份，随时可以「还原原文」。");
                }
            }
            else
            {
                this.DrawOpenPluginButton(plugin, info!);
            }

            if (info is { HasBackup: true })
            {
                ImGui.SameLine(0, 12);
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

        ImGui.SameLine(0, 10);
        if (ImGui.Button(isOpen ? "收起" : "详情"))
        {
            this.expanded = isOpen ? string.Empty : plugin.InternalName;
        }
    }

    /// <summary>
    ///     汉化完成的插件行：按钮变「打开」——有主界面开主界面，没有主界面就开设置界面，
    ///     两者都没有就置灰（点了也不会有反应）。能不能开、开哪个都从 <see cref="PluginUiBridge" /> 现读，
    ///     和官方插件安装器的按钮同一套机制（反射 <c>LocalPlugin.DalamudInterface.LocalUiBuilder</c>；
    ///     不能强转 <c>IExposedPlugin</c>——那不是同一个对象）。
    /// </summary>
    private void DrawOpenPluginButton(InstalledPluginEntry plugin, RowInfo info)
    {
        var hasMain = info.HasMainUI;
        var hasConfig = info.HasConfigUI;
        var canOpen = hasMain || hasConfig;

        // 有主界面就叫「打开」，只能开设置就叫「设置」；都没有就叫「打开」但置灰（点了不会有反应）
        var label = hasMain || !hasConfig ? "打开" : "设置";
        ImGui.BeginDisabled(!canOpen);
        if (ImGui.Button(label))
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
            var candidates = info.Total - info.Skipped;
            var patchText = info.Patch switch
            {
                UITextPatchStatus.Applied => "已应用（原始文件已备份）",
                UITextPatchStatus.PendingReload => "已应用，等待重载确认",
                UITextPatchStatus.NeedsRepatch => "插件更新过，需要重打",
                UITextPatchStatus.Failed => "上次失败，已还原",
                _ => "未应用" + (info.HasBackup ? "（原始文件已备份）" : string.Empty),
            };
            ImGui.TextDisabled(
                $"候选 {candidates} 条 · 已翻译 {info.Translated} 条 · 未翻译 {candidates - info.Translated} 条 · 不翻 {info.Skipped} 条 ｜ 补丁：{patchText}");

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
                      + "插件更新过、或想先看看有多少文本，就点它；结果会记进本地包，编辑器里也能接着改。");
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

        ImGui.Unindent(48f);
    }

    private void DrawNote(InstalledPluginEntry plugin, RowNote note)
    {
        // 成功是一次性事件：十几秒后收起；失败 / 未加载是持久状态，留到下一次操作。
        if (note.Kind == NoteKind.Good && (DateTime.Now - note.CreatedAt).TotalSeconds > 12)
        {
            this.notes.Remove(plugin.InternalName);
            return;
        }

        var color = note.Kind switch
        {
            NoteKind.Good => UiHelpers.Good,
            NoteKind.Bad => UiHelpers.Bad,
            _ => UiHelpers.Muted,
        };

        UiHelpers.ColoredWrapped(color, note.Text);

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

        if (info is null || !info.HasPack)
        {
            return ("未汉化", UiHelpers.Muted);
        }

        switch (info.Patch)
        {
            case UITextPatchStatus.Applied:
                return ($"已汉化 {info.Translated} 处", UiHelpers.Good);
            case UITextPatchStatus.PendingReload:
                return ($"已汉化 {info.Translated} 处 · 待重载", UiHelpers.Info);
            case UITextPatchStatus.NeedsRepatch:
                return ($"已汉化 {info.Translated} 处 · 需要重打", UiHelpers.Warn);
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
        this.notes.Remove(entry.InternalName);
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
        this.notes.Remove(entry.InternalName);
        run.Task = Task.Run(() => this.RunPipelineAsync(entry, run));
    }

    private async Task RunPipelineAsync(InstalledPluginEntry entry, Run run)
    {
        var token = run.Cancel.Token;
        try
        {
            // ① 抽取（盘上是我们的补丁时自动改读原始备份）
            // 先记两个时间点：「包」是不是在补丁之后又改过（改过就要重打，别被下面的“已是最新”跳过去）
            var packPath = Path.Combine(this.store.DirectoryPath, entry.InternalName + ".json");
            var packTimeBefore = File.Exists(packPath) ? File.GetLastWriteTime(packPath) : DateTime.MinValue;
            var patchedAt = this.patches.PatchedAt(entry) ?? DateTime.MinValue;
            var packTouchedSincePatch = packTimeBefore > patchedAt.AddSeconds(1);

            run.Stage = "正在读取插件界面文本…";
            var extractionSources = this.patches.ExtractionSourceOf(entry, out _, out var extractionSearchDirs);
            var extraction = await Task.Run(() => UIStringExtractor.ExtractMany(extractionSources, extractionSearchDirs), token).ConfigureAwait(false);
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
                    Text = "没找到可翻译的界面文本（插件可能带壳 / 加密，或界面本来就是中文）。",
                });
                return;
            }

            if (run.Mode == RunMode.ExtractOnly)
            {
                var summary = $"抽取完成：候选 {merge.UICount} 条 · 灰名单 {merge.AmbiguousCount} 条 · 已翻译 {pack.TranslatedTotal} 条";
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
                _ = rejected;
                _ = unchanged;

                if (result.Error is not null || applied == 0 && result.Failed.Count > 0)
                {
                    var reason = result.Error ?? $"失败 {result.Failed.Count} 条";
                    this.FinishRun(run, new RowNote
                    {
                        Kind = NoteKind.Bad,
                        Text = $"翻译失败：{reason}。已经翻好的 {pack.TranslatedCount} 条不会丢，原来的补丁也还在（界面不会变回英文）——"
                               + "等几分钟再点右侧「一键汉化」接着来；想稳定跑，去「翻译设置」里换成彩云小译（免费、有额度、一次 50 条）或自己的大模型 key。",
                        CanOpenSettings = true,
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
                this.FinishRun(run, new RowNote
                {
                    Kind = NoteKind.Bad,
                    Text = message + "（点右侧「一键汉化」可以重来一次）",
                });
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

    // ── 首次运行：选翻译方式 / 免费确认 ─────────────────────────────

    private void DrawPendingModals()
    {
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
            ImGui.CloseCurrentPopup();
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
            ImGui.CloseCurrentPopup();
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("取消", new Vector2(90, 0)))
        {
            this.pendingStart = null;
            ImGui.CloseCurrentPopup();
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
        this.notes[run.InternalName] = note;
        this.runs.Exit(run.InternalName);
        run.CanCancel = false;
        run.Finished = true;
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
    private void RestoreNow(InstalledPluginEntry entry)
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
        this.notes.Remove(entry.InternalName);
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
                };

                var pack = this.store.Load(entry.InternalName);
                if (File.Exists(Path.Combine(this.store.DirectoryPath, entry.InternalName + ".json")))
                {
                    row.HasPack = true;
                    row.Total = pack.Entries.Count + pack.Resources.Count + pack.Attributes.Count;
                    row.Translated = pack.TranslatedTotal;
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
