namespace FireGaze.PortraitPreview;

/// <summary>Framework-owned integration for saved glamour templates and the shared favorite library.</summary>
internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitGlamourPlateCatalog glamourPlates = null!;
    private PortraitGlamourPlateCache glamourPlateCache = null!;
    private PortraitGlamourPlateReader glamourPlateReader = null!;
    private PortraitGlamourPlateView? glamourPlateView;
    private PortraitFavoritesStore favoritesStore = null!;
    private IReadOnlyList<PortraitFavoriteEntry> favoriteEntries = [];
    private string favoritesStatus = "";
    private bool libraryInitialized;
    private DateTime nextGlamourPlateRead;
    private OwnedCharacterKey? glamourPlateOwner;
    private ulong contentID;
    private string? cachePersistenceWarning;
    private readonly Dictionary<ulong, string> retainerDisplayNames = [];

    public ulong ContentID => contentID;
    public string SourceGuideKey => ContentID == 0 ? string.Empty : ContentID.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public PortraitGlamourPlateView? GlamourPlateView => glamourPlateView;
    public IReadOnlyList<PortraitFavoriteEntry> Favorites => favoriteEntries;
    public string FavoritesStatus => favoritesStatus;

    private void InitializeLibrary(string directory)
    {
        glamourPlates = new();
        glamourPlateCache = new(directory);
        glamourPlateCache.Load(glamourPlates, out _);
        glamourPlateReader = new(() => Plugin.Framework.IsInFrameworkUpdateThread, CurrentCharacter);
        favoritesStore = new(Path.Combine(directory, "portrait-favorites-v1.json"));
        favoriteEntries = favoritesStore.Entries;
        // A missing plate cache is a normal first run and must not appear as a favorite error.
        favoritesStatus = favoritesStore.LoadError ?? string.Empty;
        libraryInitialized = true;
    }

    /// <summary>Called from the existing Framework update after inventory/editor polling.</summary>
    private void PollLibrary()
    {
        if (!libraryInitialized || !Plugin.Framework.IsInFrameworkUpdateThread) return;
        // Worker completion is read without disk or native calls. A queued snapshot is not
        // presented as durable while storage is failing, and successful retry clears the warning.
        cachePersistenceWarning = glamourPlateCache.IsReadOnly ? "投影模板记录暂时无法读取，原文件已保留。"
            : cacheWriter.LastWriteSucceeded == false ? "物品读取记录未能保存，请检查本地存储。"
            : glamourPlateCache.LastWriteSucceeded == false ? "投影模板读取记录未能保存，请检查本地存储。" : null;
        var owner = CurrentCharacter();
        contentID = owner?.ContentID ?? 0;
        if (owner is not { } character)
        {
            glamourPlates.DeactivateCharacter();
            glamourPlateView = null;
            glamourPlateOwner = null;
            return;
        }
        var changedOwner = glamourPlateOwner != character;
        if (!changedOwner && DateTime.UtcNow < nextGlamourPlateRead) return;
        if (changedOwner) retainerDisplayNames.Clear();
        nextGlamourPlateRead = DateTime.UtcNow.AddSeconds(2);
        glamourPlateOwner = character;
        glamourPlates.ActivateCharacter(character);
        var capture = glamourPlateReader.Capture();
        if (capture.Character == character && capture.Plates is not null)
        {
            glamourPlates.ApplyRead(character, DateTimeOffset.UtcNow, capture.Plates);
        }
        else if (capture.Character == character)
            glamourPlates.MarkUnavailable(character, capture.Reason);
        glamourPlateCache.QueueIfNeeded(glamourPlates);
        glamourPlateView = glamourPlates.BuildView(DateTimeOffset.UtcNow);
    }

    public void ApplyGlamourPlate(int index) => Mutate(() =>
    {
        if (index is < 0 or >= 20) { Error("投影模板编号无效。"); return; }
        if (CurrentCharacter() is not { } character || glamourPlateView?.Character != character)
        { Error("角色已改变，请等待当前角色的投影模板读取完成。"); return; }
        // Compensation can restore RGB, so capture only after the fit has fully released it.
        if (!CancelLightingFitForEdit()) return;
        if (!target.CanBlockSave || !session.IsActive || !target.TryResolve(out var identity, out _, out _)
            || target.CaptureEditorState(identity) is not { } before)
        { Error("请先打开肖像编辑器和 FireGaze预览，等待保存保护就绪。"); return; }
        var plate = glamourPlateView?.Plates.FirstOrDefault(value => value.Index == index);
        if (plate is null) { Error("这个投影模板尚未读取。"); return; }
        if (!PortraitGlamourPlateMerge.TryMerge(before.Look, plate, out var merged, out var reason))
        { Error(reason); return; }
        var desired = before with { Look = merged };
        if (!editorValidation.IsValid(desired, out reason)) { Error(reason); return; }
        ApplyLibrarySnapshot(identity, before, desired, "应用投影模板 " + (index + 1), preservePlayingHistory: true);
    });

    public string FavoriteAvailability(Guid id)
    {
        var entry = favoriteEntries.FirstOrDefault(value => value.ID == id);
        if (entry is null) return "找不到这条收藏。";
        // Draw uses detached Framework observations; never capture or resolve native state per row.
        if (!session.IsActive || observedEditor is not { } current || observedKey?.ContentID != contentID)
            return "请先打开肖像编辑器和 FireGaze预览。";
        return FavoriteCompatibility(entry, current, out _);
    }

    private string FavoriteCompatibility(PortraitFavoriteEntry entry, PortraitEditorSnapshot current,
        out PortraitEditorSnapshot desired)
    {
        desired = entry.Snapshot with { EntryPoint = current.EntryPoint };
        if (!string.Equals(entry.Snapshot.GameBuild, current.GameBuild, StringComparison.Ordinal)) return "收藏来自不同游戏版本。";
        if (entry.Snapshot.EntryPoint > 2) return "收藏的编辑器类型不受当前版本支持。";
        // Rebind only the editor entry point. Appearance validation protects every byte outside
        // the current beautician profile, including face, height, and base character model.
        if (!desired.IsCompatibleWith(current)) return "收藏的角色基础外观与当前编辑器不兼容。";
        if (!editorValidation.IsValid(desired, out var reason)) return reason;
        if (!current.Customize.SequenceEqual(desired.Customize)
            && !appearance.IsValidChange(current.Customize, desired.Customize, out reason)) return reason;
        return string.Empty;
    }

    public void SaveFavorite(string name, IReadOnlyCollection<string> tags) => Mutate(() =>
    {
        if (!TryCurrentSnapshot(out var snapshot, out var reason)) { SetFavoritesError(reason); return; }
        var owner = CurrentCharacter();
        if (owner is not { } character) { SetFavoritesError("尚未进入角色。"); return; }
        var sourceName = Plugin.Objects.LocalPlayer?.Name.TextValue;
        var result = favoritesStore.Add(snapshot, character.ContentID,
            string.IsNullOrWhiteSpace(sourceName) ? "当前角色" : sourceName, name, tags);
        PublishFavoriteResult(result);
    });

    public void RestoreFavorite(Guid id) => Mutate(() =>
    {
        var entry = favoriteEntries.FirstOrDefault(value => value.ID == id);
        if (entry is null) { SetFavoritesError("找不到这条收藏。"); return; }
        if (!TryCurrentSnapshot(out var before, out var reason)) { SetFavoritesError(reason); return; }
        var availability = FavoriteCompatibility(entry, before, out var desired);
        if (!string.IsNullOrEmpty(availability))
        { SetFavoritesError(availability); return; }
        if (!target.TryResolve(out var identity, out _, out _)) { SetFavoritesError("肖像编辑器暂时不可用。"); return; }
        if (!ApplyLibrarySnapshot(identity, before, desired, "恢复收藏：" + entry.Name)) favoritesStatus = status;
        else favoritesStatus = "已恢复收藏：" + entry.Name;
    });

    public void RenameFavorite(Guid id, string name) => Mutate(() => PublishFavoriteResult(favoritesStore.Rename(id, name)));
    public void TagFavorite(Guid id, IReadOnlyCollection<string> tags) => Mutate(() => PublishFavoriteResult(favoritesStore.SetTags(id, tags)));
    public void RemoveFavorite(Guid id) => Mutate(() => PublishFavoriteResult(favoritesStore.Remove(id)));

    private bool TryCurrentSnapshot(out PortraitEditorSnapshot snapshot, out string reason)
    {
        snapshot = null!;
        reason = "请先打开肖像编辑器和 FireGaze预览。";
        if (!CancelLightingFitForEdit()) { reason="正在结束灯光调整，请稍候。"; return false; }
        if (!session.IsActive || !target.TryResolve(out var identity, out _, out _)
            || target.CaptureEditorState(identity) is not { } current) return false;
        if (!editorValidation.IsValid(current, out reason)) return false;
        snapshot = current;
        reason = string.Empty;
        return true;
    }

    private bool ApplyLibrarySnapshot(PortraitIdentity identity, PortraitEditorSnapshot before,
        PortraitEditorSnapshot desired, string label, bool preservePlayingHistory = false)
    {
        if (!CancelLightingFitForEdit()) return false;
        if (!target.CanBlockSave || !session.IsActive || !target.IsCurrent(identity)
            || !desired.IsCompatibleWith(before) || !editorValidation.IsValid(desired, out var validation))
        { Error("肖像暂时无法安全预览，请等待编辑器和保存保护就绪。"); return false; }
        if (!before.Customize.SequenceEqual(desired.Customize)
            && !appearance.IsValidChange(before.Customize, desired.Customize, out validation))
        { Error(validation); return false; }
        if (GetHistoryKey(identity, before) is not { } key) { Error("无法确认当前肖像记录来源。"); return false; }
        HandleHistoryBatchResult(historyBatch.Flush());
        // Apply the complete live state, but persist only the settled pose when playback is
        // running. This keeps a template edit from turning an animation frame into a pose record.
        var historyDesired = preservePlayingHistory ? ForHistory(desired, historyObservedEditor) : desired;
        var payload = historyDesired.ToJSON();
        var preparation = history.PrepareRecord(key, payload, label);
        if (preparation.Status is not (PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate))
        { HandleHistoryResult(preparation); Error("这次修改无法记录，原肖像保持不变。"); return false; }
        if (!target.ApplyEditorState(identity, desired))
        { Error(target.LastError ?? "本地预览未能应用，原肖像保持不变。"); return false; }
        if (!session.ObserveLook(desired.Look))
        { RollbackLibrarySnapshot(identity, before, "预览状态无法确认，已取消本次修改。"); return false; }
        var result = history.Record(key, payload, label);
        if (result.Status is not (PortraitHistoryWriteStatus.Added or PortraitHistoryWriteStatus.Duplicate))
        {
            HandleHistoryResult(result);
            RollbackLibrarySnapshot(identity, before, "这次修改未能记录，已取消本次修改。");
            return false;
        }
        observedEditor = desired.Copy();
        historyObservedEditor = historyDesired.Copy();
        observedIdentity = identity;
        observedKey = key;
        appearanceGroups = appearance.Build(desired.Customize);
        HandleHistoryResult(result);
        RefreshHistoryRows();
        if (syncPlate) _ = target.UpdatePlateMirror(identity, desired, true);
        status = label;
        return true;
    }

    private void RollbackLibrarySnapshot(PortraitIdentity identity, PortraitEditorSnapshot before, string message)
    {
        var restored = target.IsCurrent(identity) && target.ApplyEditorState(identity, before);
        if (restored)
        {
            _ = session.ObserveLook(before.Look);
            observedEditor = before.Copy();
            historyObservedEditor = before.Copy();
            appearanceGroups = appearance.Build(before.Customize);
            if (syncPlate) _ = target.UpdatePlateMirror(identity, before, true);
        }
        Error(restored ? message : "本次修改未完成，还原状态无法确认，请关闭后重新打开肖像编辑器。");
    }

    private void SetFavoritesError(string reason)
    {
        favoritesStatus = reason;
        status = reason;
    }

    private void PublishFavoriteResult(PortraitFavoritesWriteResult result)
    {
        if (result.Success)
        {
            favoriteEntries = favoritesStore.Entries;
            favoritesStatus = "收藏已保存";
        }
        else SetFavoritesError(result.Reason);
    }

    private void DisposeLibrary()
    {
        if (!libraryInitialized) return;
        glamourPlateCache.QueueIfNeeded(glamourPlates);
        glamourPlateCache.FlushAsync().GetAwaiter().GetResult();
        glamourPlateCache.Dispose();
        libraryInitialized = false;
    }

    public IReadOnlyList<OwnedSourceView> Sources => ownedView.Sources;

    /// <summary>Every accepted inventory capture batch uses the same revision-aware persistence gate.</summary>
    private bool QueueOwnedCacheIfNeeded() => ownedSavePolicy.QueueIfNeeded(owned);

    public IReadOnlyList<string> StorageLocations(uint id)
        => ownedView.Items.FirstOrDefault(item => item.ItemID == id)?.Variants
            .Select(variant => SourceName(variant.Source)).Distinct(StringComparer.Ordinal).ToArray() ?? [];

    public void EquipFromSources(PortraitSlot slot, uint id, IReadOnlyCollection<OwnedSourceKind> sources) => Mutate(() =>
    {
        if (slot == PortraitSlot.Facewear) { Equip(slot, id); return; }
        if (CurrentCharacter() is not { } character || ownedView.Character != character)
        { Error("角色已改变，请等待当前角色的装备来源读取完成。"); return; }
        if (!SynchronizeCurrentLook()) return;
        var item = GetItems(slot).FirstOrDefault(row => row.ID == id);
        var variant = ownedView.Items.FirstOrDefault(row => row.ItemID == id)?.Variants
            .Where(value => sources.Count == 0 || sources.Contains(value.Source.Kind)).FirstOrDefault();
        if (item is not { Available: true } || variant is null) { Error("尚未在选择的来源中读取此装备。"); return; }
        var piece = new PortraitPiece(id, item.DyeChannels > 0 ? variant.Stain1 : (byte)0, item.DyeChannels > 1 ? variant.Stain2 : (byte)0);
        HandleHistoryResult(historyBatch.Flush());
        if (!session.Replace(slot, piece)) Error("暂时无法预览此装备，请稍后重试。");
        else RecordDirectChange("更换装备：" + item.Name);
    });

    private string SourceName(OwnedSourceKey source) => source.Kind switch
    {
        OwnedSourceKind.Equipped => "身上装备", OwnedSourceKind.Inventory => "背包", OwnedSourceKind.Armoury => "兵装库",
        OwnedSourceKind.Saddlebag => "陆行鸟鞍囊", OwnedSourceKind.PremiumSaddlebag => "额外鞍囊",
        OwnedSourceKind.GlamourDresser => "投影台", OwnedSourceKind.Armoire => "收藏柜",
        OwnedSourceKind.RetainerInventory => retainerDisplayNames.TryGetValue(source.RetainerID, out var name) ? "雇员背包 · " + name : "雇员背包",
        OwnedSourceKind.RetainerEquipped => retainerDisplayNames.TryGetValue(source.RetainerID, out var name) ? "雇员装备 · " + name : "雇员装备", _ => "物品来源",
    };
}
