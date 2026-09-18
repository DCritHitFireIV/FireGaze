using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FireGaze.RepoAudit;
using FireGaze.Translate;

namespace FireGaze.UI;

/// <summary>
/// 「参与翻译」独立窗口：搜索全部第三方插件的原文与译文，
/// 可以逐条改进、也可以给还没有译文的插件补上；
/// 改动先存在本地（配置目录），攒够了自己导出、在 GitHub 提 issue。
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

    private TranslationIndex? index;
    private Task<TranslationIndex>? buildTask;
    private DateTime retryAfter = DateTime.MinValue;
    private string search = string.Empty;
    private bool rebuildPending = true;
    private StateFilter stateFilter = StateFilter.All;

    // 搜索范围：名称 / 一行简介 / 详情
    private bool scopeName = true;
    private bool scopePunchline = true;
    private bool scopeDescription = true;

    // 编辑弹窗
    private TranslationIndexEntry? editing;
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

    public ContributeWindow(Plugin plugin, ContributionsStore store)
        : base("参与翻译###FireGazeContribute")
    {
        this.plugin = plugin;
        this.store = store;

        this.Size = new Vector2(880, 640);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 460),
        };

        this.store.Changed += () => this.rebuildPending = true;
    }

    /// <summary>待提交的条数（简介汉化页显示用）。</summary>
    public int PendingCount => this.store.Count;

    public override void Draw()
    {
        var open = this.IsOpen;

        // ---------------- 顶部说明 ----------------
        ImGui.TextWrapped("搜索插件名、一行简介、插件详情；看到翻译不合适就改，没有译文就补上。");
        ImGui.TextDisabled("提交的译文优先于机器翻译，之后不会被覆盖；改动先存在本地，攒够了自己导出、在 GitHub 提 issue。");
        ImGui.TextDisabled("本地改动只在你这里生效；点「从 GitHub 更新词表」会整份覆盖本地改动，没提交出去的译文会消失。");

        ImGui.Separator();

        // ---------------- 索引 ----------------
        if (open)
        {
            this.EnsureIndex();
        }

        if (this.index is null)
        {
            ImGui.TextDisabled("正在读取插件库…");
            if (this.index is { Available: false } broken)
            {
                UiHelpers.ColoredWrapped(UiHelpers.Warn, "读不到卫月的插件库列表：" + broken.FailureReason);
            }

            return;
        }

        if (!this.index.Available)
        {
            UiHelpers.ColoredWrapped(
                UiHelpers.Warn,
                $"读不到卫月的插件库列表（{this.index.FailureReason}）。可能是卫月升级改了内部字段——本窗口暂时不可用。");
            ImGui.Spacing();
            if (ImGui.Button("重试###RetryIndex"))
            {
                this.retryAfter = DateTime.MinValue;
                this.index = null;
            }

            return;
        }

        // ---------------- 搜索 + 范围 ----------------
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputTextWithHint("###ContributeSearch", "搜索插件名、原文、译文…", ref this.search, 256))
        {
            this.rebuildPending = true;
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
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("至少勾一项；三项全不勾 = 按插件名搜。");
        }

        // ---------------- 筛选 ----------------
        this.FilterRadio(StateFilter.All, "全部");
        this.FilterRadio(StateFilter.Missing, "缺译文");
        this.FilterRadio(StateFilter.Machine, "机器译");
        this.FilterRadio(StateFilter.User, "我提交过");
        this.FilterRadio(StateFilter.Review, "待复核");

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("「待复核」= 上游原文改过、译文还没跟上。");
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
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("勾上后连已停用仓库里的插件也列出来；这类插件当前不会出现在安装器里。");
        }

        // ---------------- 加库 ----------------
        ImGui.SetNextItemWidth(320);
        ImGui.InputTextWithHint("###AddRepoUrl", "仓库地址（pluginmaster.json）", ref this.repoInput, 512);
        ImGui.SameLine();
        var canAdd = this.repoInput.Trim().Length > 0;
        if (!canAdd)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("添加到我的库###AddRepo"))
        {
            this.AddRepository();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("把这条库加到你的第三方插件列表里，加完卫月会自己去抓它的插件；\n添加前会自动备份一份仓库列表，随时可撤回。");
        }

        if (!canAdd)
        {
            ImGui.EndDisabled();
        }

        if (!string.IsNullOrEmpty(this.repoMessage))
        {
            UiHelpers.ColoredWrapped(this.statusIsError ? UiHelpers.Bad : UiHelpers.Muted, this.repoMessage);
        }

        ImGui.Separator();

        // ---------------- 结果表 ----------------
        this.RebuildFiltered();

        var tableHeight = MathF.Max(120f, ImGui.GetContentRegionAvail().Y - 96f);
        var showIconColumn = this.plugin.Config.ShowIconsInContribute;

        if (ImGui.BeginTable(
                "###ContributeRows",
                showIconColumn ? 7 : 6,
                ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings,
                new Vector2(0, tableHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("状态", ImGuiTableColumnFlags.WidthFixed, 76, 0);
            if (showIconColumn)
            {
                ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 26, 1);
            }

            ImGui.TableSetupColumn("插件名", ImGuiTableColumnFlags.WidthFixed, 190, 2);
            ImGui.TableSetupColumn("来源库", ImGuiTableColumnFlags.WidthFixed, 120, 3);
            ImGui.TableSetupColumn("原文", ImGuiTableColumnFlags.WidthStretch, 0, 4);
            ImGui.TableSetupColumn("译文", ImGuiTableColumnFlags.WidthFixed, 120, 5);
            ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 60, 6);

            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            ImGui.TableNextColumn();
            ImGui.TableHeader("状态");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("缺译 = 还有字段没有译文；机器译 = 全部来自机器；\n你译 = 有你提交过的字段；带 ! 表示原文改过、需要复核。");
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
                ImGui.SetTooltip("插件所在的库链；点一下复制地址，右键可在浏览器打开。\n空白 = 官方主库或读不到来源。");
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
                ImGui.SetTooltip("你当前的译文完成度；够一行简介就显示前几个字。");
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

        // ---------------- 待提交 ----------------
        if (this.editing is not null && !this.editOpened)
        {
            this.editOpened = true;
            ImGui.OpenPopup("翻译###EditTranslation");
        }

        this.DrawEditPopup();
        this.DrawSubmitBar();

        // ---------------- 状态行 ----------------
        if (!string.IsNullOrEmpty(this.statusMessage))
        {
            UiHelpers.ColoredWrapped(this.statusIsError ? UiHelpers.Bad : UiHelpers.Muted, this.statusMessage);
        }
    }

    // ------------------------------------------------------------------ 索引

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
    }

    // ------------------------------------------------------------------ 行

    private void DrawRow(TranslationIndexEntry entry, bool showIconColumn)
    {
        ImGui.TableNextRow();

        // 状态
        ImGui.TableNextColumn();
        var (label, color) = entry.State switch
        {
            "missing" => ("缺译", UiHelpers.Bad),
            "user" => ("你译", UiHelpers.Good),
            _ => ("机器译", UiHelpers.Info),
        };

        if (entry.HasReview)
        {
            label += " !";
        }

        UiHelpers.ColoredText(color, label);
        if (ImGui.IsItemHovered())
        {
            var missing = entry.MissingFields.Count > 0
                ? "缺：" + string.Join("、", entry.MissingFields.Select(FieldLabel))
                : "三个字段都有译文";
            ImGui.SetTooltip(missing + (entry.HasReview ? "\n有字段的原文改过，建议复核" : string.Empty));
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
        if (entry.RepositoryUrl is null)
        {
            ImGui.TextDisabled("—");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("官方主库或读不到来源地址");
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
                ImGui.SetTooltip(entry.RepositoryUrl + "\n点一下复制");
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

                ImGui.EndPopup();
            }
        }

        // 原文
        ImGui.TableNextColumn();
        var preview = FirstNonEmpty(entry.OriginalPunchline, entry.OriginalDescription);
        UiHelpers.Truncated(preview, 44, entry.OriginalPunchline + "\n\n" + entry.OriginalDescription);

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
            UiHelpers.Truncated(current, 22, current);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"完成度 {entry.TranslatedFields}/{entry.TotalFields}（{entry.Completion * 100:0}%）");
        }

        // 操作
        ImGui.TableNextColumn();
        if (ImGui.SmallButton((entry.State == "missing" ? "补上…" : "改进…") + "###edit-" + entry.InternalName))
        {
            this.BeginEdit(entry);
        }
    }

    // ------------------------------------------------------------------ 编辑弹窗

    private void BeginEdit(TranslationIndexEntry entry)
    {
        this.editing = entry;
        this.editOpened = false;
        this.editName = entry.Entry?.Name?.Translated ?? string.Empty;
        this.editPunchline = entry.Entry?.Punchline?.Translated ?? string.Empty;
        this.editDescription = entry.Entry?.Description?.Translated ?? string.Empty;
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
        if (entry.RepositoryUrl is not null)
        {
            ImGui.SameLine();
            UiHelpers.Truncated(entry.RepositoryUrl, 50, entry.RepositoryUrl);
        }

        ImGui.Separator();

        this.DrawField("插件名", "Name", entry.OriginalName, ref this.editName);
        this.DrawField("一行简介", "Punchline", entry.OriginalPunchline, ref this.editPunchline);
        this.DrawField("插件详情", "Description", entry.OriginalDescription, ref this.editDescription);

        ImGui.Spacing();
        ImGui.TextDisabled("提交的译文优先于机器翻译，之后不会被覆盖；改完先存到本地。");

        ImGui.Separator();
        if (ImGui.Button("保存###SaveEdit", new Vector2(120, 0)))
        {
            this.SaveEdit();
        }

        ImGui.SameLine();
        if (ImGui.Button("取消###CancelEdit", new Vector2(120, 0)))
        {
            this.editing = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"待提交 {this.store.Count} 条");

        ImGui.EndPopup();
    }

    private void DrawField(string label, string field, string original, ref string value)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return;
        }

        ImGui.Spacing();
        ImGui.Text(label);
        ImGui.SameLine();
        if (value != original)
        {
            if (ImGui.SmallButton("与原文相同###Reset" + field))
            {
                value = original;
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("把这一栏填成原文（玩家不希望被翻译时用）");
        }

        ImGui.TextDisabled("原文");
        ImGui.SetNextItemWidth(-1);
        var readOnly = original;
        ImGui.InputTextMultiline(
            "###orig-" + field,
            ref readOnly,
            4096,
            new Vector2(0, ImGui.GetTextLineHeight() * 4),
            ImGuiInputTextFlags.ReadOnly);

        ImGui.TextDisabled("译文");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextMultiline(
            "###trans-" + field,
            ref value,
            4096,
            new Vector2(0, ImGui.GetTextLineHeight() * 4));
    }

    private void SaveEdit()
    {
        var entry = this.editing;
        if (entry is null)
        {
            return;
        }

        var changed = 0;
        changed += this.Commit(entry, "Name", entry.OriginalName, this.editName);
        changed += this.Commit(entry, "Punchline", entry.OriginalPunchline, this.editPunchline);
        changed += this.Commit(entry, "Description", entry.OriginalDescription, this.editDescription);

        this.editing = null;
        ImGui.CloseCurrentPopup();

        if (changed == 0)
        {
            this.SetStatus("没有改动", isError: false);
            return;
        }

        this.plugin.Table.SaveToConfigDirectory(out var error);
        this.plugin.ApplyTranslations();
        this.InvalidateIndex();
        this.SetStatus(
            error is null
                ? $"已存到本地 {changed} 处译文；攒够后在下面导出"
                : $"已记下 {changed} 处译文，但写盘失败：{error}",
            error is not null);
    }

    private int Commit(TranslationIndexEntry entry, string field, string original, string value)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return 0;
        }

        var current = entry.Entry is null
            ? string.Empty
            : field switch
            {
                "Name" => entry.Entry.Name?.Translated ?? string.Empty,
                "Punchline" => entry.Entry.Punchline?.Translated ?? string.Empty,
                _ => entry.Entry.Description?.Translated ?? string.Empty,
            };

        var next = value ?? string.Empty;
        if (string.Equals(current, next, StringComparison.Ordinal))
        {
            return 0;
        }

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
            Stale = entry.HasReview,
        });

        return 1;
    }

    // ------------------------------------------------------------------ 待提交 / 导出

    private void DrawSubmitBar()
    {
        ImGui.Separator();
        ImGui.Text($"待提交 {this.store.Count} 条");

        ImGui.SameLine();
        var canExport = this.store.Count > 0;
        if (!canExport)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("导出到文件###ExportContrib"))
        {
            var path = this.store.SaveExportFile();
            this.SetStatus(path is null ? "导出失败（写不进配置目录）" : "已导出：" + path, path is null);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("写一份 JSON 到配置目录的 contributions\\ 下；\n文件内容与「复制 JSON」完全一样。");
        }

        ImGui.SameLine();
        if (ImGui.Button("复制 JSON###CopyContrib"))
        {
            ImGui.SetClipboardText(this.store.BuildJson());
            this.SetStatus("已复制 JSON：贴到 issue 里即可", isError: false);
        }

        ImGui.SameLine();
        if (ImGui.Button("在 GitHub 提 issue###OpenIssue"))
        {
            this.OpenIssue();
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("打开浏览器并预填正文；GitHub 上直接提交即可。\n条目太多时正文会超长，用「复制 JSON」另贴一份。");
        }

        ImGui.SameLine();
        if (ImGui.Button("打开目录###OpenExportDir"))
        {
            this.store.OpenExportDirectory();
        }

        ImGui.SameLine();
        if (ImGui.Button("清空待提交###ClearContrib"))
        {
            this.store.ClearAll();
            ImGui.OpenPopup("清空待提交###ConfirmClear");
        }

        if (!canExport)
        {
            ImGui.EndDisabled();
        }

        // 确认弹窗
        ImGui.SetNextWindowSize(new Vector2(400, 0), ImGuiCond.Appearing);
        if (ImGui.BeginPopupModal("清空待提交###ConfirmClear", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("清空后本地这份待提交清单就没了（导出的文件还在）。确定吗？");
            ImGui.Spacing();
            if (ImGui.Button("清空", new Vector2(120, 0)))
            {
                this.store.ClearAll();
                this.SetStatus("已清空待提交清单", isError: false);
                ImGui.CloseCurrentPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("取消", new Vector2(120, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void OpenIssue()
    {
        try
        {
            var title = $"翻译贡献 {DateTime.Now:yyyy-MM-dd}";
            var body = this.store.BuildMarkdown();
            const int maxUrl = 7500;
            var encoded = Uri.EscapeDataString(body);
            if (encoded.Length > maxUrl)
            {
                encoded = Uri.EscapeDataString(
                    "### FireGaze 翻译贡献\n\n正文太长，完整内容见附件或下面的 JSON。\n\n```json\n" +
                    this.store.BuildJson() + "\n```");
            }

            var url = $"{ContributionsStore.RepoUrl}/issues/new?title={Uri.EscapeDataString(title)}&body={encoded}";
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            this.SetStatus("已打开浏览器；提交前可以把内容再核对一遍", isError: false);
        }
        catch (Exception e)
        {
            this.SetStatus("打开浏览器失败：" + e.Message, isError: true);
        }
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
