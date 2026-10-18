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
    private PortraitHistoryKey? observedKey;
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

    public bool SyncPlate => syncPlate;
    public bool CanUndo => canUndo;
    public bool CanRedo => canRedo;
    public IReadOnlyList<PortraitHistoryListItem> History => historyRows;
    public string HistoryStatus => historyWriteError ?? history.LoadError ?? history.PersistenceError ?? historyStatus;
    public IReadOnlyList<PortraitAppearanceGroup> AppearanceGroups => appearanceGroups;

    private void InitializeEditing(string directory)
    {
        appearance = new(Plugin.DataManager);
        editorValidation = new(Plugin.DataManager);
        history = new(Path.Combine(directory, "portrait-edit-history-v1.json"));
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
        return new(owner.AccountID, owner.ContentID, plate ? PortraitHistoryEntryPoint.AdventurerPlate : PortraitHistoryEntryPoint.GearsetPortrait, source, compat);
    }

    private void PollEditing()
    {
        var owner = CurrentCharacter();
        if (historyOwner != owner)
        {
            HandleHistoryResult(historyBatch.Flush());
            RequestPlateMirrorClose();
            historyOwner = owner;
            observedEditor = null;
            observedIdentity = default;
            observedKey = null;
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
                HandleHistoryResult(historyBatch.Flush());
                observedEditor = null;
                observedIdentity = default;
                observedKey = null;
                deferredBaselineSync = false;
                appearanceGroups = [];
                RequestPlateMirrorClose();
                RefreshHistoryRows();
            }
            else if (syncPlate) RequestPlateMirrorClose();
            return;
        }
        if (observedIdentity.IsValid && observedIdentity != identity) RequestPlateMirrorClose();
        if (DateTime.UtcNow < nextEditorSample && sampledPointer == pointerHeld && sampledKeyboard == keyboard) return;
        sampledPointer = pointerHeld;
        sampledKeyboard = keyboard;
        nextEditorSample = DateTime.UtcNow.AddMilliseconds(50);
        var captured = target.CaptureEditorState(identity);
        if (captured is null || GetHistoryKey(identity, captured) is not { } key) return;
        if (session.IsActive) _ = session.ObserveLook(captured.Look);
        if (deferredBaselineSync && observedIdentity == identity && observedKey == key)
        {
            // Close can restore while the addon is hidden. The first ready sample is a restored
            // baseline, even if strict capture could not run during the close itself.
            historyBatch.Cancel();
            observedEditor = captured;
            appearanceGroups = appearance.Build(captured.Customize);
        }
        deferredBaselineSync = false;
        if (observedIdentity != identity || observedKey != key)
        {
            HandleHistoryResult(historyBatch.Flush());
            observedIdentity = identity;
            observedKey = key;
            observedEditor = captured;
            HandleHistoryResult(history.Record(key, captured.ToJSON(), "打开肖像"));
            appearanceGroups = appearance.Build(captured.Customize);
            RefreshHistoryRows();
        }
        else
        {
            var previous = observedEditor;
            var changed = previous is null || !previous.SameForHistory(captured);
            if (changed)
            {
                var label = DescribeChange(previous, captured);
                observedEditor = captured;
                appearanceGroups = appearance.Build(captured.Customize);
                HandleHistoryResult(historyBatch.Observe(key, captured.ToJSON(), pointerHeld, keyboard, label));
            }
            else if (observedEditor is not null)
            {
                // Keep a canonical payload while playback advances; release edges still reach Batch.
                HandleHistoryResult(historyBatch.AdvanceInput(pointerHeld, keyboard));
            }
            HandleHistoryResult(historyBatch.FlushIdle());
        }
        if (syncPlate && !target.UpdatePlateMirror(identity, captured, true))
            historyStatus = "等待本人铭牌预览加载完成";
        PublishHistoryNavigation();
    }

    private static string DescribeChange(PortraitEditorSnapshot? before, PortraitEditorSnapshot after)
    {
        if (before is null) return "编辑肖像";
        if (!before.Customize.SequenceEqual(after.Customize)) return "美容师修改";
        if (!before.Look.Pieces.SequenceEqual(after.Look.Pieces)) return "换装或染色";
        if (before.Look.HeadVisible != after.Look.HeadVisible || before.Look.WeaponVisible != after.Look.WeaponVisible || before.Look.Visor != after.Look.Visor) return "装备显示设置";
        if (before.Composition.Background != after.Composition.Background || before.Composition.Frame != after.Composition.Frame || before.Composition.Decoration != after.Composition.Decoration) return "背景与装饰";
        return "姿势、镜头与灯光";
    }

    private void HandleHistoryResult(PortraitHistoryWriteResult? result)
    {
        if (result is null) return;
        if (result.Status == PortraitHistoryWriteStatus.Added) { historyWriteError = null; RefreshHistoryRows(); }
        else if (result.Status is not PortraitHistoryWriteStatus.Duplicate)
            historyWriteError = result.Status switch
            {
                PortraitHistoryWriteStatus.TooLarge => "历史记录空间不足，本次修改尚未记录。已有记录已保留。",
                PortraitHistoryWriteStatus.ReadOnlyCorrupt => "历史记录无法读取，原文件已保留。本次修改未能记录。",
                _ => "这次编辑记录未能保存，请稍后重试。",
            };
    }

    private void RefreshHistoryRows()
    {
        var owner = CurrentCharacter();
        var records = owner is null ? [] : observedKey is { } key ? history.GetRecords(key) : history.GetRecordsForOwner(owner.Value.AccountID, owner.Value.ContentID);
        var current = observedKey is { } active ? history.CurrentRecordID(active) : null;
        historyRows = records.Select(record => new PortraitHistoryListItem(record.ID, record.Timestamp,
            record.Label, record.ID == current)).ToArray();
        historyStatus = $"{history.Count} 条记录";
        PublishHistoryNavigation();
    }

    private void PublishHistoryNavigation()
    {
        canUndo = observedKey is { } key && history.TryGetUndo(key, out _);
        canRedo = observedKey is { } current && history.TryGetRedo(current, out _);
    }

    private void FlushEditing()
    {
        HandleHistoryResult(historyBatch.Flush());
        RequestPlateMirrorClose();
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
        if (plateMirrorCloseError is not null) status = "本地铭牌预览已结束。";
        plateMirrorCloseError = null;
        return true;
    }

    public void ChangeAppearance(string groupID, int value) => Mutate(() =>
    {
        if (!session.IsActive || !target.TryResolve(out var identity, out _, out _)) return;
        var before = target.CaptureEditorState(identity);
        if (before is null) return;
        HandleHistoryResult(historyBatch.Flush());
        if (!appearance.TryChange(before.Customize, groupID, value, out var customized, out var reason)) { Error(reason); return; }
        if (!target.ApplyEditorState(identity, before with { Customize = customized })) { Error(target.LastError ?? "暂时无法预览此美容选项，请稍后重试。"); return; }
        var selectedGroup = appearance.Build(customized).FirstOrDefault(group => group.ID == groupID);
        var selectedOption = selectedGroup?.Options.FirstOrDefault(option => option.Value == value);
        RecordDirectChange(selectedGroup is not null && selectedOption is not null
            ? $"{selectedGroup.Label}：{selectedOption.Label}" : "美容师修改");
    });

    private void RecordDirectChange(string label)
    {
        if (!target.TryResolve(out var identity, out _, out _) || target.CaptureEditorState(identity) is not { } captured
            || GetHistoryKey(identity, captured) is not { } key) return;
        HandleHistoryResult(historyBatch.Flush());
        observedEditor = captured;
        observedIdentity = identity;
        observedKey = key;
        appearanceGroups = appearance.Build(captured.Customize);
        HandleHistoryResult(history.Record(key, captured.ToJSON(), label));
        if (syncPlate) _ = target.UpdatePlateMirror(identity, captured, true);
        RefreshHistoryRows();
    }

    public void Undo() => Mutate(() => NavigateHistory(PortraitHistorySelectionKind.Undo, 0));
    public void Redo() => Mutate(() => NavigateHistory(PortraitHistorySelectionKind.Redo, 0));
    public void RestoreHistory(long id) => Mutate(() => NavigateHistory(PortraitHistorySelectionKind.Restore, id));

    private void NavigateHistory(PortraitHistorySelectionKind kind, long id)
    {
        if (!target.TryResolve(out var identity, out _, out _)) { Error("请先打开对应肖像编辑器。"); return; }
        var before = target.CaptureEditorState(identity);
        if (before is null || GetHistoryKey(identity, before) is not { } key) return;
        HandleHistoryResult(historyBatch.Flush());
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
        if (!target.ApplyEditorState(identity, desired)) { Error(target.LastError ?? "恢复历史失败，当前肖像已保留。"); return; }
        var committed = kind == PortraitHistorySelectionKind.Restore ? history.CommitRestore(selected) : history.CommitNavigation(selected);
        if (committed.Status is not (PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate))
        {
            _ = target.ApplyEditorState(identity, before);
            HandleHistoryResult(committed);
            Error("未能保存这次恢复，已尝试还原本次操作。");
            return;
        }
        _ = session.ObserveLook(desired.Look);
        observedEditor = target.CaptureEditorState(identity) ?? desired;
        observedIdentity = identity;
        observedKey = key;
        appearanceGroups = appearance.Build(observedEditor.Customize);
        status = kind == PortraitHistorySelectionKind.Undo ? "已撤回上一步" : kind == PortraitHistorySelectionKind.Redo ? "已重做" : "已恢复所选时刻";
        if (syncPlate) _ = target.UpdatePlateMirror(identity, observedEditor, true);
        RefreshHistoryRows();
    }

    public void SetSyncPlate(bool enabled) => Mutate(() =>
    {
        if (!enabled) { RequestPlateMirrorClose(); return; }
        if (!RetryPlateMirrorClose()) return;
        if (!session.IsActive || !target.TryResolve(out var identity, out _, out _)
            || target.CaptureEditorState(identity) is not { } current)
        { Error("请先在肖像编辑器旁打开 FireGaze修改。"); return; }
        syncPlate = true;
        status = "肖像修改已开启。";
        if (!target.UpdatePlateMirror(identity, current, true)) status = "已开启同步，请打开自己的冒险者铭牌查看本地预览。";
    });

    private void SynchronizeAfterClose()
    {
        historyBatch.Cancel();
        deferredBaselineSync = true;
        if (target.TryResolve(out var identity, out _, out _) && target.CaptureEditorState(identity) is { } restored)
        {
            observedEditor = restored;
            observedIdentity = identity;
            observedKey = GetHistoryKey(identity, restored);
            appearanceGroups = appearance.Build(restored.Customize);
            deferredBaselineSync = false;
        }
        PublishHistoryNavigation();
    }

    private bool SynchronizeCurrentLook()
    {
        return session.IsActive && target.TryResolve(out var identity, out _, out _)
            && target.CaptureEditorState(identity) is { } latest && session.ObserveLook(latest.Look);
    }

    public void DrawContent()
    {
        ImGui.TextWrapped("在装备套装肖像或冒险者铭牌编辑器旁点击「FireGaze修改」，可预览装备、染色和美容。关闭后还原临时外观。");
        ImGui.TextDisabled("仅本人可见。请先关闭 FireGaze修改，再保存肖像。");
        using (UiHelpers.Disabled(!editorAvailable))
        {
            if (ImGui.Button(IsActive ? "打开修改窗口" : "FireGaze修改")) OpenPreview();
        }
        ImGui.SameLine();
        using (UiHelpers.Disabled(!CanUndo))
        {
            if (ImGui.Button("撤回")) Undo();
        }
        ImGui.SameLine();
        using (UiHelpers.Disabled(!CanRedo))
        {
            if (ImGui.Button("重做")) Redo();
        }
        var sync = SyncPlate;
        if (ImGui.Checkbox("肖像实时同步铭牌", ref sync)) SetSyncPlate(sync);
        ImGui.TextDisabled("同步只影响本地铭牌预览，关闭后还原。");
        if (plateMirrorCloseError is not null) ImGui.TextWrapped(plateMirrorCloseError);
    }
}
