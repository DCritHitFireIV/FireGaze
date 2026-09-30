using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using FireGaze.RepoAudit;
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
    ///     界面上一行：抽取结果里的一条字面量 + 包里的译文。
    /// </summary>
    private sealed class Row
    {
        public UITextPackEntry Entry = null!;
        public UITextRole Role;
        public string Reason = string.Empty;
        public bool Skipped;
    }

    private readonly Plugin plugin;
    private readonly UITextStore store;
    private readonly FileDialogManager fileDialog = new();

    private InstalledPluginEntry? entry;
    private UITextPack pack = new();
    private Task<UITextExtraction>? extractionTask;
    private UITextExtraction? extraction;
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
    private bool showSettings;
    private string keyInput = string.Empty;
    private string messageKey = string.Empty;
    private Task<(string Channel, UITextTranslateResult Result)>? translateTask;
    private CancellationTokenSource? translateCancel;
    private int translateDone;
    private int translateTotal;

    public UITextEditorWindow(Plugin plugin, UITextStore store)
        : base("插件汉化###FireGazeUITextEditor", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        this.store = store;
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
        this.DrawHeader();
        ImGui.Separator();
        this.DrawToolbar();
        if (this.showSettings)
        {
            ImGui.Separator();
            this.DrawSettings();
        }

        ImGui.Separator();
        this.DrawTable();
        this.SaveIfDirty(force: false);
    }

    // ── 抽取 ─────────────────────────────────────────────────────────────

    private void StartExtraction()
    {
        var path = this.entry?.DLLPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            this.SetStatus("读不到插件主程序集路径，没法抽取。", true);
            return;
        }

        if (this.extractionTask is { IsCompleted: false })
        {
            return;
        }

        this.extractionTask = Task.Run(() => UIStringExtractor.Extract(path));
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

        this.SortEntries();
        this.RebuildRows();
        this.MarkDirty();
        this.SetStatus($"抽取完成：候选 {uiCount} 条 · 灰名单 {ambiguous} 条（灰名单默认不翻）", false);
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
    }

    // ── 翻译 ─────────────────────────────────────────────────────────────

    private List<Row> TranslationTargets()
    {
        var includeGrey = this.plugin.Config.UITextTranslateGreyList;
        return this.rows
            .Where(r => !r.Skipped && !r.Entry.HasTranslation)
            .Where(r => r.Role == UITextRole.UI || (includeGrey && r.Role == UITextRole.Ambiguous))
            .ToList();
    }

    private void StartTranslate(List<Row> targets)
    {
        if (this.translateTask is { IsCompleted: false })
        {
            return;
        }

        var items = targets
            .Select(r => new UITextTranslateItem(r.Entry.Original, r.Entry.Context))
            .ToList();
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

        var applied = 0;
        var placeholderRejected = 0;
        var unchanged = 0;
        foreach (var (original, translated) in outcome.Result.Translated)
        {
            var row = this.rows.FirstOrDefault(r => string.Equals(r.Entry.Original, original, StringComparison.Ordinal));
            if (row is null)
            {
                continue;
            }

            var clean = UITextText.CleanTranslated(translated);
            if (string.IsNullOrWhiteSpace(clean) || string.Equals(clean, UITextText.ForTranslation(original), StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            var problem = UITextText.CheckPlaceholders(original, clean);
            if (problem is not null)
            {
                row.Entry.Review = problem;
                placeholderRejected++;
                continue;
            }

            row.Entry.Translated = clean;
            row.Entry.Source = "ai:" + outcome.Channel;
            row.Entry.Review = null;
            applied++;
        }

        this.MarkDirty();
        var failed = outcome.Result.Failed.Count;
        var summary = $"翻译完成：写入 {applied} 条" +
                      (failed > 0 ? $" · 失败 {failed} 条" : string.Empty) +
                      (placeholderRejected > 0 ? $" · 占位符对不上跳过 {placeholderRejected} 条" : string.Empty) +
                      (unchanged > 0 ? $" · 原样返回 {unchanged} 条" : string.Empty);
        if (outcome.Result.Error is not null)
        {
            summary += $"（{outcome.Result.Error}）";
        }

        this.SetStatus(summary, outcome.Result.Error is not null && applied == 0);
    }

    // ── 界面 ─────────────────────────────────────────────────────────────

    private void DrawHeader()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Accent);
        ImGui.TextUnformatted(this.entry!.DisplayName);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled($"({this.entry.InternalName}{(string.IsNullOrEmpty(this.entry.Version) ? string.Empty : " · v" + this.entry.Version)})");

        var candidate = this.rows.Count(r => r.Role == UITextRole.UI);
        var ambiguous = this.rows.Count(r => r.Role == UITextRole.Ambiguous);
        var translated = this.rows.Count(r => r.Entry.HasTranslation);
        var skipped = this.rows.Count(r => r.Skipped);
        ImGui.TextDisabled(
            $"候选 {candidate} · 灰名单 {ambiguous} · 已翻 {translated} · 不翻 {skipped}" +
            (this.extractionTask is { IsCompleted: false } ? " · 抽取中…" : string.Empty));
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("重新抽取"))
        {
            this.StartExtraction();
            this.SetStatus("正在重新抽取…", false);
        }

        ImGui.SameLine();
        var translating = this.translateTask is { IsCompleted: false };
        ImGui.BeginDisabled(translating);
        if (ImGui.Button("批量翻译未翻"))
        {
            this.StartTranslate(this.TranslationTargets());
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var targets = this.TranslationTargets().Count;
            ImGui.SetTooltip($"用当前通道翻 {targets} 条（未翻的候选" +
                             (this.plugin.Config.UITextTranslateGreyList ? " + 灰名单" : "，灰名单不翻") + "）");
        }

        if (translating)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted($"{this.translateDone} / {this.translateTotal}");
            ImGui.SameLine();
            if (ImGui.Button("取消翻译"))
            {
                this.translateCancel?.Cancel();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("导出未翻（给 AI）"))
        {
            this.ExportForAI();
        }

        ImGui.SameLine();
        if (ImGui.Button("导入译文…"))
        {
            this.ImportFromFile();
        }

        ImGui.SameLine();
        if (ImGui.Button("翻译通道…"))
        {
            this.showSettings = !this.showSettings;
            this.keyInput = string.Empty;
            this.messageKey = string.Empty;
        }

        ImGui.SameLine();
        if (ImGui.Button("保存"))
        {
            this.SaveIfDirty(force: true);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(this.dirty ? "有未保存的改动…" : $"已保存（{this.lastSaveAt:HH:mm:ss}）");

        ImGui.TextDisabled("当前通道：" + UITextChannelFactory.Describe(this.plugin.Config));

        if (this.status.Length > 0)
        {
            UiHelpers.ColoredWrapped(this.statusIsError ? UiHelpers.Bad : UiHelpers.Muted, this.status);
        }
    }

    private void DrawSettings()
    {
        var config = this.plugin.Config;
        var changed = false;

        var channels = new (string Key, string Label)[]
        {
            ("auto", "免费·自动"),
            ("google", "Google 免 key"),
            ("mymemory", "MyMemory"),
            ("llm", "大模型（自填 key）"),
            ("deepl", "DeepL"),
        };
        for (var i = 0; i < channels.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
            }

            if (ImGui.RadioButton(channels[i].Label, string.Equals(config.UITextChannel, channels[i].Key, StringComparison.Ordinal)))
            {
                if (!string.Equals(config.UITextChannel, channels[i].Key, StringComparison.Ordinal))
                {
                    config.UITextChannel = channels[i].Key;
                    changed = true;
                }
            }
        }

        var translateGrey = config.UITextTranslateGreyList;
        if (ImGui.Checkbox("批量翻译时连灰名单一起翻", ref translateGrey))
        {
            config.UITextTranslateGreyList = translateGrey;
            changed = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("灰名单 = 既画在界面上、又被拿去做比较/当键名的字符串，默认不翻（翻错可能影响功能）。");
        }

        if (string.Equals(config.UITextChannel, "llm", StringComparison.Ordinal))
        {
            ImGui.Separator();
            var isDeepSeek = string.Equals(config.UITextLLMProvider, "deepseek", StringComparison.OrdinalIgnoreCase);
            if (ImGui.RadioButton("DeepSeek 官方", isDeepSeek) && !isDeepSeek)
            {
                config.UITextLLMProvider = "deepseek";
                config.UITextLLMBaseURL = "https://api.deepseek.com/v1";
                config.UITextLLMModel = "deepseek-flash";
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.RadioButton("其它 OpenAI 兼容服务", !isDeepSeek) && isDeepSeek)
            {
                config.UITextLLMProvider = "custom";
                changed = true;
            }

            var baseURL = config.UITextLLMBaseURL;
            ImGui.SetNextItemWidth(420);
            if (ImGui.InputText("接口地址", ref baseURL, 512))
            {
                config.UITextLLMBaseURL = baseURL;
                changed = true;
            }

            ImGui.SameLine();
            var model = config.UITextLLMModel;
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputText("模型", ref model, 128))
            {
                config.UITextLLMModel = model;
                changed = true;
            }

            this.DrawKeyRow("大模型 API key", config.UITextLLMKeyProtected, v => config.UITextLLMKeyProtected = v, ref changed);
        }

        if (string.Equals(config.UITextChannel, "deepl", StringComparison.Ordinal))
        {
            ImGui.Separator();
            this.DrawKeyRow("DeepL API key", config.UITextDeepLKeyProtected, v => config.UITextDeepLKeyProtected = v, ref changed);
        }

        ImGui.TextDisabled("key 只存在本机（DPAPI 加密），不会随任何提交上传；换机器/换 Windows 用户后需要重填。");
        if (this.messageKey.Length > 0)
        {
            UiHelpers.ColoredWrapped(UiHelpers.Muted, this.messageKey);
        }

        if (changed)
        {
            this.plugin.SaveConfig();
        }
    }

    private void DrawKeyRow(string label, string currentProtected, Action<string> apply, ref bool changed)
    {
        var existing = DPAPI.UnprotectFromBase64(currentProtected);
        ImGui.SetNextItemWidth(360);
        var input = this.keyInput;
        if (ImGui.InputTextWithHint($"###key-{label}", existing is null ? "粘贴 API key…" : "已保存（" + DPAPI.Mask(existing) + "），要换就粘贴新的", ref input, 256, ImGuiInputTextFlags.Password))
        {
            this.keyInput = input;
        }

        ImGui.SameLine();
        if (ImGui.Button("保存 key###save-" + label))
        {
            if (this.keyInput.Trim().Length == 0)
            {
                this.messageKey = "输入框是空的；要清掉已保存的 key 请点「清除」。";
            }
            else
            {
                var protectedValue = DPAPI.ProtectToBase64(this.keyInput.Trim());
                if (protectedValue.Length == 0)
                {
                    this.messageKey = "加密失败，key 没有保存。";
                }
                else
                {
                    apply(protectedValue);
                    changed = true;
                    this.keyInput = string.Empty;
                    this.messageKey = "已保存（DPAPI 加密）。";
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("清除###clear-" + label))
        {
            apply(string.Empty);
            changed = true;
            this.keyInput = string.Empty;
            this.messageKey = "已清除。";
        }

        ImGui.SameLine();
        if (ImGui.Button("测试连接###test-" + label))
        {
            this.TestChannel();
        }
    }

    private void TestChannel()
    {
        var targets = new List<UITextTranslateItem> { new("Settings", "test") };
        var channel = UITextChannelFactory.Create(this.plugin.Config, out var error);
        if (channel is null)
        {
            this.messageKey = error ?? "通道不可用。";
            return;
        }

        this.messageKey = "测试中…";
        var current = channel;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await current.TranslateAsync(targets, null, CancellationToken.None).ConfigureAwait(false);
                var text = result.Translated.TryGetValue("Settings", out var value) ? value : null;
                this.messageKey = text is null
                    ? $"连接失败：{result.Error ?? "没有返回译文"}"
                    : $"连接正常（{current.Name}）：Settings → {text}";
            }
            catch (Exception e)
            {
                this.messageKey = "连接失败：" + e.Message;
            }
        });
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
            ImGui.TextDisabled(row.Entry.Context ?? "—");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Shorten(row.Entry.Original, 60));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(row.Entry.Original);
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

        if (row.Entry.Review is { Length: > 0 } review)
        {
            UiHelpers.ColoredText(UiHelpers.Warn, "⚠");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(review);
            }

            return;
        }

        if (!row.Entry.HasTranslation)
        {
            ImGui.TextDisabled("·");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("还没翻");
            }
        }
        else if (row.Entry.IsUserSource)
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
                ImGui.SetTooltip("机器译文（" + (row.Entry.Source ?? "ai") + "），可以改");
            }
        }
    }

    private void DrawTranslationCell(Row row)
    {
        var value = row.Entry.Translated;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##translated", ref value, 2048))
        {
            row.Entry.Translated = value;
            row.Entry.Source = value.Length == 0 ? null : "user";
            row.Entry.Review = null;
            this.MarkDirty();
        }

        if (ImGui.IsItemHovered() && ImGui.IsItemActive() is false && value.Length > 0)
        {
            ImGui.SetTooltip(value);
        }
    }

    private void DrawRowContextMenu(Row row)
    {
        if (!ImGui.BeginPopupContextItem("###rowctx"))
        {
            return;
        }

        if (ImGui.MenuItem("翻译这一条", enabled: !(this.translateTask is { IsCompleted: false })))
        {
            this.StartTranslate([row]);
        }

        if (row.Skipped)
        {
            if (ImGui.MenuItem("取消「不翻」"))
            {
                this.pack.UnmarkSkipped(row.Entry.Original);
                row.Skipped = false;
                this.MarkDirty();
            }
        }
        else if (ImGui.MenuItem("标记「不翻」"))
        {
            this.pack.MarkSkipped(row.Entry.Original);
            row.Skipped = true;
            this.MarkDirty();
        }

        if (ImGui.MenuItem("清除译文", enabled: row.Entry.HasTranslation))
        {
            row.Entry.Translated = string.Empty;
            row.Entry.Source = null;
            this.MarkDirty();
        }

        if (ImGui.MenuItem("复制原文"))
        {
            ImGui.SetClipboardText(row.Entry.Original);
        }

        if (ImGui.MenuItem("复制译文", enabled: row.Entry.HasTranslation))
        {
            ImGui.SetClipboardText(row.Entry.Translated);
        }

        ImGui.EndPopup();
    }

    private void DrawFilters()
    {
        var filters = new (Filter Filter, string Label)[]
        {
            (Filter.Candidates, "候选"),
            (Filter.Ambiguous, "灰名单"),
            (Filter.Untranslated, "未翻"),
            (Filter.Translated, "已翻"),
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

            if (ImGui.SmallButton(filters[i].Label) && !selected)
            {
                this.filter = filters[i].Filter;
            }

            if (selected)
            {
                ImGui.PopStyleColor(2);
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("###UITextSearch", "搜索原文 / 译文 / 上下文…", ref this.search, 256);
    }

    private bool Matches(Row row, string filterText)
    {
        bool byFilter = this.filter switch
        {
            Filter.Candidates => row.Role == UITextRole.UI,
            Filter.Ambiguous => row.Role == UITextRole.Ambiguous,
            Filter.Untranslated => !row.Entry.HasTranslation && !row.Skipped,
            Filter.Translated => row.Entry.HasTranslation,
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

        return row.Entry.Original.Contains(filterText, StringComparison.OrdinalIgnoreCase)
               || row.Entry.Translated.Contains(filterText, StringComparison.OrdinalIgnoreCase)
               || (row.Entry.Context ?? string.Empty).Contains(filterText, StringComparison.OrdinalIgnoreCase);
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
                    File.WriteAllText(path, UITextStore.BuildAIExport(this.pack.Entries, onlyUntranslated: true), new System.Text.UTF8Encoding(false));
                    this.SetStatus($"已导出未翻条目：{path}", false);
                }
                catch (Exception e)
                {
                    this.SetStatus("导出失败：" + e.Message, true);
                }
            });
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
                        if (existing is null)
                        {
                            unknown++;
                            continue;
                        }

                        if (!existing.IsUserSource || existing.Translated.Length == 0)
                        {
                            existing.Translated = incoming.Translated;
                            existing.Source = incoming.Source ?? "user";
                            existing.Review = null;
                            applied++;
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
