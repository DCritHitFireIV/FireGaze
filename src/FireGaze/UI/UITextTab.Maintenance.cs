using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;
using FireGaze.UIText;

namespace FireGaze.UI;

internal sealed partial class UITextTab
{
    private readonly UITextJobQueue workQueue;
    private bool skipQueueItem;
    private string focusedPlugin = string.Empty;
    private bool focusScrollPending;
    private bool workflowDisposed;
    private DateTime queueTickAt;
    private readonly ConcurrentDictionary<string, MaintenanceResult> maintenance = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> diagnosticInput = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DiagnosticResult> diagnostics = new(StringComparer.Ordinal);
    private readonly HashSet<string> diagnosticBusy = new(StringComparer.Ordinal);

    private sealed record MaintenanceResult(UITextPack Candidate, string Baseline, string DLLHash,
        int NewTexts, HashSet<string> NewOriginals, int LibraryChanges, int AddedTranslations, int ChangedTranslations, string? Warning)
    {
        public bool CanApply => UITextMaintenance.CanApplyUpdate(new(AddedTranslations, ChangedTranslations, 0, 0), Warning);
    }
    private sealed record DiagnosticResult(string Query, List<UITextDiagnostic> Matches, string? Error);

    private static float RowDetailInset() => 64f;

    private static string MaintenanceSignature(InstalledPluginEntry entry, UITextPack pack)
    {
        var files = UITextRules.ResolveCompanions(entry.DLLPath, entry.InternalName);
        var directory = Path.GetDirectoryName(entry.DLLPath);
        if (directory is not null)
            foreach (var resource in pack.Resources.Where(r => UITextLocalizationFiles.IsFileContainer(r.Container)
                && !pack.IsResourceSkipped(r.Container, r.Key)))
            {
                var file = UITextLocalizationFiles.ResolveTarget(directory, resource.Container);
                if (file is not null) files.Add(file);
            }
        return UITextMaintenance.FileSignature(files);
    }

    public void FocusPlugin(string internalName)
    {
        this.search = string.Empty;
        this.focusedPlugin = internalName;
        this.focusScrollPending = true;
        this.filter = RowFilter.All;
        this.onlyEnabled = false;
        this.onlyThirdParty = false;
        this.expanded = internalName;
        this.installedPluginsDirty = true;
    }

    public void TickWorkflows()
    {
        if (this.workflowDisposed || DateTime.UtcNow - this.queueTickAt < TimeSpan.FromMilliseconds(200)) return;
        this.queueTickAt = DateTime.UtcNow;
        this.PollRun();
        if (this.installedPluginsDirty && this.indexRebuild is null)
        {
            this.installedPluginsDirty = false;
            this.KickIndexRebuild();
        }
        if (this.indexRebuild is { } rebuilding)
        {
            if (!rebuilding.IsCompleted) return;
            this.indexRebuild = null;
            if (rebuilding.Status == TaskStatus.RanToCompletion && rebuilding.Result.Available)
            {
                this.index = rebuilding.Result;
                this.indexAt = DateTime.Now;
                this.rowsDirty = true;
            }
        }
        // This path keeps a queue moving with the window closed.
        if (this.index is not null) this.TickQueue();
    }

    public void StopWorkflows()
    {
        this.workflowDisposed = true;
        this.workQueue.SetPaused(true);
        this.run?.Cancel.Cancel();
    }

    private void DrawQueueToolbar()
    {
        ImGui.BeginDisabled(this.index is not { Available: true });
        if (ImGui.Button("检查汉化更新"))
        {
            foreach (var entry in this.index!.All.Where(e => this.rows.GetValueOrDefault(e.InternalName)?.HasPack == true
                && !UITextRules.IsDoNotLocalize(e.InternalName)))
                this.workQueue.Enqueue(entry.InternalName, entry.DisplayName, UITextJobMode.CheckUpdates);
            this.workQueue.SetPaused(false);
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("检查所有已抽取插件的公共译文更新，不修改插件。");

        var jobs = this.workQueue.Items;
        var updates = this.index?.All.Where(e => this.maintenance.TryGetValue(e.InternalName, out var result) && result.CanApply).ToList() ?? [];
        if (updates.Count > 0)
        {
            UiHelpers.SameLineOrWrap(UiHelpers.LabelWidth($"批量更新 {updates.Count}"));
            ImGui.BeginDisabled(this.run is not null || this.editor.IsOpen || this.diagnosticBusy.Count > 0
                || jobs.Any(j => j.State is UITextJobState.Pending or UITextJobState.Running));
            UiHelpers.PushPrimaryButton();
            if (ImGui.Button($"批量更新 {updates.Count}###uit-update-all"))
            {
                foreach (var entry in updates) this.workQueue.Enqueue(entry.InternalName, entry.DisplayName, UITextJobMode.ApplyUpdates);
                this.workQueue.SetPaused(false);
            }
            UiHelpers.PopPrimaryButton();
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("写入检查到的全部译文更新，并依次重载插件。保留人工译文，不做全量机器重翻；请先停下插件任务。");
        }

        if (jobs.Any(j => j.State is UITextJobState.Pending or UITextJobState.Running))
        {
            UiHelpers.SameLineOrWrap(UiHelpers.LabelWidth("取消队列"));
            if (ImGui.SmallButton("取消队列"))
            {
                this.workQueue.Cancel();
                if (this.workQueue.Active is not null && this.run is { CanCancel: true } active)
                {
                    this.skipQueueItem = true;
                    active.Cancel.Cancel();
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("取消所有等待项。正在翻译的会停止并保留已保存译文；已经开始写入或重载的插件会安全完成后停止。");
        }
        if (jobs.Count > 0)
        {
            var waiting = jobs.Count(j => j.State == UITextJobState.Pending);
            var finished = jobs.Count(j => j.State is UITextJobState.Done or UITextJobState.Skipped);
            var failed = jobs.Count(j => j.State == UITextJobState.Failed);
            ImGui.SameLine(0, 8f);
            var queueText = $"队列 {finished}/{jobs.Count} · 等待 {waiting}" + (failed > 0 ? $" · 失败 {failed}" : string.Empty);
            if (this.workQueue.Paused && waiting > 0) queueText += " · 等待操作";
            else if (this.editor.IsOpen && waiting > 0) queueText += " · 等待校对结束";
            ImGui.PushStyleColor(ImGuiCol.Text, UiHelpers.Muted);
            UiHelpers.Fitted(queueText, string.Join("\n", jobs.Select(j => j.DisplayName + "：" + (j.Message.Length > 0 ? j.Message
                : j.State switch { UITextJobState.Pending => "等待", UITextJobState.Running => "进行中", UITextJobState.Done => "完成", UITextJobState.Failed => "失败", _ => "已取消" }))));
            ImGui.PopStyleColor();
        }
        if (this.workQueue.Error is { } error) UiHelpers.ColoredWrapped(UiHelpers.Bad, error);
    }

    private void TickQueue()
    {
        if (this.workflowDisposed || this.run is not null || this.pendingStart is not null || this.editor.IsOpen || this.diagnosticBusy.Count > 0) return;
        if (this.workQueue.Active is { } abandoned)
        {
            this.workQueue.Complete(abandoned.InternalName, false, "已取消，没有开始任务");
            this.workQueue.SetPaused(true);
            return;
        }
        if (this.index is not { Available: true }) return;
        var job = this.workQueue.BeginNext();
        if (job is null) return;
        var entry = this.index.All.FirstOrDefault(e => e.InternalName == job.InternalName);
        if (entry is null) { this.workQueue.Complete(job.InternalName, false, "这个插件已卸载"); return; }
        if (job.Mode == UITextJobMode.CheckUpdates) this.StartMaintenance(entry);
        else if (job.Mode == UITextJobMode.ApplyUpdates)
        {
            if (this.maintenance.TryGetValue(entry.InternalName, out var update) && update.CanApply) this.ApplyMaintenance(entry, update);
            else this.workQueue.Complete(job.InternalName, false, "请重新检查汉化更新后再更新。");
        }
        else this.StartOneClick(entry, this.maintenance.GetValueOrDefault(entry.InternalName)?.NewOriginals is { Count: > 0 } newTexts ? newTexts : null);
        if (this.run is null && this.pendingStart is null)
            this.workQueue.Complete(job.InternalName, false, this.notes.GetValueOrDefault(job.InternalName)?.Text ?? "任务未能开始");
    }

    private void StartMaintenance(InstalledPluginEntry entry)
    {
        if (this.run is not null || !this.runs.TryEnter(entry.InternalName, "检查汉化更新", out _)) return;
        this.maintenance.TryRemove(entry.InternalName, out _);
        var active = new Run { InternalName = entry.InternalName, Mode = RunMode.CheckUpdates, Stage = "正在检查新增文本与译文更新…", CanCancel = true };
        this.run = active;
        active.Task = Task.Run(async () =>
        {
            try
            {
                var before = this.store.Load(entry.InternalName);
                var baseline = before.ToJSON();
                if (string.IsNullOrEmpty(entry.DLLPath)) throw new IOException("没有找到插件文件，请刷新列表后重试。");
                var guard = this.patches.ExtractWithGuard(entry);
                if (guard.Extraction.Error is not null) throw new IOException(guard.Extraction.Error);
                var added = UITextMaintenance.NewCandidates(before, guard.Extraction);
                var newOriginals = UITextMaintenance.NewOriginals(before, guard.Extraction);
                var candidate = UITextPack.FromJSON(baseline, out _)!;
                UITextFlow.MergeExtraction(candidate, guard.Extraction);
                var hash = MaintenanceSignature(entry, candidate);
                var libraryChanges = this.plugin.Config.UITextLibraryEnabled
                    ? await this.plugin.TextLibrary.MergeIntoAsync(candidate, entry.InternalName, active.Cancel.Token, forceRefresh: true).ConfigureAwait(false) : 0;
                active.Cancel.Token.ThrowIfCancellationRequested();
                // A public pack can contain obsolete keys; classify again before offering a DLL write.
                UITextFlow.MergeExtraction(candidate, guard.Extraction);
                if (MaintenanceSignature(entry, candidate) != hash)
                    throw new IOException("检查期间插件文件发生了变化，请重新检查。");
                var preview = UITextFlow.PreviewMerge(before, candidate);
                var warning = this.plugin.Config.UITextLibraryEnabled ? this.plugin.TextLibrary.LastError : null;
                this.maintenance[entry.InternalName] = new(candidate, baseline, hash, added, newOriginals, libraryChanges,
                    preview.Added, preview.Overwritten, warning);
                this.FinishRun(active, new RowNote { Kind = warning is null ? NoteKind.Info : NoteKind.Bad,
                    Text = warning is not null ? "译文库暂时查不到，请稍后重新检查。"
                        : preview.Added + preview.Overwritten > 0 ? "有汉化更新，可点击更新。" : "检查完成，公共译文已是最新。", Detail = warning });
            }
            catch (OperationCanceledException) { this.FinishRun(active, new RowNote { Kind = NoteKind.Info, Text = "已取消检查，没有修改译文或插件。" }); }
            catch (Exception e) { this.FinishRun(active, new RowNote { Kind = NoteKind.Bad, Text = "检查失败：" + e.GetBaseException().Message }); }
        });
    }

    private void DrawMaintenance(InstalledPluginEntry entry)
    {
        if (this.maintenance.TryGetValue(entry.InternalName, out var result))
        {
            ImGui.TextDisabled($"新增文本 {result.NewTexts} 条 · 补入译文 {result.AddedTranslations} 条 · 更新译文 {result.ChangedTranslations} 条");
            if (result.Warning is { } warning) UiHelpers.ColoredWrapped(UiHelpers.Warn, "公共译文库检查未完成：" + warning);
        }
    }

    private void DrawDiagnosisPopup(InstalledPluginEntry entry, bool busy, Vector2 anchorMin, Vector2 anchorMax)
    {
        var viewport = ImGui.GetMainViewport();
        var margin = ImGui.GetStyle().WindowPadding;
        var width = Math.Min(440f, viewport.WorkSize.X - margin.X * 2);
        var height = Math.Min(360f, viewport.WorkSize.Y - margin.Y * 2);
        var left = Math.Clamp(anchorMin.X, viewport.WorkPos.X + margin.X, viewport.WorkPos.X + viewport.WorkSize.X - width - margin.X);
        var top = anchorMax.Y + ImGui.GetStyle().ItemSpacing.Y;
        if (top + height > viewport.WorkPos.Y + viewport.WorkSize.Y) top = Math.Max(viewport.WorkPos.Y + margin.Y, anchorMin.Y - height);
        ImGui.SetNextWindowPos(new Vector2(left, top), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, 0));
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0), new Vector2(width, height));
        if (!ImGui.BeginPopup("###uit-english-popup")) return;
        ImGui.TextUnformatted("这句怎么还是英文");
        ImGui.TextUnformatted("仍是英文的原文");
        var input = this.diagnosticInput.GetValueOrDefault(entry.InternalName, string.Empty);
        ImGui.SetNextItemWidth(Math.Max(80f, ImGui.GetContentRegionAvail().X));
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        if (ImGui.InputTextWithHint("###uit-english-text", "仍是英文的原文", ref input, 512))
        {
            this.diagnosticInput[entry.InternalName] = input;
            this.diagnostics.TryRemove(entry.InternalName, out _);
        }
        var checking = this.diagnosticBusy.Contains(entry.InternalName);
        ImGui.BeginDisabled(busy || checking || input.Trim().Length == 0);
        if (ImGui.Button(checking ? "检查中…###uit-diagnose" : "检查这句###uit-diagnose")) this.StartDiagnosis(entry, input);
        ImGui.EndDisabled();
        if (this.diagnostics.TryGetValue(entry.InternalName, out var diagnosis) && diagnosis.Query == input)
        {
            if (diagnosis.Error is { } error) UiHelpers.ColoredWrapped(UiHelpers.Bad, "检查失败：" + error);
            else if (diagnosis.Matches.Count == 0)
                ImGui.TextWrapped("没有找到这句。它可能是拼接出来的文字，或存放在目前读不到的语言文件里。可以换成其中几个英文词再查，或准备反馈。");
            else
            {
                if (diagnosis.Matches.Count == 10) ImGui.TextDisabled("最多显示 10 处匹配，可输入更完整的原文");
                for (var matchIndex = 0; matchIndex < diagnosis.Matches.Count; matchIndex++)
                {
                    var match = diagnosis.Matches[matchIndex];
                    ImGui.PushID(matchIndex);
                    ImGui.TextWrapped(match.Original);
                    ImGui.TextWrapped(match.Kind switch
                    {
                        UITextDiagnosticKind.KeptOriginal => "这句被保留为原文。单词缺少语境、可能影响插件功能，或你曾标记不翻；不要直接全量重翻。",
                        UITextDiagnosticKind.Excluded => "这句用于插件功能或查表，不能直接替换成中文。",
                        UITextDiagnosticKind.NeedsReview => "译文需要校对，检查通过前不会写入插件。",
                        UITextDiagnosticKind.ReadyToApply => "已有译文，但还没有应用到当前插件。可以写入并重载。",
                        UITextDiagnosticKind.AlreadyTranslated => "已有译文和补丁。若仍显示英文，先确认插件已重载，并检查它自己的语言设置。",
                        _ => "已经找到了这句，但还没有译文。可以进入编辑校对，结合上下文处理。",
                    });
                    if (match.Translation.Length > 0) ImGui.TextWrapped("译文：" + match.Translation);
                    if ((match.Reason.Length > 0 || match.Context is { Length: > 0 }) && ImGui.TreeNode("查看判断依据"))
                    {
                        if (match.Reason.Length > 0) ImGui.TextWrapped("原因：" + match.Reason);
                        if (match.Context is { Length: > 0 } context) ImGui.TextWrapped("位置：" + context);
                        ImGui.TreePop();
                    }
                    ImGui.PopID();
                }
            }
            ImGui.BeginDisabled(busy);
            if (diagnosis.Matches.Any(m => m.Kind != UITextDiagnosticKind.Excluded) && ImGui.Button("定位到编辑校对###uit-diagnose-editor"))
                this.editor.OpenForText(entry, diagnosis.Query);
            ImGui.EndDisabled();
            if (ImGui.Button("准备反馈###uit-diagnose-feedback"))
            {
                this.feedbackCategory = 0;
                this.feedbackText = $"插件：{entry.DisplayName}（{entry.InternalName} {entry.Version}）\n仍显示英文：{diagnosis.Query}\n检查："
                    + (diagnosis.Error ?? (diagnosis.Matches.Count == 0 ? "没有找到原文" : string.Join("；", diagnosis.Matches.Select(m => m.Original + "：" + m.Reason))));
                this.feedbackOpen = true;
            }
            if (diagnosis.Matches.Any(m => m.Kind == UITextDiagnosticKind.ReadyToApply))
            {
                ImGui.BeginDisabled(busy);
                if (ImGui.Button("写入并重载###uit-diagnose-apply")) this.StartApplyCurrent(entry);
                ImGui.EndDisabled();
            }
        }
        ImGui.EndPopup();
    }

    private void ApplyMaintenance(InstalledPluginEntry entry, MaintenanceResult result)
    {
        if (this.run is not null || !result.CanApply || !this.runs.TryEnter(entry.InternalName, "应用译文更新", out _)) return;
        var active = new Run { InternalName = entry.InternalName, Mode = RunMode.ApplyUpdates, Stage = "正在应用译文更新并重载…" };
        this.run = active;
        active.Task = Task.Run(async () =>
        {
            try
            {
                if (this.store.Load(entry.InternalName).ToJSON() != result.Baseline
                    || string.IsNullOrEmpty(entry.DLLPath) || MaintenanceSignature(entry, result.Candidate) != result.DLLHash)
                    throw new IOException("插件或本机译文已经变化，请重新检查更新。");
                if (!this.store.Save(entry.InternalName, result.Candidate, out var error)) throw new IOException(error);
                this.maintenance.TryRemove(entry.InternalName, out _);
                var outcome = await this.patches.ApplyAndReloadAsync(entry).ConfigureAwait(false);
                this.FinishRun(active, new RowNote { Kind = outcome.Ok ? NoteKind.Good : NoteKind.Bad, Text = outcome.Message });
            }
            catch (Exception e)
            {
                this.maintenance.TryRemove(entry.InternalName, out _);
                this.FinishRun(active, new RowNote { Kind = NoteKind.Bad, Text = "应用更新失败：" + e.GetBaseException().Message });
            }
        });
    }

    private void StartApplyCurrent(InstalledPluginEntry entry)
    {
        if (this.run is not null || !this.runs.TryEnter(entry.InternalName, "写入并重载", out _)) return;
        var active = new Run { InternalName = entry.InternalName, Stage = "正在写入并重载…" };
        this.run = active;
        active.Task = Task.Run(async () =>
        {
            try
            {
                var outcome = await this.patches.ApplyAndReloadAsync(entry).ConfigureAwait(false);
                this.FinishRun(active, new RowNote { Kind = outcome.Ok ? NoteKind.Good : NoteKind.Bad, Text = outcome.Message });
            }
            catch (Exception e) { this.FinishRun(active, new RowNote { Kind = NoteKind.Bad, Text = "写入失败：" + e.GetBaseException().Message }); }
        });
    }

    private void StartDiagnosis(InstalledPluginEntry entry, string query)
    {
        if (!this.runs.TryEnter(entry.InternalName, "检查英文原文", out _)) return;
        this.diagnosticBusy.Add(entry.InternalName);
        _ = Task.Run(async () =>
        {
            DiagnosticResult result;
            try
            {
                var extraction = this.patches.ExtractWithGuard(entry).Extraction;
                var pack = this.store.Load(entry.InternalName);
                var applied = this.patches.StatusOf(entry, out _) == UITextPatchStatus.Applied;
                result = new(query, UITextMaintenance.Diagnose(pack, extraction, query, applied, this.patches.PackNewerThanPatch(entry)), extraction.Error);
            }
            catch (Exception e) { result = new(query, [], e.GetBaseException().Message); }
            finally { this.runs.Exit(entry.InternalName); }
            this.diagnostics[entry.InternalName] = result;
            await Plugin.Framework.RunOnFrameworkThread(() => this.diagnosticBusy.Remove(entry.InternalName)).ConfigureAwait(false);
        });
    }
}
