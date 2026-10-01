using System.Diagnostics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using FireGaze.RepoAudit;
using FireGaze.Translate;
using FireGaze.UIText;

namespace FireGaze.UI;

/// <summary>
///     「插件汉化」编辑器：一个插件一个窗口，逐条改插件界面上的英文文本。
///     抽取只认「会画到界面上的字符串」（<see cref="UIStringExtractor" />），灰名单默认不翻。
/// </summary>
internal sealed class UITextEditorWindow : Window
{
    private enum Filter
    {
        Candidates,
        Ambiguous,
        All,
        Untranslated,
        Translated,
        Skipped,
    }

    /// <summary>
    ///     界面上一行：抽取结果里的一条字面量 + 包里的译文；资源行（<see cref="Resource" />）是
    ///     内嵌 <c>.resources</c> 容器里的一条（身份 = 容器 + key）。两种行二选一。
    /// </summary>
    private sealed class Row
    {
        public UITextPackEntry? Entry;
        public UITextResourceEntry? Resource;
        public UITextRole Role;
        public string Reason = string.Empty;
        public bool Skipped;

        public bool IsResource => this.Resource is not null;

        public string Original => this.Resource?.Original ?? this.Entry!.Original;

        public string? Context => this.Resource is not null
            ? "资源：" + this.Resource.Container + " · " + this.Resource.Key
            : this.Entry!.Context;

        public bool HasTranslation => this.Resource?.HasTranslation ?? this.Entry!.HasTranslation;

        public string Translated => this.Resource?.Translated ?? this.Entry!.Translated;

        public string? Review => this.Resource?.Review ?? this.Entry!.Review;

        public string? Source => this.Resource?.Source ?? this.Entry!.Source;

        public bool IsUserSource => this.Resource?.IsUserSource ?? this.Entry!.IsUserSource;
    }

    private readonly Plugin plugin;
    private readonly UITextStore store;
    private readonly UITextPatchManager patches;
    private readonly UITextRunLock runs;
    private readonly FileDialogManager fileDialog = new();

    private InstalledPluginEntry? entry;
    private UITextPack pack = new();
    private Task<UITextExtraction>? extractionTask;
    private UITextExtraction? extraction;
    private string extractionNote = string.Empty;
    private readonly Dictionary<string, (UITextRole Role, string Reason)> roles = new(StringComparer.Ordinal);
    private List<Row> rows = [];
    private readonly List<Row> visible = [];
    private string search = string.Empty;
    private Filter filter = Filter.Candidates;
    private string status = "打开插件后自动抽取。";
    private bool statusIsError;
    private bool dirty;
    private DateTime dirtySince = DateTime.MinValue;
    private DateTime lastSaveAt = DateTime.MinValue;

    // 翻译通道
    private Task<(string Channel, UITextTranslateResult Result)>? translateTask;
    private Task<(bool Ok, string Message)>? patchTask;
    private CancellationTokenSource? translateCancel;
    private int translateDone;
    private int translateTotal;

    public UITextEditorWindow(Plugin plugin, UITextStore store, UITextPatchManager patches, UITextRunLock runs)
        : base("插件汉化 — 编辑校对###FireGazeUITextEditor", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        this.store = store;
        this.patches = patches;
        this.runs = runs;
        this.Size = new System.Numerics.Vector2(980, 640);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(640, 420),
        };
    }

    /// <summary>
    ///     当前插件。
    /// </summary>
    public string? CurrentInternalName => this.entry?.InternalName;

    /// <summary>
    ///     换一个插件（自动抽取；同一个插件再点一次只是把窗口带到前面）。
    /// </summary>
    public void OpenFor(InstalledPluginEntry target)
    {
        this.IsOpen = true;
        this.BringToFront();

        if (this.entry is not null && string.Equals(this.entry.InternalName, target.InternalName, StringComparison.Ordinal))
        {
            return;
        }

        this.entry = target;
        this.pack = this.store.Load(target.InternalName);
        this.rows = [];
        this.roles.Clear();
        this.search = string.Empty;
        this.dirty = false;
        this.extraction = null;
        this.extractionTask = null;
        this.SetStatus($"已载入 {target.DisplayName}：正在抽取界面文本…", false);
        this.StartExtraction();
    }

    public override void OnClose()
    {
        this.SaveIfDirty(force: true);
    }

    /// <summary>点开时总是展开（折叠状态会被 ImGui 的 ini 记住，2026-10-02 实测）。</summary>
    public override void OnOpen()
    {
        ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
    }

    public override void Draw()
    {
        // ImGuiFileDialog 要求每帧画一次，否则弹不出来
        this.fileDialog.Draw();

        if (this.entry is null)
        {
            ImGui.TextDisabled("还没有选插件。");
            return;
        }

        this.PollExtraction();
        this.PollTranslate();
        this.PollPatchTask();
        this.DrawHeader();
        ImGui.Separator();
        this.DrawToolbar();
        ImGui.Separator();
        this.DrawTable();
        this.SaveIfDirty(force: false);
    }

    // ── 抽取 ─────────────────────────────────────────────────────────────

    private void StartExtraction()
    {
        if (this.entry is null)
        {
            this.SetStatus("还没有选插件。", true);
            return;
        }

        var path = this.entry.DLLPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            this.SetStatus("读不到插件主程序集路径，没法抽取。", true);
            return;
        }

        if (this.extractionTask is { IsCompleted: false })
        {
            return;
        }

        // 盘上是我们自己的补丁时一定要改读原始备份：对着补丁后的 DLL 抽到的是「译文###原文」，
        // 会把包里好好的条目当成「原文没了」整批清掉。
        var sources = this.patches.ExtractionSourceOf(this.entry, out var note);
        this.extractionNote = note;
        this.extractionTask = Task.Run(() => UIStringExtractor.ExtractMany(sources));
    }

    private void PollExtraction()
    {
        var task = this.extractionTask;
        if (task is null || !task.IsCompleted)
        {
            return;
        }

        this.extractionTask = null;
        UITextExtraction result;
        try
        {
            result = task.Result;
        }
        catch (Exception e)
        {
            this.SetStatus("抽取失败：" + e.Message, true);
            return;
        }

        if (result.Error is not null)
        {
            this.SetStatus("抽取失败（插件可能加壳/加密）：" + result.Error, true);
            return;
        }

        this.extraction = result;
        this.roles.Clear();
        var preserve = new Dictionary<string, bool>(StringComparer.Ordinal);
        var uiCount = 0;
        var ambiguous = 0;
        foreach (var item in result.Entries)
        {
            if (item.Role == UITextRole.Excluded)
            {
                continue;
            }

            // 同一原文出现在多处时，只要有「候选」就算候选
            if (!this.roles.TryGetValue(item.Original, out var existing) || (existing.Role == UITextRole.Ambiguous && item.Role == UITextRole.UI))
            {
                this.roles[item.Original] = (item.Role, item.Reason);
            }

            preserve[item.Original] = preserve.GetValueOrDefault(item.Original) || item.PreserveID;
            var packEntry = this.pack.GetOrAdd(item.Original, item.Context, item.PreserveID);
            if (packEntry.Context is null)
            {
                packEntry.Context = item.Context;
            }

            if (item.Role == UITextRole.UI)
            {
                uiCount++;
            }
            else
            {
                ambiguous++;
            }
        }

        // PreserveID 是抽取的推导值，重新抽取要以这一轮为准。不能只 |= ——
        // 以前判错过就永远换不掉（2026-10-01 Orbwalker 的 Movement 就被困在「移动###Movement」里）。
        foreach (var (original, keepID) in preserve)
        {
            var entry = this.pack.Find(original);
            if (entry is not null)
            {
                entry.PreserveID = keepID;
            }
        }

        // 清账：这一轮没被列为 候选/灰名单 的条目（键名、功能串、过期条目）标成「不翻」，不再打进补丁；
        // 又变回候选的自动恢复；已是纯空壳的（没译文也没来源）直接清掉。
        var prune = this.pack.PruneAgainstExtraction(result);
        this.SortEntries();
        this.RebuildRows();
        this.MarkDirty();
        var pieces = new List<string>(3);
        if (this.extractionNote.Length > 0)
        {
            pieces.Add(this.extractionNote);
        }

        if (prune.Any)
        {
            pieces.Add(prune.Describe() + "（键名/功能串、版本变了、或上一轮抽错）");
        }

        this.SetStatus(
            $"抽取完成：候选 {uiCount} 条 · 灰名单 {ambiguous} 条（灰名单默认不翻）"
            + (pieces.Count > 0 ? " · " + string.Join(" · ", pieces) : string.Empty),
            false);
    }

    private void SortEntries()
    {
        this.pack.Entries.Sort((a, b) =>
        {
            var ra = this.roles.TryGetValue(a.Original, out var va) ? va.Role : UITextRole.UI;
            var rb = this.roles.TryGetValue(b.Original, out var vb) ? vb.Role : UITextRole.UI;
            var rank = RoleRank(ra).CompareTo(RoleRank(rb));
            if (rank != 0)
            {
                return rank;
            }

            var ca = a.Context ?? string.Empty;
            var cb = b.Context ?? string.Empty;
            var byContext = string.Compare(ca, cb, StringComparison.Ordinal);
            return byContext != 0 ? byContext : string.Compare(a.Original, b.Original, StringComparison.Ordinal);
        });
    }

    private static int RoleRank(UITextRole role) => role switch
    {
        UITextRole.UI => 0,
        UITextRole.Ambiguous => 1,
        _ => 2,
    };

    private void RebuildRows()
    {
        this.rows = [];
        foreach (var packEntry in this.pack.Entries)
        {
            UITextRole role;
            string reason;
            if (this.roles.TryGetValue(packEntry.Original, out var info))
            {
                role = info.Role;
                reason = info.Reason;
            }
            else
            {
                role = UITextRole.UI;
                reason = string.Empty;
            }

            this.rows.Add(new Row
            {
                Entry = packEntry,
                Role = role,
                Reason = reason,
                Skipped = this.pack.IsSkipped(packEntry.Original),
            });
        }

        foreach (var resource in this.pack.Resources)
        {
            this.rows.Add(new Row
            {
                Resource = resource,
                Role = UITextRole.UI,
                Reason = "资源型本地化（.resources）：打补丁改容器里的值，不动卫星程序集",
                Skipped = this.pack.IsResourceSkipped(resource.Container, resource.Key),
            });
        }
    }

    // ── 翻译 ─────────────────────────────────────────────────────────────

    private List<UITextTarget> TranslationTargets()
    {
        var includeGrey = this.plugin.Config.UITextTranslateGreyList;
        return this.rows
            .Where(r => !r.Skipped && !r.HasTranslation)
            .Where(r => r.IsResource || r.Role == UITextRole.UI || (includeGrey && r.Role == UITextRole.Ambiguous))
            .Select(r => new UITextTarget(r.Original, r.Context, r.Entry, r.Resource))
            .ToList();
    }

    private void StartTranslate(List<UITextTarget> targets)
    {
        if (this.translateTask is { IsCompleted: false })
        {
            return;
        }

        var items = UITextFlow.BuildTranslateItems(targets);
        if (items.Count == 0)
        {
            this.SetStatus("没有需要翻译的条目。", false);
            return;
        }

        var channel = UITextChannelFactory.Create(this.plugin.Config, out var error);
        if (channel is null)
        {
            this.SetStatus(error ?? "当前翻译通道不可用。", true);
            return;
        }

        this.translateCancel = new CancellationTokenSource();
        this.translateDone = 0;
        this.translateTotal = items.Count;
        var token = this.translateCancel.Token;
        this.translateTask = Task.Run(async () =>
        {
            var result = await channel.TranslateAsync(
                items,
                (done, _) =>
                {
                    this.translateDone = done;
                },
                token).ConfigureAwait(false);
            return (channel.Name, result);
        });
        this.SetStatus($"正在用 {channel.Name} 翻译 {items.Count} 条…", false);
    }

    private void PollTranslate()
    {
        var task = this.translateTask;
        if (task is null || !task.IsCompleted)
        {
            return;
        }

        this.translateTask = null;
        this.translateCancel?.Dispose();
        this.translateCancel = null;

        (string Channel, UITextTranslateResult Result) outcome;
        try
        {
            outcome = task.Result;
        }
        catch (Exception e)
        {
            this.SetStatus("翻译失败：" + e.Message, true);
            return;
        }

        // 收译文：字面量与资源条目共用同一套清洗/占位符校验/来源标记
        var (applied, placeholderRejected, unchanged) = UITextFlow.AcceptTranslations(this.pack, outcome.Result.Translated, outcome.Channel);
        this.RebuildRows();

        this.MarkDirty();
        var failed = outcome.Result.Failed.Count;
        var summary = $"翻译完成（{outcome.Channel}）：写入 {applied} 条" +
                      (failed > 0 ? $" · 失败 {failed} 条" : string.Empty) +
                      (placeholderRejected > 0 ? $" · 占位符对不上跳过 {placeholderRejected} 条" : string.Empty) +
                      (unchanged > 0 ? $" · 原样返回 {unchanged} 条" : string.Empty);
        if (outcome.Result.Error is not null)
        {
            summary += $"（{outcome.Result.Error}）";
        }

        if (outcome.Result.Notes.Count > 0)
        {
            summary += "｜" + string.Join("｜", outcome.Result.Notes);
        }

        Plugin.Log?.Information(
            $"[内部文本] {this.entry?.InternalName} 翻译通道 {outcome.Channel}：目标 {this.translateTotal}，写入 {applied}，失败 {failed}" +
            (outcome.Result.Notes.Count > 0 ? "；" + string.Join("；", outcome.Result.Notes) : string.Empty));

        var unapplied = this.rows.Count(r => r.HasTranslation && !r.Skipped);
        if (applied > 0 && unapplied > 0)
        {
            summary += $" · 尚未应用——点「写入并重载」（共 {unapplied} 条）";
        }

        this.SetStatus(summary, applied == 0 && failed > 0);
    }

    /// <summary>
    ///     写入插件 + 自动重载（一步完成；重载不单独给按钮）。
    /// </summary>
    private void StartApply()
    {
        if (this.entry is null || this.patchTask is { IsCompleted: false })
        {
            return;
        }

        this.SaveIfDirty(force: true);
        var target = this.entry;
        this.SetStatus("正在写入补丁…（写入后会自动重载插件）", false);
        this.patchTask = Task.Run(() => this.patches.ApplyAndReloadAsync(target));
    }

    /// <summary>
    ///     还原原始 DLL + 自动重载。
    /// </summary>
    private void StartRestore()
    {
        if (this.entry is null || this.patchTask is { IsCompleted: false })
        {
            return;
        }

        this.SaveIfDirty(force: true);
        var target = this.entry;
        this.SetStatus("正在还原原始文件…（还原后会自动重载插件）", false);
        this.patchTask = Task.Run(() => this.patches.RestoreAndReloadAsync(target));
    }

    private void PollPatchTask()
    {
        var task = this.patchTask;
        if (task is null || !task.IsCompleted)
        {
            return;
        }

        this.patchTask = null;
        try
        {
            var (ok, message) = task.Result;
            this.SetStatus(message, !ok);
        }
        catch (Exception e)
        {
            this.SetStatus("重载失败：" + e.Message, true);
        }
    }

    private static string DescribePatchStatus(UITextPatchStatus status) => status switch
    {
        UITextPatchStatus.Applied => "已打补丁",
        UITextPatchStatus.PendingReload => "已打补丁，待重载确认",
        UITextPatchStatus.NeedsRepatch => "插件更新过，需重打",
        UITextPatchStatus.Failed => "失败（已还原）",
        _ => "未打补丁",
    };

    // ── 界面 ─────────────────────────────────────────────────────────────

    private void DrawHeader()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Accent);
        ImGui.TextUnformatted(this.entry!.DisplayName);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        var sameName = string.Equals(this.entry.DisplayName, this.entry.InternalName, StringComparison.Ordinal);
        var version = string.IsNullOrEmpty(this.entry.Version) ? string.Empty : "v" + this.entry.Version;
        var suffix = sameName
            ? version
            : this.entry.InternalName + (version.Length > 0 ? " · " + version : string.Empty);
        ImGui.TextDisabled($"({suffix})");

        var candidate = this.rows.Count(r => r.Role == UITextRole.UI);
        var ambiguous = this.rows.Count(r => r.Role == UITextRole.Ambiguous);
        var resources = this.pack.Resources.Count;
        var translated = this.rows.Count(r => r.HasTranslation);
        var skipped = this.rows.Count(r => r.Skipped);
        ImGui.TextDisabled(
            $"候选 {candidate}（含资源 {resources}）· 灰名单 {ambiguous} · 已翻译 {translated} · 不翻 {skipped}" +
            (this.extractionTask is { IsCompleted: false } ? " · 抽取中…" : string.Empty));
    }

    private void DrawToolbar()
    {
        var entry = this.entry!;
        var busy = this.translateTask is { IsCompleted: false } || this.patchTask is { IsCompleted: false };
        var targets = this.TranslationTargets().Count;
        var hasBackup = this.patches.HasBackup(entry);

        // 列表页正在对这个插件跑「一键汉化」时，这里别动同一份文件（谁先拿到锁谁干活）
        var otherRun = this.runs.IsBusyWith(entry.InternalName);
        if (otherRun)
        {
            busy = true;
        }

        // 主操作：翻译未翻（翻完不自动应用——这一步是给人校对用的）
        ImGui.BeginDisabled(busy || targets == 0);
        if (ImGui.Button($"翻译未翻 ({targets})###uitranslate"))
        {
            this.StartTranslate(this.TranslationTargets());
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var hint = $"用当前通道翻 {targets} 条（未翻的候选" +
                       (this.plugin.Config.UITextTranslateGreyList ? " + 灰名单" : "，灰名单不翻") + "）。" +
                       "\n翻完不会自动写入插件——想先核对就留在这里改，想直接生效点「写入并重载」。";
            if (targets > 100 && this.plugin.Config.UITextChannel is "auto" or "google" or "mymemory")
            {
                hint += "\n注意：这两条免 key 通道按 IP 限流（Google 会 429、MyMemory 额度只有几千字符/天），而且是一条一条翻。" +
                        "\n上百条建议改用「彩云小译」（免费额度、一次 50 条）或「大模型（自填 key）」。";
            }

            ImGui.SetTooltip(hint);
        }

        if (this.translateTask is { IsCompleted: false })
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"{this.translateDone} / {this.translateTotal}");
            ImGui.SameLine();
            if (ImGui.Button("取消翻译"))
            {
                this.translateCancel?.Cancel();
            }
        }

        // 写入插件 + 自动重载（重载是合并步骤，不单独给按钮）
        ImGui.SameLine();
        ImGui.BeginDisabled(busy || this.rows.Count(r => r.HasTranslation && !r.Skipped) == 0);
        if (ImGui.Button("写入并重载"))
        {
            this.StartApply();
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("把有译文的条目写进插件 DLL，然后自动重载插件——界面即刻变中文。" +
                             "\n原始文件会先备份，随时可以「还原原文」。");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(busy || !hasBackup);
        if (ImGui.Button("还原原文"))
        {
            this.StartRestore();
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(hasBackup
                ? "把插件 DLL 还原成打补丁之前的原始文件，然后自动重载插件（界面恢复英文）。"
                : "还没有打过补丁，没有可还原的文件。");
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("更多…###uitrans-more"))
        {
            ImGui.OpenPopup("###uitrans-more-popup");
        }

        ImGui.EndDisabled();
        if (ImGui.BeginPopup("###uitrans-more-popup"))
        {
            if (ImGui.MenuItem("重新抽取", enabled: !busy))
            {
                this.StartExtraction();
                this.SetStatus("正在重新抽取…", false);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("插件更新过、或想拾取新出现的文本时点它；会以原始文件为准重算候选。");
            }

            ImGui.Separator();
            if (ImGui.MenuItem("导出未翻（给 AI）"))
            {
                this.ExportForAI();
            }

            if (ImGui.MenuItem("导入译文…"))
            {
                this.ImportFromFile();
            }

            ImGui.Separator();
            var hasHuman = this.pack.Entries.Any(e => e.IsUserSource && e.HasTranslation)
                           || this.pack.Resources.Any(r => r.IsUserSource && r.HasTranslation);
            if (ImGui.MenuItem("提交人工译文到公共库…", enabled: hasHuman))
            {
                this.SubmitContributions();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("把你手工改过的译文整理成一份贡献，打开 GitHub 提交页（不带任何账号信息）。\n提交后随每周更新进入公共译文库。");
            }

            ImGui.Separator();
            if (ImGui.MenuItem("打开包目录"))
            {
                this.OpenPackDirectory();
            }

            ImGui.EndPopup();
        }

        if (this.patchTask is { IsCompleted: false })
        {
            ImGui.SameLine();
            ImGui.TextDisabled("处理中…");
        }

        // 右上：通道 + 保存状态 + 补丁状态
        var patchStatus = this.patches.StatusOf(entry, out var patchDetailText);
        ImGui.SameLine();
        var right = $"通道：{UITextChannelFactory.Describe(this.plugin.Config)} · " +
                    (this.dirty ? "有未保存的改动…" : $"已保存 {this.lastSaveAt:HH:mm:ss}") +
                    " · " + DescribePatchStatus(patchStatus);
        ImGui.TextDisabled(right);
        if (patchDetailText.Length > 0 && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(patchDetailText);
        }

        if (this.status.Length > 0)
        {
            UiHelpers.ColoredWrapped(this.statusIsError ? UiHelpers.Bad : UiHelpers.Muted, this.status);
        }
    }

    /// <summary>
    ///     打开包目录（找不到就提示，不抛）。
    /// </summary>
    private void OpenPackDirectory()
    {
        try
        {
            Directory.CreateDirectory(this.store.DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = this.store.DirectoryPath,
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            this.SetStatus("打不开包目录：" + e.Message, true);
        }
    }

    private void DrawTable()
    {
        this.DrawFilters();

        var filterText = this.search.Trim();
        this.visible.Clear();
        foreach (var row in this.rows)
        {
            if (!this.Matches(row, filterText))
            {
                continue;
            }

            this.visible.Add(row);
        }

        ImGui.TextDisabled($"显示 {this.visible.Count} / 共 {this.rows.Count} 条");

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                                      ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("###UITextRows", 4, flags, new System.Numerics.Vector2(0, -1)))
        {
            return;
        }

        ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 54);
        ImGui.TableSetupColumn("上下文", ImGuiTableColumnFlags.WidthFixed, 180);
        ImGui.TableSetupColumn("原文", ImGuiTableColumnFlags.WidthStretch, 0.45f);
        ImGui.TableSetupColumn("译文", ImGuiTableColumnFlags.WidthStretch, 0.55f);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var index = 0;
        foreach (var row in this.visible)
        {
            index++;
            ImGui.PushID(index);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            this.DrawStatusCell(row);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(row.Context ?? "—");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Shorten(row.Original, 60));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(row.Original);
            }

            this.DrawRowContextMenu(row);

            ImGui.TableNextColumn();
            this.DrawTranslationCell(row);

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawStatusCell(Row row)
    {
        if (row.Skipped)
        {
            UiHelpers.ColoredText(UiHelpers.Warn, "⛔");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("已标记「不翻」");
            }

            return;
        }

        if (row.Role == UITextRole.Ambiguous)
        {
            UiHelpers.ColoredText(UiHelpers.Info, "?");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("灰名单：既进 UI 又进功能语境，默认不翻。\n" + row.Reason);
            }

            return;
        }

        if (row.Review is { Length: > 0 } review)
        {
            UiHelpers.ColoredText(UiHelpers.Warn, "⚠");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(review);
            }

            return;
        }

        if (!row.HasTranslation)
        {
            ImGui.TextDisabled("·");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("还没翻译");
            }
        }
        else if (row.IsUserSource)
        {
            UiHelpers.ColoredText(UiHelpers.Good, "✔");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("人工译文（不会被机器翻译覆盖）");
            }
        }
        else
        {
            UiHelpers.ColoredText(UiHelpers.Muted, "⚙");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("机器译文（" + (row.Source ?? "ai") + "），可以改");
            }
        }
    }

    private void DrawTranslationCell(Row row)
    {
        var value = row.Translated;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##translated", ref value, 2048))
        {
            this.SetTranslationValue(row, value);
            this.MarkDirty();
        }

        if (ImGui.IsItemHovered() && ImGui.IsItemActive() is false && value.Length > 0)
        {
            ImGui.SetTooltip(value);
        }
    }

    /// <summary>写一行的译文（字面量 / 资源两种行都走这里，来源统一标 user）。</summary>
    private void SetTranslationValue(Row row, string value)
    {
        if (row.Resource is not null)
        {
            row.Resource.Translated = value;
            row.Resource.Source = value.Length == 0 ? null : "user";
            row.Resource.Review = null;
            return;
        }

        row.Entry!.Translated = value;
        row.Entry.Source = value.Length == 0 ? null : "user";
        row.Entry.Review = null;
    }

    /// <summary>清一行的译文。</summary>
    private static void ClearTranslationValue(Row row)
    {
        if (row.Resource is not null)
        {
            row.Resource.Translated = string.Empty;
            row.Resource.Source = null;
            return;
        }

        row.Entry!.Translated = string.Empty;
        row.Entry.Source = null;
    }

    /// <summary>标/取消「不翻」。</summary>
    private void SetRowSkipped(Row row, bool skipped)
    {
        if (row.Resource is not null)
        {
            if (skipped)
            {
                this.pack.MarkResourceSkipped(row.Resource.Container, row.Resource.Key);
            }
            else
            {
                this.pack.UnmarkResourceSkipped(row.Resource.Container, row.Resource.Key);
            }

            row.Resource.Review = null;
        }
        else
        {
            if (skipped)
            {
                this.pack.MarkSkipped(row.Entry!.Original);
            }
            else
            {
                this.pack.UnmarkSkipped(row.Entry!.Original);
            }

            row.Entry!.Review = null;
        }

        row.Skipped = skipped;
    }

    private void DrawRowContextMenu(Row row)
    {
        if (!ImGui.BeginPopupContextItem("###rowctx"))
        {
            return;
        }

        if (ImGui.MenuItem("翻译这一条", enabled: !(this.translateTask is { IsCompleted: false })))
        {
            this.StartTranslate([new UITextTarget(row.Original, row.Context, row.Entry, row.Resource)]);
        }

        if (row.Skipped)
        {
            if (ImGui.MenuItem("取消「不翻」"))
            {
                this.SetRowSkipped(row, false);
                this.MarkDirty();
            }
        }
        else if (ImGui.MenuItem("标记「不翻」"))
        {
            this.SetRowSkipped(row, true);
            this.MarkDirty();
        }

        if (ImGui.MenuItem("清除译文", enabled: row.HasTranslation))
        {
            ClearTranslationValue(row);
            this.MarkDirty();
        }

        if (ImGui.MenuItem("复制原文"))
        {
            ImGui.SetClipboardText(row.Original);
        }

        if (ImGui.MenuItem("复制译文", enabled: row.HasTranslation))
        {
            ImGui.SetClipboardText(row.Translated);
        }

        ImGui.EndPopup();
    }

    private void DrawFilters()
    {
        // 筛选标签带计数：一眼看出每一栏有多少条（盲评 F19）
        var counts = new Dictionary<Filter, int>
        {
            [Filter.Candidates] = this.rows.Count(r => r.Role == UITextRole.UI),
            [Filter.Ambiguous] = this.rows.Count(r => r.Role == UITextRole.Ambiguous),
            [Filter.Untranslated] = this.rows.Count(r => !r.HasTranslation && !r.Skipped),
            [Filter.Translated] = this.rows.Count(r => r.HasTranslation),
            [Filter.Skipped] = this.rows.Count(r => r.Skipped),
            [Filter.All] = this.rows.Count,
        };

        var filters = new (Filter Filter, string Label)[]
        {
            (Filter.Candidates, "候选"),
            (Filter.Ambiguous, "灰名单"),
            (Filter.Untranslated, "未翻译"),
            (Filter.Translated, "已翻译"),
            (Filter.Skipped, "不翻"),
            (Filter.All, "全部"),
        };

        for (var i = 0; i < filters.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
            }

            var selected = this.filter == filters[i].Filter;
            if (selected)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, UiHelpers.Accent);
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.05f, 0.08f, 0.12f, 1f));
            }

            if (ImGui.SmallButton($"{filters[i].Label} {counts[filters[i].Filter]}") && !selected)
            {
                this.filter = filters[i].Filter;
            }

            if (selected)
            {
                ImGui.PopStyleColor(2);
            }

            if (filters[i].Filter == Filter.Ambiguous && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("灰名单 = 既画在界面上、又被拿去做比较 / 当键名的字符串，默认不翻（翻错可能影响功能）。");
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("###UITextSearch", "搜索原文 / 译文 / 上下文…", ref this.search, 256);

        ImGui.TextDisabled("状态列：✔ 已翻译 ｜ ⚠ 待复核（悬停看原因）｜「·」未翻译 ｜ 编辑完点「写入并重载」");
    }

    private bool Matches(Row row, string filterText)
    {
        bool byFilter = this.filter switch
        {
            Filter.Candidates => row.Role == UITextRole.UI,
            Filter.Ambiguous => row.Role == UITextRole.Ambiguous,
            Filter.Untranslated => !row.HasTranslation && !row.Skipped,
            Filter.Translated => row.HasTranslation,
            Filter.Skipped => row.Skipped,
            _ => true,
        };
        if (!byFilter)
        {
            return false;
        }

        if (filterText.Length == 0)
        {
            return true;
        }

        return row.Original.Contains(filterText, StringComparison.OrdinalIgnoreCase)
               || row.Translated.Contains(filterText, StringComparison.OrdinalIgnoreCase)
               || (row.Context ?? string.Empty).Contains(filterText, StringComparison.OrdinalIgnoreCase);
    }

    // ── 导入导出 / 保存 ───────────────────────────────────────────────────

    private void ExportForAI()
    {
        var suggested = this.entry!.InternalName + "-未翻-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".json";
        this.fileDialog.SaveFileDialog(
            "导出未翻文本（桌面翻译工具 / AI 都能直接吃）",
            ".json",
            suggested,
            ".json",
            (ok, path) =>
            {
                if (!ok)
                {
                    return;
                }

                try
                {
                    File.WriteAllText(path, UITextStore.BuildAIExport(BuildExportEntries(), onlyUntranslated: true), new System.Text.UTF8Encoding(false));
                    this.SetStatus($"已导出未翻条目：{path}", false);
                }
                catch (Exception e)
                {
                    this.SetStatus("导出失败：" + e.Message, true);
                }
            });
    }

    /// <summary>
    ///     导出/导入用的扁平列表：字面量 + 资源条目。资源用「资源：容器 · key」当上下文，
    ///     同一英文值只出现一次（多个 key 共用一条导出，对得上任何一边都能写回）。
    /// </summary>
    private List<UITextPackEntry> BuildExportEntries()
    {
        var list = new List<UITextPackEntry>(this.pack.Entries);
        var seen = new HashSet<string>(this.pack.Entries.Select(e => e.Original), StringComparer.Ordinal);
        foreach (var resource in this.pack.Resources)
        {
            if (!seen.Add(resource.Original))
            {
                continue;
            }

            list.Add(new UITextPackEntry
            {
                Original = resource.Original,
                Translated = resource.Translated,
                Context = "资源：" + resource.Container + " · " + resource.Key,
                Source = resource.Source,
            });
        }

        return list;
    }

    private void ImportFromFile()
    {
        this.fileDialog.OpenFileDialog(
            "导入译文（桌面翻译工具的 JSON 格式）",
            ".json",
            (ok, path) =>
            {
                if (!ok)
                {
                    return;
                }

                try
                {
                    var entries = UITextStore.ParseAIExport(File.ReadAllText(path), out var error);
                    if (error is not null)
                    {
                        this.SetStatus("导入失败：" + error, true);
                        return;
                    }

                    var applied = 0;
                    var unknown = 0;
                    foreach (var incoming in entries)
                    {
                        var existing = this.pack.Find(incoming.Original);
                        var appliedOne = false;
                        if (existing is not null)
                        {
                            if (!existing.IsUserSource || existing.Translated.Length == 0)
                            {
                                existing.Translated = incoming.Translated;
                                existing.Source = incoming.Source ?? "user";
                                existing.Review = null;
                                appliedOne = true;
                            }
                        }

                        // 资源条目按原文匹配（同一值可能落在多个 key 下）
                        foreach (var resource in this.pack.Resources)
                        {
                            if (!string.Equals(resource.Original, incoming.Original, StringComparison.Ordinal)
                                || this.pack.IsResourceSkipped(resource.Container, resource.Key))
                            {
                                continue;
                            }

                            if (resource.IsUserSource && resource.Translated.Length > 0)
                            {
                                continue;
                            }

                            resource.Translated = incoming.Translated;
                            resource.Source = incoming.Source ?? "user";
                            resource.Review = null;
                            appliedOne = true;
                        }

                        if (appliedOne)
                        {
                            applied++;
                        }
                        else
                        {
                            unknown++;
                        }
                    }

                    this.RebuildRows();
                    this.MarkDirty();
                    this.SetStatus($"已导入 {applied} 条译文" + (unknown > 0 ? $"（{unknown} 条原文对不上，已忽略）" : string.Empty), false);
                }
                catch (Exception e)
                {
                    this.SetStatus("导入失败：" + e.Message, true);
                }
            });
    }

    /// <summary>
    ///     把「人工改过的译文」整理成一份贡献，打开 GitHub 新建 issue 页（匿名，不带账号信息）。
    ///     与「参与翻译」的简介投稿同一套：玩家在网页上按 Submit；内容太大时先导出 JSON 让玩家拖附件。
    /// </summary>
    private void SubmitContributions()
    {
        if (this.entry is null)
        {
            return;
        }

        this.SaveIfDirty(force: true);

        var entries = this.pack.Entries
            .Where(e => e.IsUserSource && e.HasTranslation)
            .Select(e => new { e.Original, e.Translated, Context = e.Context ?? string.Empty })
            .ToList();
        var resources = this.pack.Resources
            .Where(r => r.IsUserSource && r.HasTranslation)
            .Select(r => new { r.Container, r.Key, r.Original, r.Translated })
            .ToList();
        var total = entries.Count + resources.Count;
        if (total == 0)
        {
            this.SetStatus("还没有人工译文可提交：先在列表里改几条（改过的会标成人工）再来。", false);
            return;
        }

        var payload = JsonSerializer.Serialize(
            new { type = "uit-contribution", plugin = this.entry.InternalName, entries, resources },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        var header = $"### FireGaze 插件界面文字译文贡献\n\n- 插件：`{this.entry.InternalName}`\n- 条数：{total}\n\n";
        var body = header + "```json\n" + payload + "\n```\n";
        var title = $"[译文贡献] {this.entry.InternalName} · {total} 条";
        var url = $"{ContributionsStore.RepoURL}/issues/new"
                  + $"?title={Uri.EscapeDataString(title)}"
                  + $"&body={Uri.EscapeDataString(body)}";

        if (body.Length > 6000 || url.Length > 20000)
        {
            // 太大：完整 JSON 落盘，issue 里只放摘要（玩家把它拖进附件）
            var path = Path.Combine(this.store.DirectoryPath, $"{this.entry.InternalName}-贡献-{DateTime.Now:yyyyMMdd-HHmm}.json");
            try
            {
                Directory.CreateDirectory(this.store.DirectoryPath);
                File.WriteAllText(path, payload, new System.Text.UTF8Encoding(false));
            }
            catch (Exception e)
            {
                this.SetStatus("导出贡献 JSON 失败：" + e.Message, true);
                return;
            }

            body = header + $"- 条数较多，完整 JSON 已导出到：`{path}`\n\n请在网页上把它拖进附件后提交。\n";
            url = $"{ContributionsStore.RepoURL}/issues/new"
                  + $"?title={Uri.EscapeDataString(title)}"
                  + $"&body={Uri.EscapeDataString(body)}";
            this.SetStatus($"已在浏览器打开提交页；完整 JSON 在 {path}，拖进附件再 Submit。", false);
        }
        else
        {
            this.SetStatus("已在浏览器打开 GitHub 提交页：按绿色 Submit 即可（不带账号信息）。", false);
        }

        try
        {
            Dalamud.Utility.Util.OpenLink(url);
        }
        catch (Exception e)
        {
            this.SetStatus("打开浏览器失败：" + e.Message, true);
        }
    }

    private void MarkDirty()
    {
        if (!this.dirty)
        {
            this.dirty = true;
            this.dirtySince = DateTime.Now;
        }
    }

    private void SaveIfDirty(bool force)
    {
        if (!this.dirty)
        {
            return;
        }

        if (!force && (DateTime.Now - this.dirtySince).TotalSeconds < 1.5)
        {
            return;
        }

        if (this.entry is null)
        {
            return;
        }

        if (this.store.Save(this.entry.InternalName, this.pack, out var error))
        {
            this.dirty = false;
            this.lastSaveAt = DateTime.Now;
        }
        else
        {
            this.SetStatus("保存失败：" + error, true);
        }
    }

    private void SetStatus(string message, bool isError)
    {
        this.status = message;
        this.statusIsError = isError;
    }

    private static string Shorten(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        return text[..(max - 1)] + "…";
    }
}
