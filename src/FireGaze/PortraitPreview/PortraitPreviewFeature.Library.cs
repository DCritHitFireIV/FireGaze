namespace FireGaze.PortraitPreview;

/// <summary>Framework-owned integration for saved glamour templates and the shared favorite library.</summary>
internal sealed unsafe partial class PortraitPreviewFeature
{
    private PortraitGlamourPlateCatalog glamourPlates = null!;
    private PortraitGlamourPlateCache glamourPlateCache = null!;
    private PortraitGlamourPlateReader glamourPlateReader = null!;
    private PortraitGlamourPlateView? glamourPlateView;
    private PortraitFavoritesStore favoritesStore = null!;
    private PortraitFavoritesTrashStore favoritesTrashStore = null!;
    private IReadOnlyList<PortraitFavoriteEntry> favoriteEntries = [];
    private IReadOnlyList<PortraitFavoriteEntry> favoriteTrashEntries = [];
    private string favoritesStatus = "";
    private string favoriteSuccessMessage = "收藏已保存";
    private bool libraryInitialized;
    private DateTime nextGlamourPlateRead;
    private OwnedCharacterKey? glamourPlateOwner;
    private ulong contentID;
    private string? cachePersistenceWarning;
    private Task<bool>? favoritePersistence;
    private volatile bool favoriteMetadataSucceeded = true;
    private PortraitFavoritesWriteResult? favoriteCompletedResult;
    private PortraitFavoritesWriteResult? lastFavoriteStorageFailure;
    private Func<PortraitFavoritesWriteResult>? retainedFavoriteWrite;
    private (ulong ContentID, PortraitEditorSnapshot Snapshot)? retainedFavoriteProtection;
    private Task<bool>? storageObservation;
    private bool storageObservationClosing;
    private readonly Dictionary<ulong, string> retainerDisplayNames = [];
    private OwnedRetainerRoster? retainerRosterView;
    private OwnedCharacterKey? sourceCharacter;

    // Publish before the rest of each Framework update, independently of the throttled
    // inventory capture. Drawing must not resolve PlayerState or the local actor.
    private void RefreshSourceCharacter()
    {
        sourceCharacter = null;
        sourceCharacter = CurrentCharacter();
    }

    private bool SourceOwnerCurrent => sourceCharacter is { } owner && ownedView.Character == owner;

    public ulong ContentID => contentID;
    public string SourceGuideKey => ContentID == 0 ? string.Empty : ContentID.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public PortraitGlamourPlateView? GlamourPlateView => glamourPlateView;
    public IReadOnlyList<PortraitFavoriteEntry> Favorites => favoriteEntries;
    public IReadOnlyList<PortraitFavoriteEntry> FavoriteTrash => favoriteTrashEntries;
    public string FavoritesStatus => favoritesStatus;

    private void InitializeLibrary(string directory)
    {
        glamourPlates = new();
        glamourPlateCache = new(directory, storage: storageBudget);
        glamourPlateCache.Load(glamourPlates, out _);
        glamourPlateReader = new(() => Plugin.Framework.IsInFrameworkUpdateThread, CurrentCharacter);
        favoritesStore = new(Path.Combine(directory, "portrait-favorites-v1.json"), storage: storageBudget);
        favoritesTrashStore = new(Path.Combine(directory, "portrait-favorites-trash-v1.json"), storage: storageBudget);
        PublishFavoriteEntries();
        // A missing plate cache is a normal first run and must not appear as a favorite error.
        favoritesStatus = favoritesStore.LoadError is null && favoritesTrashStore.LoadError is null ? string.Empty : "收藏文件暂时无法读取，原文件已保留。";
        libraryInitialized = true;
    }

    /// <summary>Called from the existing Framework update after inventory/editor polling.</summary>
    private void PollLibrary()
    {
        if (!libraryInitialized || !Plugin.Framework.IsInFrameworkUpdateThread) return;
        ObserveFavoritePersistence();
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
        if (favoritesStore.Entries.Concat(favoritesTrashStore.Entries).DistinctBy(entry => entry.ID).Count() >= PortraitFavoritesStore.MaximumEntries)
        { SetFavoritesError("收藏容量已满，请先清理回收站中的部分收藏。已有收藏已保留。"); return; }
        if (!TryCurrentSnapshot(out var snapshot, out var reason)) { SetFavoritesError(reason); return; }
        var owner = CurrentCharacter();
        if (owner is not { } character) { SetFavoritesError("尚未进入角色。"); return; }
        var sourceName = Plugin.Objects.LocalPlayer?.Name.TextValue;
        // Protect an existing completed photo before metadata can make it an intentional save.
        // The token spans durable metadata, archive synchronization and permanent pin refresh.
        var savedTags = tags.ToArray();
        QueueFavoriteWrite(() => favoritesStore.Add(snapshot, character.ContentID,
            string.IsNullOrWhiteSpace(sourceName) ? "当前角色" : sourceName, name, savedTags), (character.ContentID, snapshot));
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

    public void RenameFavorite(Guid id, string name) => Mutate(() =>
    {
        if (!favoriteEntries.Any(entry => entry.ID == id)) { SetFavoritesError("这条收藏已移入回收站，请先恢复后再编辑。"); return; }
        QueueFavoriteWrite(() => favoritesTrashStore.Find(id) is null ? favoritesStore.Rename(id, name)
            : new(PortraitFavoritesWriteStatus.NotFound));
    });
    public void TagFavorite(Guid id, IReadOnlyCollection<string> tags) => Mutate(() =>
    {
        if (!favoriteEntries.Any(entry => entry.ID == id)) { SetFavoritesError("这条收藏已移入回收站，请先恢复后再编辑。"); return; }
        var savedTags = tags.ToArray();
        QueueFavoriteWrite(() => favoritesTrashStore.Find(id) is null ? favoritesStore.SetTags(id, savedTags)
            : new(PortraitFavoritesWriteStatus.NotFound));
    });
    public void EditFavorite(Guid id, string name, IReadOnlyCollection<string> tags) => Mutate(() =>
    {
        if (!favoriteEntries.Any(entry => entry.ID == id)) { SetFavoritesError("这条收藏已移入回收站，请先恢复后再编辑。"); return; }
        var savedTags = tags.ToArray();
        QueueFavoriteWrite(() => favoritesTrashStore.Find(id) is null ? favoritesStore.UpdateMetadata(id, name, savedTags)
            : new(PortraitFavoritesWriteStatus.NotFound));
    });
    public void RemoveFavorite(Guid id) => Mutate(() =>
    {
        var entry = favoriteEntries.FirstOrDefault(value => value.ID == id);
        if (entry is null) { SetFavoritesError("找不到这条收藏。"); return; }
        // The tombstone is the atomic delete authority. Retaining the source snapshot in
        // the compatible v1 library avoids a two-file move losing data on interruption.
        QueueFavoriteWrite(() =>
        {
            var latest = favoritesStore.Entries.FirstOrDefault(value => value.ID == id);
            return latest is null ? new(PortraitFavoritesWriteStatus.NotFound) : favoritesTrashStore.Add(latest);
        }, successMessage: "已移入回收站，可在回收站中恢复。");
    });

    public void RestoreDeletedFavorite(Guid id) => Mutate(() =>
    {
        var entry = favoriteTrashEntries.FirstOrDefault(value => value.ID == id);
        if (entry is null) { SetFavoritesError("找不到这条回收站记录。"); return; }
        QueueFavoriteWrite(() =>
        {
            var restored = favoritesStore.AddExisting(entry);
            if (!restored.Success) return restored;
            var removed = favoritesTrashStore.Remove(id);
            if (removed.Success) return restored;
            return removed;
        }, successMessage: "已恢复收藏。");
    });

    public void PermanentlyRemoveFavorite(Guid id) => Mutate(() => QueueFavoriteWrite(() =>
    {
        // Keep the tombstone and its complete snapshot until the old source has been
        // removed durably. Failure leaves a recoverable item instead of resurrecting it.
        if (favoritesTrashStore.Find(id) is null)
            return new(PortraitFavoritesWriteStatus.NotFound, "这条回收站记录已不存在。");
        if (favoritesStore.Entries.Any(entry => entry.ID == id))
        {
            var removed = favoritesStore.Remove(id);
            if (!removed.Success) return removed;
        }
        return favoritesTrashStore.Remove(id);
    }, successMessage: "已永久删除收藏。"));

    private void QueueFavoriteWrite(Func<PortraitFavoritesWriteResult> write,
        (ulong ContentID, PortraitEditorSnapshot Snapshot)? protection = null, string successMessage = "收藏已保存")
    {
        ObserveFavoritePersistence();
        if (favoritesStore.IsReadOnly || favoritesTrashStore.IsReadOnly)
        { SetFavoritesError("收藏或回收站暂时无法读取，原文件已保留。请修复本地存储后再试。"); return; }
        if (!libraryInitialized || favoritePersistence is { IsCompleted: false })
        {
            SetFavoritesError("上一条收藏仍在保存，请稍候再试。");
            return;
        }
        retainedFavoriteWrite = write;
        favoriteSuccessMessage = successMessage;
        retainedFavoriteProtection = protection;
        favoriteCompletedResult = null;
        favoriteMetadataSucceeded = false;
        favoritesStatus = "正在保存收藏参数…";
        favoritePersistence = photos.PersistFavoriteAsync(
            () => PortraitFavoritePersistence.RunAsync(() => WriteFavoriteWithReclaim(write)).ContinueWith(task =>
            {
                var result = task.GetAwaiter().GetResult();
                favoriteCompletedResult = result;
                favoriteMetadataSucceeded = result.Success;
                return result.Success;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default),
            () => FavoriteSnapshotsForPhotos(), protection);
    }

    private void ObserveFavoritePersistence()
    {
        if (favoritePersistence is not { IsCompleted: true } || favoriteCompletedResult is not { } result) return;
        favoriteCompletedResult = null;
        PublishFavoriteResult(result);
        if (result.Success)
        {
            retainedFavoriteWrite = null;
            retainedFavoriteProtection = null;
        }
    }

    private void RetryFavoritePersistence()
    {
        ObserveFavoritePersistence();
        if (favoritePersistence is { IsCompleted: false } || retainedFavoriteWrite is not { } write) return;
        var protection = retainedFavoriteProtection;
        retainedFavoriteProtection = null;
        QueueFavoriteWrite(write, protection, favoriteSuccessMessage);
    }

    private Task<bool> DrainFavoritePersistence() => favoritePersistence?.ContinueWith(task =>
    {
        _ = task.GetAwaiter().GetResult();
        // The photo service owns archive recovery and its final-state drain. This receipt
        // represents metadata only, while still waiting for the entire accepted action.
        return favoriteMetadataSucceeded;
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default) ?? Task.FromResult(true);

    private PortraitFavoritesWriteResult WriteFavoriteWithReclaim(Func<PortraitFavoritesWriteResult> write)
    {
        PortraitFavoritesWriteResult result = null!;
        // Every store attempt returns its lock and reservation before the next oldest photo
        // is reclaimed. The shared helper stops at the first success or lack of real progress.
        photos.TryWithStorageReclaim(() => (result = write()).Success,
            () => result.Status == PortraitFavoritesWriteStatus.TooLarge && storageBudget.NeedsReclaim);
        return result;
    }

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
        if (!ApplyWithFailureNotice(() => target.ApplyEditorState(identity, desired),
                "本次造型未能应用，请稍后重试。若画面异常，请结束预览。")) return false;
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
        SetStatus(label);
        return true;
    }

    private void RollbackLibrarySnapshot(PortraitIdentity identity, PortraitEditorSnapshot before, string message)
    {
        var beforeNotice = playerErrorRevision;
        var restored = target.IsCurrent(identity) && target.ApplyEditorState(identity, before);
        if (restored)
        {
            _ = session.ObserveLook(before.Look);
            observedEditor = before.Copy();
            historyObservedEditor = before.Copy();
            appearanceGroups = appearance.Build(before.Customize);
            if (syncPlate) _ = target.UpdatePlateMirror(identity, before, true);
        }
        ReportFailureIfUnreported(beforeNotice,
            restored ? message : "本次修改未完成，还原状态无法确认，请关闭后重新打开肖像编辑器。");
    }

    private void SetFavoritesError(string reason)
    {
        favoritesStatus = reason;
        status = reason;
        statusIsError = true;
    }

    private void PublishFavoriteResult(PortraitFavoritesWriteResult result)
    {
        if (result.Success)
        {
            PublishFavoriteEntries();
            favoritesStatus = favoriteSuccessMessage;
            if (favoritePersistence is { IsCompletedSuccessfully: true, Result: false })
                favoritesStatus = "收藏已保存，照片暂时无法保存。";
        }
        else
        {
            // Storage details can include operating-system messages and local paths.
            // Keep the detached evidence for diagnostics and map only structured codes to UI.
            lastFavoriteStorageFailure = result with { Entry = null };
            SetFavoritesError(result.Status switch
            {
                PortraitFavoritesWriteStatus.InvalidInput => "收藏未能保存，请检查名称、标签和肖像内容后重试。",
                PortraitFavoritesWriteStatus.NotFound => "这条收藏已不存在，请刷新收藏列表后重试。",
                PortraitFavoritesWriteStatus.ReadOnlyCorrupt => "收藏文件暂时无法读取，原文件已保留。",
                PortraitFavoritesWriteStatus.TooLarge when result.StorageCode == PortraitStorageCode.Capacity
                    => "本地肖像存储空间不足，收藏未能保存。已有收藏已保留。",
                PortraitFavoritesWriteStatus.TooLarge => "收藏容量已满，本次收藏未能保存。已有收藏已保留。",
                PortraitFavoritesWriteStatus.PersistenceFailed when result.StorageCode == PortraitStorageCode.Busy
                    => "其他肖像数据正在保存，请稍候重试。",
                PortraitFavoritesWriteStatus.Conflict => "收藏已被其他操作更新，请刷新收藏列表后重试。",
                _ => "收藏未能保存，请检查本地存储后重试。已有收藏已保留。",
            });
        }
    }

    private IEnumerable<(ulong ContentID, PortraitEditorSnapshot Snapshot)> FavoriteSnapshotsForPhotos()
        => favoritesStore.Entries.Concat(favoritesTrashStore.Entries)
            .DistinctBy(entry => entry.ID).Select(entry => (entry.SourceContentID, entry.Snapshot));

    private void PublishFavoriteEntries()
    {
        favoriteTrashEntries = favoritesTrashStore.Entries;
        var removed = favoriteTrashEntries.Select(entry => entry.ID).ToHashSet();
        favoriteEntries = favoritesStore.Entries.Where(entry => !removed.Contains(entry.ID)).ToArray();
    }

    private void DisposeLibrary()
    {
        if (!libraryInitialized) return;
        glamourPlateCache.QueueIfNeeded(glamourPlates);
        // Seal the writer here; the parent lifetime awaits the managed drain after the
        // Framework-owned native release has returned.
        glamourPlateCache.BeginDispose();
        libraryInitialized = false;
    }

    public IReadOnlyList<PortraitBrowserItem> GetItems(PortraitSlot slot) => SourceOwnerCurrent && browser.TryGetValue(slot, out var values) ? values : [];

    public IReadOnlyList<OwnedSourceView> Sources => SourceOwnerCurrent ? ownedView.Sources : [];
    public IReadOnlyList<OwnedEquipmentEntry> EquippedItems => SourceOwnerCurrent
        ? ownedView.Items.Where(item => item.Variants.Any(variant => variant.Source.Kind == OwnedSourceKind.Equipped)).ToArray()
        : [];
    public OwnedCharacterKey? SourceCharacter => sourceCharacter;
    public OwnedRetainerRoster? RetainerRoster => sourceCharacter is { } owner && retainerRosterView?.Character == owner ? retainerRosterView : null;

    /// <summary>Every accepted inventory capture batch uses the same revision-aware persistence gate.</summary>
    private bool QueueOwnedCacheIfNeeded()
    {
        // Inventory capture is also a storage recovery edge. This keeps an unchanged
        // observation from becoming an implicit retry loop while still retrying the retained
        // snapshot once the shared authority has verified the previous obstruction is gone.
        PollStorageObservation();
        return ownedSavePolicy.QueueIfNeeded(owned);
    }

    private void PollStorageObservation()
    {
        if (storageObservation is { IsCompleted: true } completed)
        {
            storageObservation = null;
            if (completed.IsCompletedSuccessfully && completed.Result) RetryStorageCore();
        }
        if (storageObservationClosing || storageObservation is not null || DateTime.UtcNow < nextStorageObservation) return;
        nextStorageObservation = DateTime.UtcNow.AddSeconds(2);
        storageObservation = Task.Run(storageBudget.RefreshObservation);
    }

    /// <summary>Retries retained storage work only after the shared authority reports recovery or an explicit user retry.</summary>
    partial void RetryOtherPortraitStorage()
    {
        _ = cacheWriter.RetryAsync();
        _ = glamourPlateCache.RetryAsync();
        RetryFavoritePersistence();
    }

    public IReadOnlyList<string> StorageLocations(uint id)
        => SourceOwnerCurrent ? ownedView.Items.FirstOrDefault(item => item.ItemID == id)?.Variants
            .Select(variant => SourceName(variant.Source)).Distinct(StringComparer.Ordinal).ToArray() ?? [] : [];

    public void EquipFromSources(PortraitSlot slot, uint id, IReadOnlyCollection<OwnedSourceKind> sources) => Mutate(() =>
    {
        if (CurrentCharacter() is not { } character || ownedView.Character != character)
        { Error("角色已改变，请等待当前角色的装备来源读取完成。"); return; }
        if (slot == PortraitSlot.Facewear) { Equip(slot, id); return; }
        if (!SynchronizeCurrentLook()) return;
        var item = GetItems(slot).FirstOrDefault(row => row.ID == id);
        var variant = ownedView.Items.FirstOrDefault(row => row.ItemID == id)?.Variants
            .Where(value => sources.Count == 0 || sources.Contains(value.Source.Kind)).FirstOrDefault();
        if (item is not { Available: true } || variant is null) { Error("尚未在选择的来源中读取此装备。"); return; }
        var piece = new PortraitPiece(id, item.DyeChannels > 0 ? variant.Stain1 : (byte)0, item.DyeChannels > 1 ? variant.Stain2 : (byte)0);
        HandleHistoryResult(historyBatch.Flush());
        if (ApplyWithFailureNotice(() => session.Replace(slot, piece), "暂时无法预览此装备，请稍后重试。"))
            RecordDirectChange("更换装备：" + item.Name);
    });

    private string SourceName(OwnedSourceKey source) => source.Kind switch
    {
        OwnedSourceKind.Equipped => "身上装备", OwnedSourceKind.Inventory => "背包", OwnedSourceKind.Armoury => "兵装库",
        OwnedSourceKind.Saddlebag => "陆行鸟鞍囊", OwnedSourceKind.PremiumSaddlebag => "额外鞍囊",
        OwnedSourceKind.GlamourDresser => "投影台", OwnedSourceKind.Armoire => "收藏柜",
        OwnedSourceKind.RetainerInventory => retainerDisplayNames.TryGetValue(source.RetainerID, out var name) ? "雇员背包 · " + name : "雇员背包",
        _ => "物品来源",
    };
}

internal static class PortraitFavoritePersistence
{
    internal static async Task<PortraitFavoritesWriteResult> RunAsync(Func<PortraitFavoritesWriteResult> write)
    {
        try
        {
            var result = write();
            for (var attempt = 0; attempt < 3
                && result.StorageCode is PortraitStorageCode.Busy or PortraitStorageCode.StorageError; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt)).ConfigureAwait(false);
                result = write();
            }
            return result;
        }
        catch (Exception error)
        { return new(PortraitFavoritesWriteStatus.PersistenceFailed, "收藏写盘失败：" + error.Message); }
    }
}
