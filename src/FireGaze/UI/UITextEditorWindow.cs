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

    private readonly UITextStore store;
    private readonly FileDialogManager fileDialog = new();

    private InstalledPluginEntry? plugin;
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

    public UITextEditorWindow(UITextStore store)
        : base("插件汉化###FireGazeUITextEditor", ImGuiWindowFlags.None)
    {
        this.store = store;
        this.Size = new System.Numerics.Vector2(980, 620);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(640, 420),
        };
    }

    /// <summary>
    ///     当前插件。
    /// </summary>
    public string? CurrentInternalName => this.plugin?.InternalName;

    /// <summary>
    ///     换一个插件（自动抽取；同一个插件再点一次只是把窗口带到前面）。
    /// </summary>
    public void OpenFor(InstalledPluginEntry entry)
    {
        this.IsOpen = true;
        this.BringToFront();

        if (this.plugin is not null && string.Equals(this.plugin.InternalName, entry.InternalName, StringComparison.Ordinal))
        {
            return;
        }

        this.plugin = entry;
        this.pack = this.store.Load(entry.InternalName);
        this.rows = [];
        this.roles.Clear();
        this.search = string.Empty;
        this.dirty = false;
        this.extraction = null;
        this.extractionTask = null;
        this.SetStatus($"已载入 {entry.DisplayName}：正在抽取界面文本…", false);
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

        if (this.plugin is null)
        {
            ImGui.TextDisabled("还没有选插件。");
            return;
        }

        this.PollExtraction();
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
        var path = this.plugin?.DLLPath;
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

            var entry = this.pack.GetOrAdd(item.Original, item.Context, item.PreserveID);
            if (entry.Context is null)
            {
                entry.Context = item.Context;
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

        this.RevealIfNeeded();
        this.SortEntries();
        this.RebuildRows();
        this.MarkDirty();
        this.SetStatus($"抽取完成：候选 {uiCount} 条 · 灰名单 {ambiguous} 条（灰名单默认不翻）", false);
    }

    /// <summary>
    ///     包里躺着但这次抽取没再出现的条目：只把它们的上下文留着不动（可能只是插件换了版本）。
    /// </summary>
    private void RevealIfNeeded()
    {
        // 目前不需要额外处理；保留空壳函数是为了不把「抽取后不做删除」这条口径写丢
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
        foreach (var entry in this.pack.Entries)
        {
            UITextRole role;
            string reason;
            if (this.roles.TryGetValue(entry.Original, out var info))
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
                Entry = entry,
                Role = role,
                Reason = reason,
                Skipped = this.pack.IsSkipped(entry.Original),
            });
        }
    }

    // ── 界面 ─────────────────────────────────────────────────────────────

    private void DrawHeader()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Accent);
        ImGui.TextUnformatted(this.plugin!.DisplayName);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled($"({this.plugin.InternalName}{(string.IsNullOrEmpty(this.plugin.Version) ? string.Empty : " · v" + this.plugin.Version)})");

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
        // 翻译通道下一步接入：先禁用，免得点了一个没反应的按钮
        ImGui.BeginDisabled();
        if (ImGui.Button("批量翻译未翻"))
        {
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("翻译通道（免费接口 / 自填 key）下一步接入；现在可以先导出给 AI、再导入。");
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
        if (ImGui.Button("打开包目录"))
        {
            this.OpenPackFolder();
        }

        ImGui.SameLine();
        if (ImGui.Button("保存"))
        {
            this.SaveIfDirty(force: true);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(this.dirty ? "有未保存的改动…" : $"已保存（{this.lastSaveAt:HH:mm:ss}）");

        if (this.status.Length > 0)
        {
            UiHelpers.ColoredWrapped(this.statusIsError ? UiHelpers.Bad : UiHelpers.Muted, this.status);
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
                ImGui.SetTooltip("机器/库译文，可以改");
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
        var suggested = this.plugin!.InternalName + "-未翻-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".json";
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

    private void OpenPackFolder()
    {
        try
        {
            Directory.CreateDirectory(this.store.DirectoryPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = this.store.DirectoryPath,
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            this.SetStatus("打开目录失败：" + e.Message, true);
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

        if (this.plugin is null)
        {
            return;
        }

        if (this.store.Save(this.plugin.InternalName, this.pack, out var error))
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
