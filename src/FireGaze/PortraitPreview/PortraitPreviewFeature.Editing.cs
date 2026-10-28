using System.Numerics;
using Dalamud.Bindings.ImGui;
using FireGaze.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace FireGaze.PortraitPreview;

internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitAppearanceCatalog appearance = null!;
    private PortraitEditorValidation editorValidation = null!;
    private PortraitHistoryStore history = null!;
    private PortraitHistoryBatch historyBatch = null!;
    private PortraitEditorSnapshot? observedEditor;
    private PortraitEditorSnapshot? historyObservedEditor;
    private PortraitEditorSnapshot? historyLabelBaseline;
    private PortraitHistoryKey? observedKey;
    private PortraitPhotoSource? observedPhotoSource;
    private PortraitIdentity observedIdentity;
    private PortraitHistoryListItem[] historyRows = [];
    private IReadOnlyList<PortraitAppearanceGroup> appearanceGroups = [];
    private string historyStatus = "";
    private bool pointerHeld;
    private bool syncPlate;
    private bool plateMirrorClosePending;
    private string? plateMirrorCloseError;
    private bool canUndo;
    private bool canRedo;
    private DateTime nextEditorSample;
    private OwnedCharacterKey? historyOwner;
    private bool sampledPointer;
    private bool sampledKeyboard;
    private bool deferredBaselineSync;
    private string? historyWriteError;
    private bool historyBatchWriteBlocked;
    private string? historyPhotoStatus;
    private Task<bool>? historyPersistence;
    private DateTime nextStorageObservation;
    partial void RetryOtherPortraitStorage();

    public bool SyncPlate => syncPlate;
    public bool CanUndo => canUndo;
    public bool CanRedo => canRedo;
    public IReadOnlyList<PortraitHistoryListItem> History => historyRows;
    public string HistoryStatus => historyWriteError
        ?? (history.LoadError is not null ? "历史记录暂时无法读取，原文件已保留。" : null)
        ?? (history.PersistenceError is not null ? "历史记录尚未保存，请检查本地存储后重试。已有记录已保留。" : null)
        ?? historyPhotoStatus
        ?? (historyPersistence is { IsCompleted: false } ? "正在保存编辑记录…" : historyStatus);
    public IReadOnlyList<PortraitAppearanceGroup> AppearanceGroups => appearanceGroups;
    public IReadOnlyList<PortraitAppearanceGroup> OriginalAppearanceGroups
        => originalEditorSnapshot is { } original ? appearance.Build(original.Customize) : [];
    public IReadOnlyList<PortraitAppearanceGroup> ActualAppearanceGroups
        => actualCustomize is { Length: PortraitAppearanceCatalog.CustomizeLength } current ? appearance.Build(current) : [];

    public PortraitPreviewMenuState MenuState => PortraitPreviewMenuState.Create(
        editorAvailable,
        observedEditor is not null,
        target.CanBlockSave,
        IsActive,
        plateMirrorCloseError,
        OpeningAvailability);

    public void OpenPlateEditor() => Mutate(() =>
    {
        if (IsActive)
        {
            Error("请先关闭预览，再打开铭牌编辑器。");
            return;
        }

        OpenEditorWithReceipt("plate", Guid.NewGuid().ToString("D"));
    });

    public void OpenPreviewWindow()
    {
        if (IsActive)
        {
            window.IsOpen = true;
            window.BringToFront();
            return;
        }

        OpenPreview();
    }

    private void InitializeEditing(string directory)
    {
        appearance = new(Plugin.DataManager);
        editorValidation = new(Plugin.DataManager);
        history = new(Path.Combine(directory, "portrait-edit-history-v1.json"), storage: storageBudget);
        history.EnablePhotoBackedPublication();
        historyBatch = new(history);
        target.SnapshotGameBuild = Plugin.DataManager.GameData.Repositories["ffxiv"].Version;
        RefreshHistoryRows();
    }

    private PortraitHistoryKey? GetHistoryKey(PortraitIdentity identity, PortraitEditorSnapshot snapshot)
    {
        if (CurrentCharacter() is not { } owner) return null;
        var plate = snapshot.EntryPoint == (byte)AgentBannerEditorState.EditorOpenType.AdventurerPlate;
        var gearsets = RaptureGearsetModule.Instance();
        if (!target.IsCurrent(identity) || !plate && gearsets == null) return null;
        var state = (AgentBannerEditorState*)identity.State;
        var enabledIndex = state->OpenerEnabledGearsetIndex;
        if (!plate && (enabledIndex < 0 || enabledIndex >= 100)) return null;
        var source = plate ? 0 : gearsets->EnabledGearsetIndex2EntryIndex[enabledIndex];
        if (!plate && (source < 0 || source >= 100)) return null;
        var compat = $"{snapshot.Version}|{snapshot.GameBuild}|{snapshot.Customize[0]}:{snapshot.Customize[1]}:{snapshot.Customize[4]}:{snapshot.Customize[5]}";
        return new(owner.ContentID, plate ? PortraitHistoryEntryPoint.AdventurerPlate : PortraitHistoryEntryPoint.GearsetPortrait, source, compat);
    }

    private void PollEditing()
    {
        ObserveHistoryPersistence();
        if (LightingFitHistorySuppressed) return;
        if (historyBatchWriteBlocked && !FlushHistoryForSourceChange()) return;
        var owner = CurrentCharacter();
        if (historyOwner != owner)
        {
            if (historyOwner is not null) window.CloseAuxiliaryWindows();
            CancelLightDrag();
            HandleHistoryBatchResult(historyBatch.Flush());
            RequestPlateMirrorClose();
            historyOwner = owner;
            observedEditor = null;
            historyObservedEditor = null;
            observedIdentity = default;
            observedKey = null;
            observedPhotoSource = null;
            deferredBaselineSync = false;
            appearanceGroups = [];
            historyRows = [];
            RefreshHistoryRows();
        }
        else
        {
            // Restoration is independent of snapshot readiness/throttling and must never restart
            // synchronization. The target retains and validates the old owner/texture lease.
            RetryPlateMirrorClose();
        }
        // Input is observed, never injected or consumed. Poll release edges even when the snapshot
        // interval is throttled so one continuous native drag becomes one history transaction.
        var keyboard = false;
        for (var inputKey = 8; inputKey < 255; inputKey++)
            if (Plugin.Keys.IsVirtualKeyValid(inputKey) && Plugin.Keys[inputKey]) { keyboard = true; break; }
        if (!target.TryResolve(out var identity, out _, out _))
        {
            // Loading or temporary addon hiding does not end an ownership lease.
            if (observedIdentity.IsValid && target.IsCurrent(observedIdentity)) return;
            if (observedEditor is not null)
            {
                window.CloseAuxiliaryWindows();
                CancelLightDrag();
                HandleHistoryBatchResult(historyBatch.Flush());
                observedEditor = null;
                historyObservedEditor = null;
                observedIdentity = default;
                observedKey = null;
                observedPhotoSource = null;
                deferredBaselineSync = false;
                appearanceGroups = [];
                RequestPlateMirrorClose();
                RefreshHistoryRows();
            }
            else if (syncPlate) RequestPlateMirrorClose();
            return;
        }
        if (observedIdentity.IsValid && observedIdentity != identity)
        {
            window.CloseAuxiliaryWindows();
            RequestPlateMirrorClose();
        }
        var photoSource = CurrentPhotoSource();
        if (!lightDrag.IsActive && observedPhotoSource == photoSource && DateTime.UtcNow < nextEditorSample
            && sampledPointer == pointerHeld && sampledKeyboard == keyboard) return;
        sampledPointer = pointerHeld;
        sampledKeyboard = keyboard;
        nextEditorSample = DateTime.UtcNow.AddMilliseconds(50);
        var captured = target.CaptureEditorState(identity);
        if (captured is null || GetHistoryKey(identity, captured) is not { } key) return;
        ObserveLightDrag(identity,key,captured);
        if (session.IsActive) _ = session.ObserveLook(captured.Look);
        if (deferredBaselineSync && observedIdentity == identity && observedKey == key)
        {
            // Close can restore while the addon is hidden. The first ready sample is a restored
            // baseline, even if strict capture could not run during the close itself.
            if (!historyBatchWriteBlocked) historyLabelBaseline = null;
            observedEditor = captured;
            historyObservedEditor = captured;
            observedPhotoSource = photoSource;
            appearanceGroups = appearance.Build(captured.Customize);
        }
        deferredBaselineSync = false;
        if (observedIdentity != identity || observedKey != key || observedPhotoSource != photoSource)
        {
            if (observedKey is not null) window.CloseAuxiliaryWindows();
            if (!FlushHistoryForSourceChange()) return;
            observedIdentity = identity;
            observedKey = key;
            observedPhotoSource = photoSource;
            observedEditor = captured;
            historyObservedEditor = captured;
            HandleHistoryResult(history.Record(key, captured.ToJSON(), photoSource, "打开肖像"));
            appearanceGroups = appearance.Build(captured.Customize);
            RefreshHistoryRows();
        }
        else
        {
            var previous = historyObservedEditor;
            var recording = ForHistory(captured, previous);
            var changed = previous is null || !previous.SameForHistory(recording);
            // Session synchronization and photo eligibility always see the real live editor,
            // independently of which animation fields are eligible for durable history.
            observedEditor = captured;
            if (changed)
            {
                historyLabelBaseline ??= previous;
                var label = DescribeChange(historyLabelBaseline, WithoutPlaybackControls(recording, historyLabelBaseline));
                historyObservedEditor = recording;
                appearanceGroups = appearance.Build(captured.Customize);
                HandleHistoryBatchResult(historyBatch.Observe(key, recording.ToJSON(), photoSource,
                    pointerHeld || lightDrag.IsActive, keyboard, label));
            }
            else if (observedEditor is not null)
            {
                // Keep a canonical payload while playback advances; release edges still reach Batch.
                HandleHistoryBatchResult(historyBatch.AdvanceInput(pointerHeld || lightDrag.IsActive, keyboard));
            }
            HandleHistoryBatchResult(historyBatch.FlushIdle());
        }
        if (syncPlate && !target.UpdatePlateMirror(identity, captured, true))
            historyStatus = "等待本人铭牌预览加载完成";
        PublishHistoryNavigation();
    }

    private string DescribeChange(PortraitEditorSnapshot? before, PortraitEditorSnapshot after)
        => PortraitEditLabels.Describe(before, after, before is null ? null : appearance.Build(before.Customize), appearance.Build(after.Customize));

    private static PortraitEditorSnapshot ForHistory(PortraitEditorSnapshot current, PortraitEditorSnapshot? baseline)
    {
        if (baseline is null || current.Composition.Paused) return current;
        var comparison = WithoutPlaybackControls(current, baseline);
        // Playback alone is not an edit. A real camera/look/light edit must retain
        // its actual playing pose so a newly captured photo can belong to this state.
        return baseline.SameForHistory(comparison) ? comparison : current;
    }

    private static PortraitEditorSnapshot WithoutPlaybackControls(PortraitEditorSnapshot current, PortraitEditorSnapshot? baseline)
    {
        if (baseline is null || current.Composition.Paused) return current;
        var previous = baseline.Composition;
        return current with { Composition = current.Composition with
        {
            BannerTimeline = previous.BannerTimeline,
            PoseClassJob = previous.PoseClassJob,
            AnimationProgress = previous.AnimationProgress,
            Paused = previous.Paused,
        } };
    }

    private void HandleHistoryBatchResult(PortraitHistoryWriteResult? result)
    {
        if (result is not null)
            historyBatchWriteBlocked = result.Status is not (PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate);
        if (result is { Status: PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate }) historyLabelBaseline = null;
        HandleHistoryResult(result);
    }

    private bool FlushHistoryForSourceChange()
    {
        var result = historyBatch.Flush();
        HandleHistoryBatchResult(result);
        return result is null or { Status: PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate };
    }

    private void HandleHistoryResult(PortraitHistoryWriteResult? result)
    {
        if (result is null) return;
        if (result.Status == PortraitHistoryWriteStatus.Added)
        {
            historyWriteError = null;
            if (result.Persistence is not null) historyPersistence = result.Persistence;
            ObserveHistoryPersistence();
            RefreshHistoryRows();
        }
        else if (result.Status is not PortraitHistoryWriteStatus.Duplicate)
            historyWriteError = result.Status switch
            {
                PortraitHistoryWriteStatus.TooLarge => "历史记录空间不足，本次修改尚未记录。已有记录已保留。",
                PortraitHistoryWriteStatus.ReadOnlyCorrupt => "历史记录无法读取，原文件已保留。本次修改未能记录。",
                PortraitHistoryWriteStatus.InvalidSource => "照片来源无法确认，本次修改尚未记录。已有记录已保留。",
                _ => "这次编辑记录未能保存，请稍后重试。",
            };
    }

    private void ObserveHistoryPersistence()
    {
        // Receipt completion is observed without waiting on the writer or calling native code.
        // PersistenceError remains the writer's latest outcome, so a later successful full
        // snapshot retry clears a failure even if no editor operation occurs in between.
        if (historyPersistence is { IsCompleted: true }) historyPersistence = null;
        // A completed reclaim request alone is not evidence of restored capacity.
        // The shared authority reports only a real space, readability or OS lease edge.
        PollStorageObservation();
    }

    public void RetryStorage() => Mutate(RetryStorageCore);

    private void RetryStorageCore()
    {
        if (history.PersistenceError is not null) historyPersistence = history.RetryAsync();
        RetryOtherPortraitStorage();
    }

    private void RefreshHistoryRows()
    {
        var owner = CurrentCharacter();
        var records = owner is null ? [] : observedKey is { } key ? history.GetRecords(key) : history.GetRecordsForOwner(owner.Value.ContentID);
        var current = observedKey is { } active ? history.CurrentRecordID(active) : null;
        historyRows = records.Select(record => new PortraitHistoryListItem(record.ID, record.Timestamp,
            record.Label, record.ID == current)).ToArray();
        historyStatus = string.Empty;
        PublishHistoryNavigation();
    }

    private void PublishHistoryNavigation()
    {
        canUndo = observedKey is { } key && history.TryGetUndo(key, out _);
        canRedo = observedKey is { } current && history.TryGetRedo(current, out _);
    }

    private bool FlushEditing()
    {
        CancelLightDrag();
        var flushed = FlushHistoryForSourceChange();
        RequestPlateMirrorClose();
        return flushed;
    }

    private void RequestPlateMirrorClose()
    {
        syncPlate = false;
        plateMirrorClosePending = true;
        RetryPlateMirrorClose();
    }

    private bool RetryPlateMirrorClose()
    {
        if (!plateMirrorClosePending) return true;
        if (!target.ClosePlateMirror())
        {
            plateMirrorCloseError = "本地铭牌预览尚未还原，正在重试。";
            return false;
        }
        plateMirrorClosePending = false;
        if (plateMirrorCloseError is not null) SetStatus("本地铭牌预览已结束。");
        plateMirrorCloseError = null;
        return true;
    }

    public void ChangeAppearance(string groupID, int value) => Mutate(() =>
    {
        if (!CancelLightingFitForEdit()) return;
        if (!session.IsActive || !target.TryResolve(out var identity, out _, out _)) return;
        var before = target.CaptureEditorState(identity);
        if (before is null) return;
        if (!FlushHistoryForSourceChange()) return;
        if (!appearance.TryChange(before.Customize, groupID, value, out var customized, out var reason)) { Error(reason); return; }
        if (!ApplyWithFailureNotice(() => target.ApplyEditorState(identity, before with { Customize = customized }),
                "暂时无法预览此美容选项，请稍后重试。若画面异常，请结束预览。")) return;
        var selectedGroup = appearance.Build(customized).FirstOrDefault(group => group.ID == groupID);
        var selectedOption = selectedGroup?.Options.FirstOrDefault(option => option.Value == value);
        RecordDirectChange(selectedGroup is not null && selectedOption is not null
            ? $"{selectedGroup.Label}：{selectedOption.Label}" : "美容师修改");
    });

    private void RecordDirectChange(string label)
    {
        if (LightingFitHistorySuppressed) return;
        if (!target.TryResolve(out var identity, out _, out _) || target.CaptureEditorState(identity) is not { } captured
            || GetHistoryKey(identity, captured) is not { } key) return;
        if (!FlushHistoryForSourceChange()) return;
        var photoSource = CurrentPhotoSource();
        var baseline = observedIdentity == identity && observedKey == key && observedPhotoSource == photoSource ? historyObservedEditor : null;
        var recording = ForHistory(captured, baseline);
        if (baseline is not null
            && label is "清空装备部位" or "装备显示设置" or "美容师修改")
            label = DescribeChange(baseline, WithoutPlaybackControls(recording, baseline));
        observedEditor = captured;
        historyObservedEditor = recording;
        observedIdentity = identity;
        observedKey = key;
        observedPhotoSource = photoSource;
        appearanceGroups = appearance.Build(captured.Customize);
        HandleHistoryResult(history.Record(key, recording.ToJSON(), photoSource, label));
        if (syncPlate) _ = target.UpdatePlateMirror(identity, captured, true);
        RefreshHistoryRows();
    }

    public void Undo() => Mutate(() => NavigateHistory(PortraitHistorySelectionKind.Undo, 0));
    public void Redo() => Mutate(() => NavigateHistory(PortraitHistorySelectionKind.Redo, 0));
    public void RestoreHistory(long id) => Mutate(() => NavigateHistory(PortraitHistorySelectionKind.Restore, id));

    private void NavigateHistory(PortraitHistorySelectionKind kind, long id)
    {
        if (!CancelLightingFitForEdit()) return;
        CancelLightDrag();
        if (!target.TryResolve(out var identity, out _, out _)) { Error("请先打开对应肖像编辑器。"); return; }
        var before = target.CaptureEditorState(identity);
        if (before is null || GetHistoryKey(identity, before) is not { } key) return;
        if (!FlushHistoryForSourceChange()) return;
        PortraitHistorySelection selected;
        var found = kind switch
        {
            PortraitHistorySelectionKind.Undo => history.TryGetUndo(key, out selected),
            PortraitHistorySelectionKind.Redo => history.TryGetRedo(key, out selected),
            _ => history.TryGetRecord(key, id, out selected),
        };
        if (!found) { Error("当前肖像没有可恢复的对应记录。"); return; }
        var desired = PortraitEditorSnapshot.FromJSON(selected.Record.SnapshotJSON);
        if (desired is null || !editorValidation.IsValid(desired, out _) || !desired.IsCompatibleWith(before)
            || !appearance.IsValidChange(before.Customize, desired.Customize, out _))
        { Error("此历史记录不适用于当前角色或游戏版本。"); return; }
        var preparation = history.PrepareCommit(selected);
        if (preparation.Status is not (PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate))
        { HandleHistoryResult(preparation); return; }
        if (!target.CanBlockSave) { Error("暂时无法阻止肖像保存，为避免保存临时外观，本次未恢复记录。请稍后重试。"); return; }
        if (!session.IsActive && !session.Open(identity)) { Error("肖像尚未加载完成，请稍后重试。"); return; }
        window.IsOpen = true;
        if (!ApplyWithFailureNotice(() => target.ApplyEditorState(identity, desired),
                "恢复历史未完成，请稍后重试。若画面异常，请结束预览。")) return;
        var committed = kind == PortraitHistorySelectionKind.Restore ? history.CommitRestore(selected) : history.CommitNavigation(selected);
        if (committed.Status is not (PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate))
        {
            var beforeNotice = playerErrorRevision;
            _ = target.ApplyEditorState(identity, before);
            HandleHistoryResult(committed);
            ReportFailureIfUnreported(beforeNotice, "未能保存这次恢复，已尝试还原本次操作。");
            return;
        }
        HandleHistoryResult(committed);
        _ = session.ObserveLook(desired.Look);
        observedEditor = target.CaptureEditorState(identity) ?? desired;
        historyObservedEditor = ForHistory(observedEditor, desired);
        observedIdentity = identity;
        observedKey = key;
        observedPhotoSource = CurrentPhotoSource();
        appearanceGroups = appearance.Build(observedEditor.Customize);
        SetStatus(kind == PortraitHistorySelectionKind.Undo ? "已撤回上一步" : kind == PortraitHistorySelectionKind.Redo ? "已重做" : "已恢复所选时刻");
        if (syncPlate) _ = target.UpdatePlateMirror(identity, observedEditor, true);
        RefreshHistoryRows();
    }

    public void SetSyncPlate(bool enabled) => Mutate(() =>
    {
        if (!CancelLightingFitForEdit()) return;
        if (!enabled) { RequestPlateMirrorClose(); return; }
        if (!RetryPlateMirrorClose()) return;
        if (!session.IsActive || !target.TryResolve(out var identity, out _, out _)
            || target.CaptureEditorState(identity) is not { } current)
        { Error("请先在肖像编辑器旁打开 FireGaze预览。"); return; }
        syncPlate = true;
        SetStatus("肖像预览已开启。");
        if (!target.UpdatePlateMirror(identity, current, true)) SetStatus("已开启同步，请打开自己的冒险者铭牌查看本地预览。");
    });

    private void SynchronizeAfterClose()
    {
        window.CloseAuxiliaryWindows();
        CancelLightDrag();
        if (!historyBatchWriteBlocked) historyLabelBaseline = null;
        deferredBaselineSync = true;
        if (target.TryResolve(out var identity, out _, out _) && target.CaptureEditorState(identity) is { } restored)
        {
            observedEditor = restored;
            historyObservedEditor = restored;
            observedIdentity = identity;
            observedKey = GetHistoryKey(identity, restored);
            observedPhotoSource = CurrentPhotoSource();
            appearanceGroups = appearance.Build(restored.Customize);
            deferredBaselineSync = false;
        }
        PublishHistoryNavigation();
    }

    private bool SynchronizeCurrentLook()
    {
        return CancelLightingFitForEdit() && session.IsActive && target.TryResolve(out var identity, out _, out _)
            && target.CaptureEditorState(identity) is { } latest && session.ObserveLook(latest.Look);
    }

    public void DrawContent()
    {
        var menu = MenuState;
        ImGui.TextWrapped("试换装备、染色和外貌，立即查看自己的铭牌效果。结束预览后还原外观。");

        ImGui.Spacing();
        var menuStatus = DescribeMenuStatus(menu.Status);
        if (menuStatus.Length != 0 && (IsActive || !OpeningAvailability.HasBlockers
            && !OpeningAvailability.Opening && !OpeningAvailability.EditorAlreadyOpen))
        {
            UiHelpers.ColoredWrapped(menu.Status == PortraitPreviewMenuStatus.Active
                ? UiHelpers.Good : UiHelpers.Muted, menuStatus);
        }

        if (menu.RestoreWarning is not null)
            ImGui.TextWrapped(menu.RestoreWarning);
        if (!IsActive)
        {
            foreach (var issue in OpeningAvailability.Issues)
            {
                if (menu.RestoreWarning is not null && issue.Code is "RestoreFailed" or "Restoring") continue;
                UiHelpers.ColoredWrapped(issue.Code switch
                {
                    "OpeningInProgress" or "EditorLoading" or "CharacterLoading" or "ChangingArea" or "WorldTravel"
                        or "LoggingOut" or "Teleporting" or "Casting" or "Moving" or "Jumping" or "Restoring"
                        or "PhotosPending" or "PreviousOperationPending" => UiHelpers.Muted,
                    "RestoreFailed" or "SaveProtectionUnavailable" or "OpeningTimeout" or "EditorChanged"
                        or "EditorDidNotOpen" or "OpeningRequestExpired" or "OpeningUnavailable" or "StateUnavailable" => UiHelpers.Bad,
                    _ => UiHelpers.Warn,
                }, issue.Message);
            }
            if (!OpeningAvailability.HasBlockers && (OpeningAvailability.Opening || OpeningAvailability.EditorAlreadyOpen))
                ImGui.TextWrapped(OpeningAvailability.PrimaryMessage);
        }

        ImGui.Spacing();
        using (UiHelpers.Disabled(!menu.CanOpenEditor))
        {
            if (ImGui.Button("打开铭牌###PortraitOpenPlate")) OpenPlateEditor();
        }
        ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X * 1.5f);
        if (menu.CanOpenWindow)
        {
            UiHelpers.PushPrimaryButton();
            try
            {
                if (ImGui.Button("打开预览窗口###PortraitOpenPreviewWindow")) OpenPreviewWindow();
            }
            finally { UiHelpers.PopPrimaryButton(); }
        }
        else
        {
            using (UiHelpers.Disabled(!menu.CanOpenPreview))
            {
                UiHelpers.PushPrimaryButton();
                try
                {
                    if (ImGui.Button("启用预览###PortraitEnablePreview")) OpenPreviewWindow();
                }
                finally { UiHelpers.PopPrimaryButton(); }
            }
        }

        ImGui.Spacing();
    }

    private string DescribeEditorType()
    {
        if (!editorAvailable) return "未打开";
        return observedEditor?.EntryPoint switch
        {
            null => "加载中",
            (byte)AgentBannerEditorState.EditorOpenType.Portrait => "普通肖像",
            (byte)AgentBannerEditorState.EditorOpenType.Gearset => "套装",
            (byte)AgentBannerEditorState.EditorOpenType.AdventurerPlate => "铭牌",
            _ => "未知",
        };
    }

    private static string DescribeMenuStatus(PortraitPreviewMenuStatus value) => value switch
    {
        PortraitPreviewMenuStatus.Loading => "铭牌正在加载",
        PortraitPreviewMenuStatus.Active => "预览进行中",
        _ => string.Empty,
    };
}
