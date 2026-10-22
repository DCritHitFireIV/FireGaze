using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using FireGaze.RepoAudit;
using FireGaze.UIText;

namespace FireGaze.UI;

internal sealed partial class UITextTab
{
    private sealed record RewritePreview(UITextRewritePlan Plan, int Count, int Requests, int Humans, string Channel);
    private sealed class PendingRewrite
    {
        public InstalledPluginEntry Entry = null!;
        public UITextJobMode Mode;
        public Task<RewritePreview> Preview = null!;
        public bool NeedsOpen = true;
        public string? Error;
        public CancellationTokenSource Cancel = new();
        public bool Cancelled;
        public bool Resume;
    }

    private PendingRewrite? pendingRewrite;
    private string RewriteDirectory => Path.Combine(this.store.DirectoryPath, "retranslation-backups");

    private string RewriteChannelSignature() => UITextRetranslation.Hash(JsonSerializer.Serialize(new
    {
        this.plugin.Config.UITextChannel, this.plugin.Config.UITextLLMProvider,
        this.plugin.Config.UITextLLMBaseURL, this.plugin.Config.UITextLLMModel,
        this.plugin.Config.UITextLLMKeyProtected, this.plugin.Config.UITextCaiyunKeyProtected,
        this.plugin.Config.UITextDeepLKeyProtected, this.plugin.Config.UITextUseGlossary,
        this.plugin.Config.UITextTranslateGreyList,
    }));

    private void DrawRewriteMenu(InstalledPluginEntry entry, bool disabled)
    {
        UiHelpers.SameLineOrWrap(ImGui.GetFrameHeight(), 4);
        using var disabledScope1 = UiHelpers.Disabled(disabled || this.pendingRewrite is not null);
        if (ImGui.ArrowButton("###uit-rewrite-menu", ImGuiDir.Down)) ImGui.OpenPopup("###uit-rewrite-actions");
        disabledScope1.Dispose();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("提升或重翻译文");
        if (!ImGui.BeginPopup("###uit-rewrite-actions")) return;
        using var disabledScope2 = UiHelpers.Disabled(disabled || this.pendingRewrite is not null);
        if (ImGui.MenuItem("提升译文…")) this.PrepareRewrite(entry, UITextJobMode.UpgradeTranslations);
        if (ImGui.MenuItem("重翻非人工译文…")) this.PrepareRewrite(entry, UITextJobMode.RetranslateNonHuman);
        var resumable = this.workQueue.Items.LastOrDefault(j => j.InternalName == entry.InternalName && j.Rewrite is not null
            && j.Mode is UITextJobMode.UpgradeTranslations or UITextJobMode.RetranslateNonHuman
            && j.State is UITextJobState.Failed or UITextJobState.Skipped);
        if (resumable is not null && ImGui.MenuItem("继续上次重翻…")) this.PrepareRewrite(entry, resumable.Mode, resumable.Rewrite);
        if (ImGui.MenuItem("撤回上次重翻…")) this.PrepareRewrite(entry, UITextJobMode.RestoreTranslations);
        disabledScope2.Dispose();
        ImGui.EndPopup();
    }

    private void PrepareRewrite(InstalledPluginEntry entry, UITextJobMode mode, UITextRewritePlan? resume = null)
    {
        if (this.pendingRewrite is not null || !this.runs.TryEnter(entry.InternalName, "准备重翻预览", out _)) return;
        var pending = new PendingRewrite { Entry = entry, Mode = mode, Resume = resume is not null };
        this.pendingRewrite = pending;
        var channelSignature = this.RewriteChannelSignature();
        var channelDescription = UITextChannelFactory.Describe(this.plugin.Config);
        var includeGrey = this.plugin.Config.UITextTranslateGreyList;
        if (mode != UITextJobMode.RestoreTranslations)
        {
            if (mode == UITextJobMode.UpgradeTranslations && this.plugin.Config.UITextChannel != "llm")
                pending.Error = "提升译文需要先在翻译设置中选择大模型。";
            else if (UITextChannelFactory.Create(this.plugin.Config, out var error) is null) pending.Error = error;
        }
        pending.Preview = Task.Run(async () =>
        {
            try
            {
                if (UITextRules.IsDoNotLocalize(entry.InternalName)) throw new IOException("这是中文插件，不需要重翻。");
                if (string.IsNullOrEmpty(entry.DLLPath)) throw new IOException("没有找到插件文件，请刷新列表。");
                var before = this.store.LoadRequired(entry.InternalName);
                var json = before.ToJSON();
                var signature = MaintenanceSignature(entry, before);
                if (mode == UITextJobMode.RestoreTranslations)
                {
                    var id = UITextRetranslation.LatestSnapshot(this.RewriteDirectory, entry.InternalName)
                        ?? throw new IOException("还没有可撤回的重翻记录。");
                    UITextRetranslation.ReadSnapshot(this.RewriteDirectory, entry.InternalName, id, json, signature);
                    return new RewritePreview(new UITextRewritePlan { ID = id, PackHash = UITextRetranslation.Hash(json),
                        DLLSignature = signature, SourceSignature = this.patches.MaintenanceSourceSignature(entry) }, 0, 0, 0, "");
                }
                var guard = await this.patches.ExtractWithRecoveryAsync(entry, pending.Cancel.Token).ConfigureAwait(false);
                if (guard.Extraction.Error is { } error) throw new IOException(error);
                var candidate = UITextPack.FromJSON(json, out _)!;
                UITextFlow.MergeExtraction(candidate, guard.Extraction);
                signature = MaintenanceSignature(entry, candidate);
                var upgrade = mode == UITextJobMode.UpgradeTranslations;
                var plan = resume ?? new UITextRewritePlan { Upgrade = upgrade, IncludeGrey = includeGrey,
                    PackHash = UITextRetranslation.Hash(json), DLLSignature = signature,
                    ChannelSignature = channelSignature, SourceSignature = this.patches.MaintenanceSourceSignature(entry) };
                var completed = Array.Empty<string>();
                if (resume is not null)
                {
                    var hasSnapshot = UITextRetranslation.HasSnapshot(this.RewriteDirectory, entry.InternalName, plan.ID);
                    var snapshot = hasSnapshot ? UITextRetranslation.ReadSnapshot(this.RewriteDirectory, entry.InternalName, plan.ID, json, signature) : null;
                    UITextRetranslation.ValidatePlan(plan, snapshot, json, signature, this.patches.MaintenanceSourceSignature(entry), channelSignature);
                    completed = snapshot?.Completed.ToArray() ?? [];
                }
                else plan.TargetIDs = UITextRetranslation.SelectTargets(candidate, upgrade, includeGrey).Select(UITextRetranslation.TargetID).ToList();
                var targets = UITextRetranslation.RemainingTargets(candidate, plan.Upgrade, plan.IncludeGrey, plan.TargetIDs.ToArray(), completed);
                var humans = candidate.Entries.Count(e => e.IsUserSource) + candidate.Resources.Count(e => e.IsUserSource)
                    + candidate.Attributes.Count(e => e.IsUserSource);
                return new RewritePreview(plan, targets.Count, UITextFlow.BuildTranslateItems(targets).Count, humans, channelDescription);
            }
            finally { this.runs.Exit(entry.InternalName); }
        });
    }

    private void DrawRewriteModal()
    {
        this.CleanupRewritePreview();
        if (this.pendingRewrite is not { } pending) return;
        if (pending.Cancelled) return;
        const string name = "确认译文操作###uit-rewrite-confirm";
        if (pending.NeedsOpen) { ImGui.OpenPopup(name); pending.NeedsOpen = false; }
        ImGui.SetNextWindowSizeConstraints(new Vector2(420, 0), new Vector2(560, float.MaxValue));
        FireGazeWindowPlacement.PrepareModal(new Vector2(560, 420));
        if (!ImGui.BeginPopupModal(name, ImGuiWindowFlags.AlwaysAutoResize))
        {
            // 弹窗被外部关闭（最常见是 Esc）：必须复位状态，否则重翻菜单会一直禁用、TickQueue 也会停摆。
            // 与 DrawFeedbackModal / DrawUploadConfirmModal 的「弹窗未开就清 pending」保持同一模式。
            pending.Cancel.Cancel();
            _ = pending.Preview.Exception;
            pending.Cancel.Dispose();
            this.pendingRewrite = null;
            return;
        }
        ImGui.TextWrapped(pending.Entry.DisplayName);
        if (!pending.Preview.IsCompleted) ImGui.TextDisabled("正在确认文本范围…");
        else if (pending.Preview.IsFaulted) pending.Error = pending.Preview.Exception!.GetBaseException().Message;
        else if (pending.Preview.IsCanceled) pending.Error = "已取消预览。";
        var preview = pending.Preview.Status == TaskStatus.RanToCompletion ? pending.Preview.Result : null;
        var restore = pending.Mode == UITextJobMode.RestoreTranslations;
        if (preview is not null)
        {
            if (restore) ImGui.TextWrapped("恢复上次重翻前的译文，并写入、重载插件。若之后改过译文或插件，则不会覆盖。");
            else
            {
                ImGui.TextWrapped(pending.Mode == UITextJobMode.UpgradeTranslations
                    ? "提升未译、免费通道及基础云端译文，保留已有大模型和来源不明的译文。"
                    : "重翻所有非人工译文，包括云端、大模型和来源不明的译文。");
                ImGui.TextWrapped($"本次翻译 {preview.Count} 条 · 保留人工译文 {preview.Humans} 条");
                if (preview.Count == 0) ImGui.TextWrapped(pending.Resume
                    ? "译文已保存，可继续写入并重载，不再发送翻译请求。"
                    : "没有符合这次范围的文本，现有译文保留。");
                ImGui.TextWrapped("通道：" + preview.Channel);
                ImGui.TextWrapped("不翻、功能用字和原生中文保留；重翻前备份旧译文，可能消耗所选通道额度。");
                if (preview.Plan.IncludeGrey) ImGui.TextWrapped("按当前设置包含灰名单文本。");
                if (preview.Plan.ChannelSignature != this.RewriteChannelSignature()) pending.Error = "翻译设置已变化，请取消后重新确认。";
                else if (preview.Plan.Upgrade && this.plugin.Config.UITextChannel != "llm") pending.Error = "提升译文需要先在翻译设置中选择大模型。";
            }
        }
        if (pending.Error is { } message) ImGui.TextWrapped(message);
        if (ImGui.Button("取消"))
        {
            pending.Cancel.Cancel();
            pending.Cancelled = true;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SetItemDefaultFocus();
        UiHelpers.SameLineOrWrap(UiHelpers.LabelWidth("确认并加入队列"));
        using var disabledScope3 = UiHelpers.Disabled(pending.Cancelled || preview is null || pending.Error is not null || !restore && !pending.Resume && preview.Count == 0);
        if (ImGui.Button("确认并加入队列") && preview is not null)
        {
            if (this.workQueue.RequestRewrite(pending.Entry.InternalName, pending.Entry.DisplayName, pending.Mode, preview.Plan))
            {
                this.notes[pending.Entry.InternalName] = new RowNote { Kind = NoteKind.Info, Text = "已加入译文操作队列。" };
                pending.Cancel.Dispose();
                this.pendingRewrite = null;
                ImGui.CloseCurrentPopup();
            }
            else pending.Error = this.workQueue.Error ?? "这个插件已有汉化任务，请等它完成。";
        }
        disabledScope3.Dispose();
        ImGui.EndPopup();
    }

    private void CleanupRewritePreview()
    {
        if (this.pendingRewrite is not { Cancelled: true } pending || !pending.Preview.IsCompleted) return;
        _ = pending.Preview.Exception;
        pending.Cancel.Dispose();
        this.pendingRewrite = null;
    }

    private void StartRewrite(InstalledPluginEntry entry, UITextJob job)
    {
        if (job.Rewrite is not { } plan || UITextRules.IsDoNotLocalize(entry.InternalName))
        { this.workQueue.Complete(entry.InternalName, false, "重翻记录不可用，请重新确认。"); return; }
        if (!this.runs.TryEnter(entry.InternalName, "重翻译文", out _)) return;
        var active = new Run { InternalName = entry.InternalName, Mode = RunMode.Rewrite, Stage = "正在验证译文操作…", CanCancel = true };
        this.run = active;
        active.Task = Task.Run(() => this.RunRewriteAsync(entry, job.Mode, plan, active));
    }

    private async Task RunRewriteAsync(InstalledPluginEntry entry, UITextJobMode mode, UITextRewritePlan plan, Run active)
    {
        // Run this separately from normal fill: deliberate retranslation must not be skipped by cloud prefill.
        var token = active.Cancel.Token;
        try
        {
            var pack = this.store.LoadRequired(entry.InternalName);
            var expectedJSON = pack.ToJSON();
            if (mode != UITextJobMode.RestoreTranslations)
            {
                var guard = await this.patches.ExtractWithRecoveryAsync(entry, token).ConfigureAwait(false);
                if (guard.Extraction.Error is { } extractionError) throw new IOException(extractionError);
                UITextFlow.MergeExtraction(pack, guard.Extraction);
            }
            var dll = MaintenanceSignature(entry, pack);
            var hasSnapshot = UITextRetranslation.HasSnapshot(this.RewriteDirectory, entry.InternalName, plan.ID);
            var snapshot = hasSnapshot ? UITextRetranslation.ReadSnapshot(this.RewriteDirectory, entry.InternalName, plan.ID, expectedJSON, dll) : null;
            UITextRetranslation.ValidatePlan(plan, snapshot, expectedJSON, dll, this.patches.MaintenanceSourceSignature(entry),
                mode == UITextJobMode.RestoreTranslations ? plan.ChannelSignature : this.RewriteChannelSignature());
            var completed = (snapshot?.Completed ?? []).ToHashSet(StringComparer.Ordinal);
            var source = snapshot?.SourceSignature ?? plan.SourceSignature;

            void ValidateCurrent(bool checkCancellation = true)
            {
                if (checkCancellation) token.ThrowIfCancellationRequested();
                if (this.store.LoadRequired(entry.InternalName).ToJSON() != expectedJSON || MaintenanceSignature(entry, pack) != dll
                    || this.patches.MaintenanceSourceSignature(entry) != source)
                    throw new IOException("操作期间译文或插件发生变化，已停止覆盖。");
            }
            void SaveCheckpoint()
            {
                if (!this.store.Save(entry.InternalName, pack, out var error)) throw new IOException("保存译文失败：" + error);
                expectedJSON = pack.ToJSON();
                UITextRetranslation.SealSnapshot(this.RewriteDirectory, entry.InternalName, plan.ID, expectedJSON, dll, source, completed.ToArray());
            }
            if (mode == UITextJobMode.RestoreTranslations)
            {
                ValidateCurrent();
                pack = UITextPack.FromJSON(snapshot!.BeforeJSON, out var error) ?? throw new IOException(error);
                SaveCheckpoint();
            }
            else
            {
                if (plan.ChannelSignature != this.RewriteChannelSignature() || plan.Upgrade && this.plugin.Config.UITextChannel != "llm")
                    throw new IOException("翻译通道已变化，请重新确认后重翻。");
                var channel = UITextChannelFactory.Create(this.plugin.Config, out var error) ?? throw new IOException(error);
                ValidateCurrent();
                var targets = UITextRetranslation.RemainingTargets(pack, plan.Upgrade, plan.IncludeGrey, plan.TargetIDs.ToArray(), completed.ToArray());
                UITextRetranslation.SealSnapshot(this.RewriteDirectory, entry.InternalName, plan.ID, expectedJSON, dll, source, completed.ToArray());
                var items = UITextFlow.BuildTranslateItems(targets);
                active.Total = items.Count;
                active.TranslateStartedTicks = DateTime.Now.Ticks;
                var applied = 0;
                var result = await UITextTranslationSession.TranslateAsync(channel, items, (done, _) =>
                {
                    active.Done = done;
                    active.Stage = $"重翻 {done} / {items.Count} 条（{channel.Name}）";
                }, batch =>
                {
                    ValidateCurrent(checkCancellation: false);
                    applied += UITextRetranslation.Accept(pack, targets, batch.Translated, channel.Name).Applied;
                    foreach (var target in targets.Where(t => batch.Translated.ContainsKey(t.Original))) completed.Add(UITextRetranslation.TargetID(target));
                    SaveCheckpoint();
                }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (result.Error is not null || result.Failed.Count > 0)
                {
                    this.FinishRun(active, new RowNote { Kind = NoteKind.Bad, Text = "重翻未完成，旧译文及已保存结果保留。",
                        Detail = result.Error ?? $"{result.Failed.Count} 条未返回；可从菜单继续上次重翻。" });
                    return;
                }
                if (applied == 0 && items.Count > 0)
                {
                    this.FinishRun(active, new RowNote { Kind = NoteKind.Info, Text = "没有可替换的有效新译文，旧译文保留。" });
                    return;
                }
            }
            ValidateCurrent();
            active.CanCancel = false;
            active.Stage = "正在写入译文并重载…";
            var outcome = await this.patches.ApplyAndReloadAsync(entry).ConfigureAwait(false);
            // Patch/reload may change the plugin signature even on failure; rollback still validates the resulting files.
            UITextRetranslation.SealSnapshot(this.RewriteDirectory, entry.InternalName, plan.ID, expectedJSON,
                MaintenanceSignature(entry, pack), this.patches.MaintenanceSourceSignature(entry), completed.ToArray());
            if (outcome.Ok && mode == UITextJobMode.RestoreTranslations)
                UITextRetranslation.MarkRestored(this.RewriteDirectory, entry.InternalName, plan.ID);
            this.maintenance.TryRemove(entry.InternalName, out _);
            this.FinishRun(active, new RowNote { Kind = outcome.Ok ? NoteKind.Good : NoteKind.Bad,
                Text = (mode == UITextJobMode.RestoreTranslations ? "撤回译文：" : "重翻译文：") + outcome.Message });
        }
        catch (OperationCanceledException) { this.FinishRun(active, new RowNote { Kind = NoteKind.Info, Text = "已取消重翻，旧译文和已保存结果保留；可从菜单继续或撤回。" }); }
        catch (Exception e) { this.FinishRun(active, new RowNote { Kind = NoteKind.Bad, Text = "译文操作已停止：" + e.GetBaseException().Message }); }
    }
}
