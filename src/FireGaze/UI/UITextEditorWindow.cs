using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using FireGaze.Internal;
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
        public UITextAttributeEntry? Attribute;
        public UITextRole Role;
        public string Reason = string.Empty;
        public bool Skipped;

        public bool IsResource => this.Resource is not null;

        public bool IsAttribute => this.Attribute is not null;

        public string Original => this.Resource is not null ? this.Resource.Original
            : this.Attribute is not null ? this.Attribute.Original
            : this.Entry!.Original;

        public string? Context => this.Resource is not null
            ? UITextLocalizationFiles.IsFileContainer(this.Resource.Container)
                ? "文件：" + UITextLocalizationFiles.RelativeOf(this.Resource.Container) + " · " + this.Resource.Key
                : UITextJSONResources.IsJSONContainer(this.Resource.Container)
                    ? "内嵌 JSON：" + UITextJSONResources.ResourceNameOf(this.Resource.Container) + " · " + this.Resource.Key
                    : "资源：" + this.Resource.Container + " · " + this.Resource.Key
            : this.Attribute is not null
                ? this.Attribute.Context
                : this.Entry!.Context;

        public bool HasTranslation => this.Resource is not null ? this.Resource.HasTranslation
            : this.Attribute is not null ? this.Attribute.HasTranslation
            : this.Entry!.HasTranslation;

        public string Translated => this.Resource is not null ? this.Resource.Translated
            : this.Attribute is not null ? this.Attribute.Translated
            : this.Entry!.Translated;

        // 注意：Review / Source 本身可以是 null，不能用 `Resource?.X ?? Entry!.X` 这种链——
        // 中间返回 null 会继续往下走到 `Entry!`，属性行/资源行就是空引用（2026-10-02 用户实测）。
        public string? Review => this.Resource is not null ? this.Resource.Review
            : this.Attribute is not null ? this.Attribute.Review
            : this.Entry!.Review;

        public string? Source => this.Resource is not null ? this.Resource.Source
            : this.Attribute is not null ? this.Attribute.Source
            : this.Entry!.Source;

        public bool IsUserSource => this.Resource is not null ? this.Resource.IsUserSource
            : this.Attribute is not null ? this.Attribute.IsUserSource
            : this.Entry!.IsUserSource;
    }

    private readonly Plugin plugin;
    private readonly UITextStore store;
    private readonly UITextPatchManager patches;
    private readonly UITextRunLock runs;
    private readonly FileDialogManager fileDialog = new();

    private InstalledPluginEntry? entry;
    private UITextPack pack = new();
    private UITextPack? editBaseline;
    private DateTime packLoadedMtime = DateTime.MinValue;
    private Task<(UITextExtraction Extraction, string Note)>? extractionTask;
    private UITextExtraction? extraction;
    private string extractionNote = string.Empty;
    private readonly Dictionary<string, (UITextRole Role, string Reason)> roles = new(StringComparer.Ordinal);
    private List<Row> rows = [];
    private readonly List<Row> visible = [];
    private string search = string.Empty;
    private Filter filter = Filter.Candidates;
    private string status = "打开后自动读取界面文字。";
    private bool statusIsError;
    private bool dirty;
    private DateTime dirtySince = DateTime.MinValue;
    private DateTime lastSaveAt = DateTime.MinValue;
    private DateTime nextDiskCheck = DateTime.MinValue;
    private DateTime lastDrawErrorAt = DateTime.MinValue;

    /// <summary>「还原原文」二次确认：第一次点只是把按钮变成「再点一次确认还原」，5 秒内不点就恢复。</summary>
    private string restoreArmKey = string.Empty;
    private DateTime restoreArmAt = DateTime.MinValue;

    // 翻译通道
    private Task<(string Channel, UITextTranslateResult Result)>? translateTask;
    private Task<(bool Ok, string Message)>? patchTask;
    private CancellationTokenSource? translateCancel;
    private object? translationGate;
    private object TranslationGate => LazyInitializer.EnsureInitialized(ref this.translationGate);
    private long editorGeneration;
    private TranslationRequest? translationRequest;
    private Dictionary<(int Kind, string Container, string Key), UITextPackEditMerge.EditIntent>? editIntents;
    private sealed record TranslationRequest(string InternalName, long EditorGeneration, long HostGeneration,
        UITextTranslationReceipt Receipt)
    {
        public int Applied;
        public int PlaceholderRejected;
        public int Unchanged;
    }
    private int translateDone;
    private int translateTotal;

    /// <summary>本次翻译是否覆盖已有译文（「全量重翻」）；默认只补空槽。</summary>
    private bool translateOverwriteExisting;

    /// <summary>「全量重翻」是否连手改过的一起覆盖；默认保护。</summary>
    private bool translateOverwriteUser;

    /// <summary>「全量重翻」确认框（默认焦点在「取消」）。</summary>
    private bool retranslateConfirmPending;
    private bool retranslateIncludeUser;

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
    ///     打开（或重新打开）某个插件的编辑器。
    /// </summary>
    /// <remarks>
    ///     同一插件且窗口本来就开着：只带到前面（窗口开着时列表页不会跑任务，不会有外部写入）。
    ///     窗口关过再开：**必须重新读盘 + 重新抽取**——关窗期间列表页的「重新抽取 / 一键汉化」
    ///     会把新条目（比如属性文本）写进包，拿旧快照会看起来“少了条目”（2026-10-02 用户实测 1324 vs 2334）。
    /// </remarks>
    public void OpenFor(InstalledPluginEntry target)
    {
        var wasOpen = this.IsOpen;
        this.IsOpen = true;
        this.BringToFront();

        if (wasOpen
            && this.entry is not null
            && string.Equals(this.entry.InternalName, target.InternalName, StringComparison.Ordinal))
        {
            return;
        }

        if (this.dirty && !this.SaveIfDirty(force: true)) return;
        lock (this.TranslationGate)
        {
            this.editorGeneration++;
            this.translationRequest = null;
            this.translateCancel?.Cancel();
            this.translateCancel?.Dispose();
            this.translateCancel = null;
            this.translateTask = null;
        }
        this.entry = target;
        this.pack = this.store.Load(target.InternalName);
        this.editBaseline = UITextPackEditMerge.Clone(this.pack);
        this.packLoadedMtime = this.PackMtime(target.InternalName);
        this.rows = [];
        this.roles.Clear();
        this.search = string.Empty;
        this.dirty = false;
        this.editIntents?.Clear();
        this.extraction = null;
        this.extractionTask = null;
        // 灰名单勾选框默认跟随全局设置；同一插件重开窗口时不重置（保留本次选择）
        this.includeGreyInTranslate = this.plugin.Config.UITextTranslateGreyList;
        this.SetStatus($"已载入 {target.DisplayName}：正在读取界面文字…", false);
        this.StartExtraction();
    }

    public override void OnClose()
    {
        lock (this.TranslationGate)
        {
            // A file-dialog callback may outlive the window. Invalidate its
            // editor identity before the close save so reopening the same
            // plugin cannot accept the old selection.
            this.editorGeneration++;
            this.translationRequest = null;
            this.translateCancel?.Cancel();
            this.translateCancel?.Dispose();
            this.translateCancel = null;
            this.translateTask = null;
        }
        if (!this.SaveIfDirty(force: true)) this.IsOpen = true;
    }

    /// <summary>点开时总是展开（折叠状态会被 ImGui 的 ini 记住，2026-10-02 实测）。</summary>
    public override void OnOpen()
    {
        ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
    }

    public override void PreDraw()
    {
        var scale = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var placement = FireGazeWindowPlacement.Prepare("FireGazeUITextEditor", new System.Numerics.Vector2(980, 640) * scale, scale, "FireGaze");
        Size = placement.Size / scale;
        SizeCondition = ImGuiCond.Appearing;
        var maximum = ImGui.GetMainViewport().WorkSize / scale;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = System.Numerics.Vector2.Min(new(640, 420), maximum), MaximumSize = maximum };
    }

    public override void Draw()
    {
        FireGazeWindowPlacement.Remember("FireGazeUITextEditor");
        try
        {
            this.DrawCore();
        }
        catch (Exception e)
        {
            // 绘制路径上任何异常都不该让整张窗口（乃至游戏）失能——行属性的空引用已实测过一次（2026-10-02）
            if ((DateTime.Now - this.lastDrawErrorAt).TotalSeconds > 5)
            {
                this.lastDrawErrorAt = DateTime.Now;
                Plugin.Log?.Error(e, "[内部文本] 编辑器窗口绘制出错");
            }

            ImGui.TextWrapped("编辑校对暂时无法显示，请重开窗口。详情：" + e.Message);
        }
    }

    private void DrawCore()
    {
        // ImGuiFileDialog 要求每帧画一次，否则弹不出来
        this.fileDialog.Draw();

        if (this.entry is null)
        {
            ImGui.TextDisabled("还没有选插件。");
            return;
        }

        // 窗口开着时外部（某个刚开始的任务刚跑完）写了包：不自带刷新就会一直显示旧行数
        if (DateTime.Now >= this.nextDiskCheck)
        {
            this.nextDiskCheck = DateTime.Now.AddSeconds(2);
            this.SyncFromDiskIfChanged();
        }

        this.PollExtraction();
        this.PollTranslate();
        this.PollPatchTask();
        this.DrawHeader();
        ImGui.Separator();
        this.DrawToolbar();
        ImGui.Separator();
        this.DrawTable();
        this.DrawRetranslateConfirm();
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
            this.SetStatus("未找到插件文件，请刷新列表后重试。", true);
            return;
        }

        if (this.extractionTask is { IsCompleted: false })
        {
            return;
        }

        // 抽取走 ExtractWithGuard：它会自动改读原始备份；万一还是抽到「译文###原文」形态，
        // 会换备份重抽或停下报错，绝不把假原文并进包（2026-10-02 防污染）。
        var target = this.entry;
        this.extractionTask = this.plugin.RunTextTask(() => this.patches.ExtractWithRecoveryAsync(target, this.plugin.WorkLifetime.ShutdownToken));
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
            var guard = task.Result;
            this.extractionNote = guard.Note;
            result = guard.Extraction;
        }
        catch (Exception e)
        {
            this.SetStatus("读取界面文字失败：" + e.Message, true);
            return;
        }

        if (result.Error is not null)
        {
            this.SetStatus("无法读取此插件的界面文字：" + result.Error, true);
            return;
        }

        this.extraction = result;

        // 并进包走 UITextFlow.MergeExtraction（与列表页「一键汉化」同一条路）：
        // 字面量、资源、属性、PreserveID、清账一次做完——编辑器自己抄一遍就会漏（资源/属性条目就曾漏过：
        // 首次在编辑器里打开一个还没跑过一键汉化的插件，本地化文件的候选根本不出现在行里）。
        var merge = UITextFlow.MergeExtraction(this.pack, result);
        this.roles.Clear();
        foreach (var (original, role) in merge.Roles)
        {
            this.roles[original] = (role, merge.Reasons.TryGetValue(original, out var reason) ? reason : string.Empty);
        }

        this.SortEntries();
        this.RebuildRows();
        this.MarkDirty();
        var pieces = new List<string>(3);
        if (this.extractionNote.Length > 0)
        {
            pieces.Add(this.extractionNote);
        }

        if (merge.Prune.Any)
        {
            pieces.Add(merge.Prune.Describe() + "（已不适合翻译的内容）");
        }

        this.SetStatus(
            $"读取完成：候选 {merge.UICount} 条 · 灰名单 {merge.AmbiguousCount} 条（灰名单默认不翻）"
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
        this.editBaseline ??= UITextPackEditMerge.Clone(this.pack);
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
                Reason = UITextLocalizationFiles.IsFileContainer(resource.Container)
                    ? "插件自带的语言文件：只补充缺少的中文，保留已有译文。"
                    : UITextJSONResources.IsJSONContainer(resource.Container)
                        ? "插件内置的语言文件：只补充缺少的中文，保留已有译文。"
                        : "插件内置的界面文字，可在此校对。",
                Skipped = this.pack.IsResourceSkipped(resource.Container, resource.Key),
            });
        }

        foreach (var attribute in this.pack.Attributes)
        {
            this.rows.Add(new Row
            {
                Attribute = attribute,
                Role = UITextRole.UI,
                Reason = "插件定义的界面文字，可在此校对。",
                Skipped = this.pack.IsAttributeSkipped(attribute.Original),
            });
        }
    }

    // ── 翻译 ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     本次编辑的「连灰名单一起翻译」开关（勾选框，2026-10-03 用户要求：不用长按钮）。
    ///     <see cref="OpenFor" /> 时从翻译设置里的全局开关初始化；这里改动只影响本次编辑。
    /// </summary>
    private bool includeGreyInTranslate;

    private List<UITextTarget> TranslationTargets(bool? includeGrey = null)
    {
        var grey = includeGrey ?? this.includeGreyInTranslate;
        return this.rows
            .Where(r => !r.Skipped && !r.HasTranslation && IsTranslatableRow(r, grey))
            .Select(r => new UITextTarget(r.Original, r.Context, r.Entry, r.Resource, r.Attribute))
            .ToList();
    }

    /// <summary>行是不是默认的翻译候选（UI / 资源 / 属性；灰名单按开关）。</summary>
    private static bool IsTranslatableRow(Row row, bool grey) =>
        row.IsResource || row.IsAttribute || row.Role == UITextRole.UI || (grey && row.Role == UITextRole.Ambiguous);

    /// <summary>
    ///     「全量重翻」的目标：把已经有译文的也算进来（下载到质量不行的公共库 / 玩家包时，
    ///     用自己通道的译文把它们整个换掉）。<paramref name="includeUser" /> = 连手动改过的也一起覆盖。
    /// </summary>
    private List<UITextTarget> RetranslateTargets(bool includeUser)
    {
        var grey = this.includeGreyInTranslate;
        return this.rows
            .Where(r => !r.Skipped && (includeUser || !r.IsUserSource) && IsTranslatableRow(r, grey))
            .Select(r => new UITextTarget(r.Original, r.Context, r.Entry, r.Resource, r.Attribute))
            .ToList();
    }

    /// <summary>全量重翻会覆盖多少条（工具栏计数用；不建列表，避免每帧白分配）。</summary>
    private int RetranslateTargetCount(bool includeUser)
    {
        var grey = this.includeGreyInTranslate;
        return this.rows.Count(r => !r.Skipped && (includeUser || !r.IsUserSource) && IsTranslatableRow(r, grey));
    }

    private int? glossaryRepairCache;

    /// <summary>
    ///     术语表命中、但当前机器译文里没有该术语译名的条目（只挑这些重译；
    ///     玩家手改过的（user）永远不动）。术语表没就绪就返回 0。
    /// </summary>
    private int GlossaryRepairCount()
    {
        if (!FFXIVGlossary.Ready)
        {
            return 0;
        }

        return this.glossaryRepairCache ??= this.GlossaryRepairTargets().Count;
    }

    private List<UITextTarget> GlossaryRepairTargets()
    {
        var list = new List<UITextTarget>();
        if (!FFXIVGlossary.Ready)
        {
            return list;
        }

        foreach (var row in this.rows)
        {
            if (row.Skipped || !row.HasTranslation || row.Entry is null || row.Entry.IsUserSource)
            {
                continue;
            }

            if (FFXIVGlossary.NeedsGlossaryRepair(row.Original, row.Entry.Translated))
            {
                list.Add(new UITextTarget(row.Original, row.Context, row.Entry, null, null));
            }
        }

        return list;
    }

    private void StartGlossaryRepair()
    {
        var repairTargets = this.GlossaryRepairTargets();
        this.StartTranslate(repairTargets, overwriteExisting: true);
    }

    private void StartTranslateRow(Row row) =>
        this.StartTranslate([new UITextTarget(row.Original, row.Context, row.Entry, row.Resource, row.Attribute)],
            overwriteExisting: true, overwriteUser: true);

    public void OpenForText(InstalledPluginEntry target, string original)
    {
        this.OpenFor(target);
        this.search = original;
        this.filter = Filter.All;
    }

    private void StartTranslate(List<UITextTarget> targets, bool overwriteExisting = false, bool overwriteUser = false)
    {
        if (this.entry is null || this.translateTask is { IsCompleted: false })
        {
            return;
        }

        // Persist pending human edits first, then resolve the originally selected
        // identities against the latest merged pack. Never widen by original text.
        if (!this.SaveIfDirty(force: true)) return;
        targets = targets.Select(target => target.Entry is not null
                ? this.pack.Find(target.Original) is { } literal ? target with { Entry = literal } : null
                : target.Resource is { } resource
                    ? this.pack.FindResource(resource.Container, resource.Key) is { } row && row.Original == target.Original
                        ? target with { Resource = row } : null
                    : this.pack.FindAttribute(target.Original) is { } attribute ? target with { Attribute = attribute } : null)
            .Where(target => target is not null).Select(target => target!).ToList();
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

        this.translateOverwriteExisting = overwriteExisting;
        this.translateOverwriteUser = overwriteUser;
        this.translateCancel?.Dispose();
        this.translateCancel = CancellationTokenSource.CreateLinkedTokenSource(this.plugin.WorkLifetime.ShutdownToken);
        this.translateDone = 0;
        this.translateTotal = items.Count;
        var token = this.translateCancel.Token;
        var request = new TranslationRequest(this.entry.InternalName, this.editorGeneration,
            this.plugin.WorkLifetime.Generation, new UITextTranslationReceipt(this.pack, targets, overwriteExisting, overwriteUser));
        this.translationRequest = request;
        this.translateTask = this.plugin.RunTextTask(async () =>
        {
            var result = await UITextTranslationSession.TranslateAsync(channel,
                items,
                (done, _) =>
                {
                    lock (this.TranslationGate)
                        if (this.IsCurrentTranslation(request)) this.translateDone = done;
                },
                batch =>
                {
                    lock (this.TranslationGate)
                    {
                        if (token.IsCancellationRequested || !this.IsCurrentTranslation(request)) return;
                        if (!this.plugin.WorkLifetime.TryAcquire(out var lease, out _)) return;
                        using (lease)
                            lease!.TryExecute(() =>
                            {
                                UITextTranslationReceipt.Result? accepted = null;
                                if (!this.store.SaveMerged(request.InternalName,
                                    latest => (accepted = request.Receipt.Accept(latest, batch.Translated, channel.Name)).Pack,
                                    out _, out var checkpointError))
                                    throw new IOException("保存译文失败：" + checkpointError);
                                request.Applied += accepted!.Applied;
                                request.PlaceholderRejected += accepted.PlaceholderRejected;
                                request.Unchanged += accepted.Unchanged;
                            });
                    }
                },
                token).ConfigureAwait(false);
            return (channel.Name, result);
        });
        this.SetStatus($"正在用 {channel.Name} 翻译 {items.Count} 条{(overwriteExisting ? "（全量重翻）" : string.Empty)}…", false);
    }

    private void PollTranslate()
    {
        if (!this.plugin.WorkLifetime.TryAcquire(out var lease, out _)) return;
        using (lease)
            lease!.TryExecute(() =>
            {
                lock (this.TranslationGate) this.PollTranslateCore();
            });
    }

    private bool IsCurrentTranslation(TranslationRequest request) =>
        ReferenceEquals(this.translationRequest, request)
        && this.editorGeneration == request.EditorGeneration
        && string.Equals(this.entry?.InternalName, request.InternalName, StringComparison.Ordinal)
        && this.plugin.WorkLifetime.Generation == request.HostGeneration
        && this.plugin.WorkLifetime.State == FireGaze.Lifecycle.PluginWorkLifetimeState.Running;

    private void PollTranslateCore()
    {
        var task = this.translateTask;
        var request = this.translationRequest;
        if (task is null || !task.IsCompleted || request is null || !this.IsCurrentTranslation(request))
        {
            return;
        }

        (string Channel, UITextTranslateResult Result) outcome;
        try
        {
            outcome = task.Result;
        }
        catch (Exception e)
        {
            this.translateTask = null;
            this.translateCancel?.Dispose();
            this.translateCancel = null;
            this.translationRequest = null;
            this.SetStatus("翻译失败：" + e.Message, true);
            return;
        }

        // 收译文：字面量与资源条目共用同一套清洗/占位符校验/来源标记
        UITextPack latest;
        try { latest = this.store.LoadRequired(request.InternalName); }
        catch (Exception error)
        {
            this.SetStatus("读取最新译文失败：" + error.Message, true);
            return;
        }
        UITextPack current;
        try
        {
            current = UITextPackEditMerge.Merge(this.editBaseline ?? latest, this.pack, latest, this.editIntents?.Values);
        }
        catch (Exception error)
        {
            this.SetStatus("翻译结果无法合并：" + error.Message, true);
            return;
        }
        var accepted = request.Receipt.Accept(current, outcome.Result.Translated, outcome.Channel);
        var (applied, placeholderRejected, unchanged) = (request.Applied, request.PlaceholderRejected, request.Unchanged);
        this.pack = accepted.Pack;
        // This is the disk observation used by the edit merge, not the fixed
        // request baseline held by Receipt. Background fields are not edits.
        this.editBaseline = UITextPackEditMerge.Clone(latest);
        this.translateTask = null;
        this.translateCancel?.Dispose();
        this.translateCancel = null;
        this.translationRequest = null;
        this.RebuildRows();

        this.MarkDirty();
        var failed = outcome.Result.Failed.Count;
        var summary = (outcome.Result.Error == "已取消" ? "已停止翻译" : this.translateOverwriteExisting ? "全量重翻完成" : "翻译完成") + $"（{outcome.Channel}）：写入 {applied} 条" +
                      (failed > 0 ? $" · 失败 {failed} 条" : string.Empty) +
                      (placeholderRejected > 0 ? $" · 格式标记不匹配，跳过 {placeholderRejected} 条" : string.Empty) +
                      (unchanged > 0 ? $" · 与原文相同 {unchanged} 条" : string.Empty);
        if (outcome.Result.Error is not null)
        {
            summary += $"（{outcome.Result.Error}）";
        }

        if (outcome.Result.Notes.Count > 0)
        {
            summary += "｜" + string.Join("｜", outcome.Result.Notes);
        }

        Plugin.Log?.Information(
            $"[内部文本] {this.entry?.InternalName} 翻译通道 {outcome.Channel}{(this.translateOverwriteExisting ? "（全量重翻）" : string.Empty)}：目标 {this.translateTotal}，写入 {applied}，失败 {failed}" +
            (outcome.Result.Notes.Count > 0 ? "；" + string.Join("；", outcome.Result.Notes) : string.Empty));

        var unapplied = this.rows.Count(r => r.HasTranslation && !r.Skipped);
        if (applied > 0 && unapplied > 0)
        {
            summary += $" · 尚未应用 {unapplied} 条，请点「写入并重载」";
        }

        this.SetStatus(summary, UITextTranslationSession.IsFailure(outcome.Result, applied, placeholderRejected, unchanged));
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

        if (!this.SaveIfDirty(force: true)) return;
        var target = this.entry;
        this.SetStatus("正在写入译文并重载插件…", false);
        this.patchTask = this.plugin.RunTextTask(() => this.patches.ApplyAndReloadAsync(target));
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

        if (!this.SaveIfDirty(force: true)) return;
        var target = this.entry;
        this.SetStatus("正在还原原文并重载插件…", false);
        this.patchTask = this.plugin.RunTextTask(() => this.patches.RestoreAndReloadAsync(target));
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
        UITextPatchStatus.Applied => "已应用",
        UITextPatchStatus.PendingReload => "已写入，等待重载确认",
        UITextPatchStatus.NeedsRepatch => "插件已更新，需重新应用",
        UITextPatchStatus.Failed => "失败（已还原）",
        _ => "未应用",
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
        var attributes = this.pack.Attributes.Count;
        var translated = this.rows.Count(r => r.HasTranslation);
        var untranslated = this.rows.Count(r => !r.HasTranslation && !r.Skipped);
        var skipped = this.rows.Count(r => r.Skipped);
        var literalCandidates = Math.Max(0, candidate - resources - attributes);
        // 「｜」分开两个维度：抽取判定（候选/灰名单）与翻译状态（已翻译/未翻译/不翻）——
        // 直接把两组数字接在一起会让人试图相加（v4 复评 N6）。
        ImGui.TextDisabled(
            $"共 {this.rows.Count} 条 · 候选 {candidate} · 灰名单 {ambiguous} · 已翻译 {translated} · 未翻译 {untranslated} · 不翻 {skipped}" +
            (this.extractionTask is { IsCompleted: false } ? " · 读取中…" : string.Empty));
    }

    private void DrawToolbar()
    {
        var entry = this.entry!;
        var busy = this.translateTask is { IsCompleted: false } || this.patchTask is { IsCompleted: false };
        var targets = this.TranslationTargets().Count;
        var greyUntranslated = this.rows.Count(r => !r.HasTranslation && !r.Skipped && r.Role == UITextRole.Ambiguous);
        var hasBackup = this.patches.HasBackup(entry);

        // 列表页正在对这个插件跑「一键汉化」时，这里别动同一份文件（谁先拿到锁谁干活）
        var otherRun = this.runs.IsBusyWith(entry.InternalName);
        if (otherRun)
        {
            busy = true;
        }

        // 主操作：翻译未翻（翻完不自动应用——这一步是给人校对用的）
        using var disabledScope1 = UiHelpers.Disabled(busy || targets == 0);
        if (ImGui.Button($"翻译未翻 ({targets})###uitranslate"))
        {
            this.StartTranslate(this.TranslationTargets());
        }

        disabledScope1.Dispose();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var hint = $"用当前通道翻 {targets} 条（未翻的候选" +
                       (this.includeGreyInTranslate ? " + 灰名单" : "，灰名单不翻") + "）。" +
                       "\n翻译后可先校对，再点「写入并重载」应用。";
            if (targets > 100 && this.plugin.Config.UITextChannel is "auto" or "google" or "mymemory")
            {
                hint += "\n当前免费通道可能限流，批量翻译较慢。" +
                        "\n可在「翻译设置…」中选择其他通道。";
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
        using var disabledScope2 = UiHelpers.Disabled(busy || this.rows.Count(r => r.HasTranslation && !r.Skipped) == 0);
        if (ImGui.Button("写入并重载"))
        {
            this.StartApply();
        }

        disabledScope2.Dispose();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("将译文应用到插件，并自动重载。请先停止此插件正在执行的任务。" +
                             "\n原始文件会先备份，随时可以「还原原文」。");
        }

        ImGui.SameLine();
        var retranslateCount = this.RetranslateTargetCount(includeUser: true);
        using var disabledScope3 = UiHelpers.Disabled(busy || retranslateCount == 0);
        if (ImGui.Button($"全量重翻 ({retranslateCount})…"))
        {
            this.retranslateIncludeUser = false;
            this.retranslateConfirmPending = true;
        }

        disabledScope3.Dispose();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("使用当前通道重新翻译全部条目，覆盖范围会在开始前确认。\n" +
                             "翻译完成后需点「写入并重载」应用。");
        }

        ImGui.SameLine();
        using var disabledScope4 = UiHelpers.Disabled(busy);
        if (ImGui.Button("更多…###uitrans-more"))
        {
            ImGui.OpenPopup("###uitrans-more-popup");
        }

        disabledScope4.Dispose();
        if (ImGui.BeginPopup("###uitrans-more-popup"))
        {
            if (ImGui.MenuItem("重新读取文字", enabled: !busy))
            {
                this.StartExtraction();
                this.SetStatus("正在重新读取界面文字…", false);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("重新读取插件界面文字，保留已有译文。");
            }

            // 术语表更新后，旧的机器译不会自动升级（机器翻译不覆盖已有译文）——
            // 这里把「术语对不上」的机器译条目挑出来重翻（玩家的手改不动）。
            var glossaryRepair = this.GlossaryRepairCount();
            if (ImGui.MenuItem($"按术语表重译（{glossaryRepair} 条）", enabled: !busy && glossaryRepair > 0))
            {
                this.StartGlossaryRepair();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("按最新术语表修正不一致的机器译文，保留人工修改。");
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
            if (this.restoreArmKey.Length > 0 && (DateTime.Now - this.restoreArmAt).TotalSeconds > 5)
            {
                this.restoreArmKey = string.Empty;
            }

            var restoreArmed = string.Equals(this.restoreArmKey, entry.InternalName, StringComparison.Ordinal);
            if (ImGui.MenuItem(restoreArmed ? "再点一次确认还原" : "还原原文", enabled: !busy && hasBackup))
            {
                if (restoreArmed)
                {
                    this.restoreArmKey = string.Empty;
                    this.StartRestore();
                }
                else
                {
                    this.restoreArmKey = entry.InternalName;
                    this.restoreArmAt = DateTime.Now;
                    this.SetStatus("还原会把插件恢复成英文；5 秒内再点一次「再点一次确认还原」执行。", false);
                }
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(hasBackup
                    ? "还原插件原始文件并重载，界面恢复原文。"
                    : "没有可还原的原始文件。");
            }

            ImGui.Separator();
            if (ImGui.MenuItem("打开包目录"))
            {
                this.OpenPackDirectory();
            }

            ImGui.EndPopup();
        }

        // 连灰名单一起翻译：做成勾选框（2026-10-03 用户要求：长按钮只会让人更想按）。
        // 勾上后，主按钮「翻译未翻」会把灰名单里未翻的条目一并翻译；默认跟随翻译设置里的全局开关。
        UiHelpers.SameLineOrWrap(UiHelpers.LabelWidth("连灰名单一起翻译"), 12);
        using var disabledScope5 = UiHelpers.Disabled(busy || greyUntranslated == 0);
        ImGui.Checkbox("连灰名单一起翻译###uitranslate-grey", ref this.includeGreyInTranslate);
        var greyHovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        disabledScope5.Dispose();
        ImGui.SameLine(0, 4);
        if (greyUntranslated == 0)
        {
            ImGui.TextDisabled("（没有未翻的灰名单）");
        }

        if (greyHovered)
        {
            ImGui.SetTooltip("勾上后，点「翻译未翻」会把灰名单里还没翻的条目一起翻" +
                             (greyUntranslated > 0 ? $"（本插件还有 {greyUntranslated} 条）。」\n" : "。\n") +
                             "默认沿用「翻译设置」；此处更改仅影响本次编辑。\n" +
                             "灰名单可能影响插件功能，请逐条确认后再写入。");
        }

        if (this.patchTask is { IsCompleted: false })
        {
            ImGui.SameLine();
            ImGui.TextDisabled("处理中…");
        }

        // 右上：通道 + 保存状态 + 补丁状态。
        // 只把「有改动尚未写入」着橙（v4 复评 N4：整串变色会让橙不再是「要动手」的可靠信号）；
        // 补丁那段加「补丁：」前缀，与「本次改动」区分开（N3/F2：两段并排不再像互相矛盾）。
        var patchStatus = this.patches.StatusOf(entry, out var patchDetailText);
        var packNewer = this.patches.PackNewerThanPatch(entry);
        ImGui.SameLine();
        var stateText = this.dirty
            ? "有未保存的改动…"
            : $"已保存 {this.lastSaveAt:HH:mm:ss}";
        ImGui.TextDisabled($"通道：{UITextChannelFactory.Describe(this.plugin.Config)} · {stateText}");
        if (packNewer && !this.dirty)
        {
            ImGui.SameLine(0, 0);
            UiHelpers.ColoredText(UiHelpers.Warn, " · 有改动尚未写入（点「写入并重载」生效）");
        }

        ImGui.SameLine(0, 0);
        ImGui.TextDisabled(" · 汉化状态：" + DescribePatchStatus(patchStatus));
        if (patchDetailText.Length > 0 && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(patchDetailText);
        }

        if (this.status.Length > 0)
        {
            UiHelpers.ColoredWrapped(this.statusIsError ? UiHelpers.Bad : UiHelpers.Muted, this.status);
        }

        // 灰名单翻译的风险提醒（勾选框的下面一行；2026-10-03 用户要求常驻，避免不知道这些条目的代价）
        UiHelpers.ColoredText(UiHelpers.Warn, "灰名单可能影响插件功能，建议在列表中逐条确认后再写入。");
    }

    /// <summary>
    ///     「全量重翻」确认框：说清会覆盖多少条、其中手改过的有多少条（默认焦点在「取消」）。
    /// </summary>
    private void DrawRetranslateConfirm()
    {
        const string name = "全量重翻###uitrans-retranslate";
        if (this.retranslateConfirmPending && !ImGui.IsPopupOpen(name))
        {
            ImGui.OpenPopup(name);
        }

        ImGui.SetNextWindowSizeConstraints(new Vector2(560, 0), new Vector2(720, float.MaxValue));
        FireGazeWindowPlacement.PrepareModal(new Vector2(720, 420));
        if (!ImGui.BeginPopupModal(name, ref this.retranslateConfirmPending, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        var all = this.RetranslateTargets(includeUser: true);
        var keep = this.RetranslateTargets(includeUser: false);
        var handEdited = all.Count - keep.Count;
        ImGui.TextWrapped($"使用当前通道重新翻译 {all.Count} 条。公共译文和玩家包中的译文也在覆盖范围内。");
        ImGui.TextDisabled("翻完要点「写入并重载」才生效；原始文件会先备份。");
        if (handEdited > 0)
        {
            ImGui.Spacing();
            ImGui.Checkbox($"连手动改过的 {handEdited} 条一起覆盖", ref this.retranslateIncludeUser);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("关闭时，保留本机和玩家包中的人工译文。\n" +
                                 "开启后，这些人工译文也会重新翻译。");
            }
        }
        else
        {
            this.retranslateIncludeUser = false;
        }

        ImGui.Separator();
        if (ImGui.Button("取消", new Vector2(120, 0)))
        {
            this.retranslateConfirmPending = false;
            ImGui.CloseCurrentPopup();
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetItemDefaultFocus();
        }

        ImGui.SameLine();
        var targets = this.retranslateIncludeUser ? all : keep;
        using var disabledScope6 = UiHelpers.Disabled(targets.Count == 0 || this.translateTask is { IsCompleted: false });
        if (ImGui.Button($"开始重翻（{targets.Count} 条）", new Vector2(180, 0)))
        {
            this.retranslateConfirmPending = false;
            ImGui.CloseCurrentPopup();
            this.StartTranslate(targets, overwriteExisting: true, overwriteUser: this.retranslateIncludeUser);
        }

        disabledScope6.Dispose();
        ImGui.EndPopup();
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
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(row.Context ?? "—");
            }

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
                ImGui.SetTooltip("灰名单：可能影响插件功能，默认不翻译。\n" + row.Reason);
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
        this.RecordRowIntent(row, translation: true);
        if (row.Resource is not null)
        {
            row.Resource.Translated = value;
            row.Resource.Source = value.Length == 0 ? null : "user";
            row.Resource.Review = null;
            return;
        }

        if (row.Attribute is not null)
        {
            row.Attribute.Translated = value;
            row.Attribute.Source = value.Length == 0 ? null : "user";
            row.Attribute.Review = null;
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

        if (row.Attribute is not null)
        {
            row.Attribute.Translated = string.Empty;
            row.Attribute.Source = null;
            return;
        }

        row.Entry!.Translated = string.Empty;
        row.Entry.Source = null;
    }

    /// <summary>标/取消「不翻」。</summary>
    private void SetRowSkipped(Row row, bool skipped)
    {
        this.RecordRowIntent(row, translation: false);
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
        else if (row.Attribute is not null)
        {
            if (skipped)
            {
                this.pack.MarkAttributeSkipped(row.Attribute.Original);
            }
            else
            {
                this.pack.UnmarkAttributeSkipped(row.Attribute.Original);
            }

            row.Attribute.Review = null;
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
            this.StartTranslateRow(row);
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
            this.RecordRowIntent(row, translation: true);
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
                ImGui.PushStyleColor(ImGuiCol.Button, FireGazePalette.Selected);
                ImGui.PushStyleColor(ImGuiCol.Text, FireGazePalette.Text);
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
                ImGui.SetTooltip("灰名单可能影响插件功能，默认不翻译。");
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("###UITextSearch", "搜索原文 / 译文 / 上下文…", ref this.search, 256);

        ImGui.TextDisabled("状态列：✔ 人工译文 ｜ ⚙ 机器译文 ｜ ⚠ 待复核（这条不会写入，悬停看原因）");
        ImGui.TextDisabled("? 灰名单（默认不翻）｜ ⛔ 不翻 ｜ · 未翻译 ｜ 右键行：翻译这一条 / 标记不翻 / 清除译文 / 复制");
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
            "导出未翻译文本",
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
                    AtomicFile.WriteAllText(path, UITextStore.BuildAIExport(BuildExportEntries(), onlyUntranslated: true), new System.Text.UTF8Encoding(false));
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

        foreach (var attribute in this.pack.Attributes)
        {
            if (!seen.Add(attribute.Original))
            {
                continue;
            }

            list.Add(new UITextPackEntry
            {
                Original = attribute.Original,
                Translated = attribute.Translated,
                Context = attribute.Context ?? "属性",
                Source = attribute.Source,
            });
        }

        return list;
    }

    private void ImportFromFile()
    {
        var internalName = this.entry?.InternalName;
        var generation = this.editorGeneration;
        var hostGeneration = this.plugin.WorkLifetime.Generation;
        this.fileDialog.OpenFileDialog(
            "导入译文（桌面翻译工具的 JSON 格式）",
            ".json",
            (ok, path) =>
            {
                if (!ok)
                {
                    return;
                }
                if (!this.plugin.WorkLifetime.TryAcquire(out var lease, out _)) return;
                using (lease)
                    lease!.TryExecute(() =>
                    {
                        lock (this.TranslationGate)
                        {
                            if (!this.IsOpen || this.editorGeneration != generation
                                || this.plugin.WorkLifetime.Generation != hostGeneration
                                || !string.Equals(this.entry?.InternalName, internalName, StringComparison.Ordinal)) return;
                            this.ImportTranslationFile(path);
                        }
                    });
            });
    }

    private void ImportTranslationFile(string path)
    {
        lock (this.TranslationGate)
        {
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
                        if (existing is not null && !this.pack.IsSkipped(existing.Original))
                        {
                            if (!existing.IsUserSource || existing.Translated.Length == 0)
                            {
                                this.RecordTargetIntent(new(existing.Original, existing.Context, existing, null), translation: true);
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

                            this.RecordTargetIntent(new(resource.Original, null, null, resource), translation: true);
                            resource.Translated = incoming.Translated;
                            resource.Source = incoming.Source ?? "user";
                            resource.Review = null;
                            appliedOne = true;
                        }

                        // 属性条目同样按原文匹配
                        foreach (var attribute in this.pack.Attributes)
                        {
                            if (!string.Equals(attribute.Original, incoming.Original, StringComparison.Ordinal)
                                || this.pack.IsAttributeSkipped(attribute.Original))
                            {
                                continue;
                            }

                            if (attribute.IsUserSource && attribute.Translated.Length > 0)
                            {
                                continue;
                            }

                            this.RecordTargetIntent(new(attribute.Original, attribute.Context, null, null, attribute), translation: true);
                            attribute.Translated = incoming.Translated;
                            attribute.Source = incoming.Source ?? "user";
                            attribute.Review = null;
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
        }
    }

    private void MarkDirty()
    {
        this.glossaryRepairCache = null;
        if (!this.dirty)
        {
            this.dirty = true;
            this.dirtySince = DateTime.Now;
        }
    }

    private void RecordRowIntent(Row row, bool translation)
    {
        var target = new UITextTarget(row.Original, row.Context, row.Entry, row.Resource, row.Attribute);
        this.RecordTargetIntent(target, translation);
    }

    private void RecordTargetIntent(UITextTarget target, bool translation)
    {
        lock (this.TranslationGate)
        {
            if (this.entry is not null) this.store.RecordEditIntent(this.entry.InternalName, target);
            this.translationRequest?.Receipt.Invalidate(target);
            var key = target.Resource is { } resource ? (1, resource.Container, resource.Key)
                : (target.Attribute is null ? 0 : 2, string.Empty, target.Original);
            this.editIntents ??= new();
            this.editIntents.TryGetValue(key, out var old);
            this.editIntents[key] = new(target, translation || old?.Translation == true, !translation || old?.Skipped == true);
        }
    }

    private bool SaveIfDirty(bool force)
    {
        if (!this.dirty) return true;
        if (!this.plugin.WorkLifetime.TryAcquire(out var lease, out _)) return false;
        using (lease)
        {
            var saved = false;
            // Admission and the full synchronous write belong to one tracked
            // operation. Closing rejects new saves and waits for started ones.
            lease!.TryExecute(() => saved = this.SaveIfDirtyCore(force));
            return saved;
        }
    }

    private bool SaveIfDirtyCore(bool force)
    {
        if (!this.dirty)
        {
            return true;
        }

        if (!force && (DateTime.Now - this.dirtySince).TotalSeconds < 1.5)
        {
            return false;
        }

        if (this.entry is null)
        {
            return false;
        }

        // Keep local intent separate from the merged candidate until the
        // actual write succeeds. This includes deletion and unskip intent,
        // and avoids promoting background changes to local edits on retry.
        if (this.store.SaveMerged(this.entry.InternalName, latest =>
            UITextPackEditMerge.Merge(this.editBaseline ?? latest, this.pack, latest, this.editIntents?.Values), out var saved, out var error))
        {
            this.pack = saved!;
            this.editBaseline = UITextPackEditMerge.Clone(this.pack);
            this.dirty = false;
            this.editIntents?.Clear();
            this.lastSaveAt = DateTime.Now;
            this.packLoadedMtime = this.PackMtime(this.entry.InternalName);
            this.RebuildRows();
            return true;
        }
        else
        {
            this.SetStatus("保存失败：" + error, true);
            return false;
        }
    }

    // Invoked by the host's final cleanup only, after ordinary text work has
    // drained. Closing rejects ordinary saves, but must still persist edits
    // made before close without reopening ordinary work admission.
    internal void FlushForHostClose()
    {
        if (this.plugin.WorkLifetime.State != FireGaze.Lifecycle.PluginWorkLifetimeState.Closing)
            throw new InvalidOperationException("编辑器最终保存只能在宿主关闭阶段执行。");
        if (!this.SaveIfDirtyCore(force: true))
            throw new IOException("编辑器最终保存失败，未保存的编辑仍被保留。");
    }

    private DateTime PackMtime(string internalName) => this.store.LastWriteTime(internalName);

    /// <summary>
    ///     窗口开着时发现磁盘上的包被外部改过就重新载入 + 重抽（自带保护：本地有未保存改动时不动，
    ///     等 SaveIfDirty 里的 rebase 处理）。
    /// </summary>
    private void SyncFromDiskIfChanged()
    {
        if (this.entry is null || this.dirty || this.extractionTask is { IsCompleted: false })
        {
            return;
        }

        if (this.PackMtime(this.entry.InternalName) <= this.packLoadedMtime)
        {
            return;
        }

        UITextStore.ReadSnapshot snapshot;
        try { snapshot = this.store.LoadSnapshotRequired(this.entry.InternalName); }
        catch (Exception error)
        {
            this.SetStatus("读取最新译文失败，将稍后重试：" + error.Message, true);
            return;
        }
        this.pack = snapshot.Pack;
        this.editBaseline = UITextPackEditMerge.Clone(snapshot.Pack);
        this.packLoadedMtime = snapshot.LastWriteTime;
        this.rows = [];
        this.roles.Clear();
        this.extraction = null;
        this.extractionTask = null;
        this.SetStatus("译文包已更新，正在重新读取界面文字…", false);
        this.StartExtraction();
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
