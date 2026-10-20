using System.Numerics;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace FireGaze.PortraitPreview;

/// <summary>
/// Disposable visual indexes for history. Native state is only sampled on Framework; the UI
/// composes owned pixel copies, and a single background writer owns all cache I/O.
/// Browsing never restores a history state or changes the currently edited portrait.
/// </summary>
internal sealed class PortraitHistoryPhotos : IDisposable
{
    private static readonly Guid Png = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    private readonly object sync = new();
    private readonly object favoriteSynchronization = new();
    private readonly ITextureProvider textures;
    private readonly ITextureReadbackProvider readback;
    private readonly PortraitThumbnailSource source;
    private readonly PortraitThumbnailCache cache;
    private readonly PortraitFavoritePhotos favoritePhotos;
    private readonly PortraitStorageBudget storage;
    private readonly Func<TimeSpan, CancellationToken, Task> retryDelay;
    private HashSet<string> favoriteKeys = new(StringComparer.Ordinal);
    private bool favoriteReferencesKnown;
    private HashSet<string> unarchivedFavoriteKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> provisionalFavoriteKeys = new(StringComparer.Ordinal);
    private bool favoriteSynchronizationNeeded;
    private readonly PortraitThumbnailCaptureGate gate = new();
    private readonly PortraitThumbnailTextureCache<IDalamudTextureWrap> loaded;
    private readonly PortraitPhotoTrace photoTrace;
    private PortraitPhotoTrace.Operation photoOperation = PortraitPhotoTrace.Operation.Silent;
    private readonly CancellationTokenSource lifetime = new();
    private readonly PortraitCaptureRetirement physical = new();
    private IDisposable? captureLifetime;
    private IDisposable? encodingLifetime;
    private Task? physicalDrain;
    private readonly Dictionary<long, (string Key, PortraitEditorSnapshot? Snapshot)> records = [];
    private Task<PortraitThumbnailLayers?>? capture;
    private PortraitThumbnailCaptureTicket? ticket;
    private Task<byte[]>? encoding;
    private Task<bool>? writing;
    private (string Key, byte[] Bytes)? retainedPng;
    private string? writingKey;
    private bool retryPngRequested;
    private string? pngWriteError;
    private PortraitStorageCode pngWriteCode;
    private Task? maintenance;
    private Task<bool>? favoriteSynchronizationTask;
    private bool favoriteMetadataFailed;
    private IDisposable? retainedFavoriteProtection;
    private (string Key, byte[] Bytes)? retainedFavoriteImage;
    private int availableBytes;
    private bool maintenanceNeeded;
    private long maintenanceRequest;
    private long attemptedMaintenance = -1;
    private long attemptedStorageChange = -1;
    private long scheduledReclaimVersion;
    private int maintenanceAttempts;
    private string? maintenanceError;
    private HashSet<string> imageKeys = [];
    private int cacheBytes;
    private string? cacheError;
    private bool readyToCompose;
    private bool disposed;
    private string? lastError;
    private long saved;
    private long discarded;
    private Task? managedDrain;

    public PortraitHistoryPhotos(string path, ITextureProvider textures, ITextureReadbackProvider readback,
        PortraitThumbnailSource source, int historyBytes, Action<string>? logger = null, string? favoritePhotoPath = null,
        IEnumerable<(ulong ContentID, PortraitEditorSnapshot Snapshot)>? favoriteSnapshots = null,
        Func<TimeSpan, CancellationToken, Task>? retryDelay = null,
        PortraitStorageBudget? storage = null)
    {
        photoTrace = new(logger);
        this.textures = textures;
        this.readback = readback;
        this.source = source;
        this.storage = storage ?? PortraitStorageBudget.ForDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        this.retryDelay = retryDelay ?? Task.Delay;
        availableBytes = Math.Max(0, PortraitThumbnailCache.MaximumBytes - historyBytes);
        // On the first upgrade, rescue known favorites before shrinking the old disposable
        // cache. Otherwise an already completed image could be evicted before being archived.
        cache = new(path, PortraitThumbnailCache.MaximumBytes, this.storage);
        cache.SetProtectedKeys(null);
        favoritePhotos = new(favoritePhotoPath ?? Path.ChangeExtension(path, ".favorites.json"), storage: this.storage);
        if (favoriteSnapshots is not null)
        {
            SetFavoriteSnapshots(favoriteSnapshots);
            maintenanceNeeded |= !cache.SetAvailableBytes(availableBytes);
        }
        PublishCacheMetadata();
        loaded = new(async key =>
        {
            var operation = photoTrace.Start(PortraitPhotoOperation.LoadedImage);
            byte[] image = [];
            if (!operation.Call(PortraitPhotoPhase.LoadedImageRead,
                    () => favoritePhotos.TryGet(key, out image) || cache.TryGet(key, out image))
                || !PortraitThumbnailImage.IsSafeSize(image)) return null;
            var result = await operation.CallAsync(PortraitPhotoPhase.LoadedImageUpload,
                () => textures.CreateFromImageAsync(image, "FireGaze history thumbnail", CancellationToken.None)).ConfigureAwait(false);
            if (result.Width == 160 && result.Height == 256) return result;
            PortraitCaptureCleanupException.DisposeAll(result);
            return null;
        });
    }

    public object Diagnostics()
    {
        lock (sync) return new { count = imageKeys.Count, bytes = cacheBytes, availableBytes, loaded = loaded.Count,
            loading = loaded.InFlight, capturing = capture is not null || encoding is not null || writing is not null,
            saved, discarded, favoritePhotoCount = favoritePhotos.Count,
            favoritePhotoError = favoritePhotos.LastError, lastError = lastError ?? maintenanceError ?? cacheError,
            maintenancePending = maintenanceNeeded, maintenanceAttempts, favoritePhotoPending = favoriteSynchronizationNeeded,
            managedFavoritesPending = favoriteSynchronizationTask is { IsCompleted: false },
            physicalPending = physical.Pending, physicalRetiring = physicalDrain is { IsCompleted: false },
            cleanupFailures = physical.CleanupFailures + loaded.CleanupFailures,
            cleanupFailureType = physical.LastCleanupFailureType ?? loaded.LastCleanupFailureType,
            phaseTrace = photoOperation.Snapshot };
    }

    // This route must remain independent of the service lock: a public provider
    // can still be in its synchronous prefix while Framework owns that lock.
    public PortraitPhotoStageSnapshot[] ReadCaptureProgress() => Volatile.Read(ref photoOperation).Snapshot;

    public bool NeedsStateValidation
    {
        get { lock (sync) return capture?.IsCompleted == true || encoding?.IsCompleted == true; }
    }

    public bool IsBusy
    {
        get { lock (sync) return capture is not null || encoding is not null || writing is not null || maintenance is not null
            || favoriteSynchronizationTask is { IsCompleted: false }; }
    }

    public bool HasStorageError { get { lock (sync) return pngWriteError is not null || maintenanceError is not null
        || cacheError is not null || favoritePhotos.LastError is not null; } }

    public void RetryStorage()
    {
        lock (sync)
        {
            if (disposed) return;
            maintenanceNeeded = true;
            maintenanceRequest++;
            retryPngRequested = retainedPng is not null;
        }
    }

    // A completed logical work slot can still own late provider cleanup.
    internal bool IsPhysicallyIdle => physical.Pending == 0 && physical.CleanupFailures == 0
        && loaded.InFlight == 0 && loaded.CleanupFailures == 0;

    private void PublishCacheMetadata()
    {
        Volatile.Write(ref imageKeys, cache.Keys.ToHashSet(StringComparer.Ordinal));
        Volatile.Write(ref cacheBytes, cache.Bytes);
        cacheError = cache.LastError;
    }

    public void SetRecords(IReadOnlyList<PortraitHistoryRecord> values, int historyBytes)
    {
        lock (sync)
        {
            if (disposed) return;
            var ids = values.Select(value => value.ID).ToHashSet();
            foreach (var id in records.Keys.Where(id => !ids.Contains(id)).ToArray()) records.Remove(id);
            foreach (var record in values)
                if (!records.ContainsKey(record.ID))
                    records[record.ID] = (PortraitThumbnailCache.KeyForSnapshot(record.Key.ContentID.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + record.SnapshotJSON), PortraitEditorSnapshot.FromJSON(record.SnapshotJSON));
            availableBytes = Math.Max(0, PortraitThumbnailCache.MaximumBytes - historyBytes);
            maintenanceNeeded = true;
            maintenanceRequest++;
        }
    }

    // Called after normal history sampling, so pending native gestures never get a premature image.
    public void Poll(string owner, PortraitIdentity identity, long? currentID, PortraitEditorSnapshot? snapshot, bool ready)
    {
        lock (sync)
        {
            if (disposed) return;
            if (maintenance?.IsCompleted == true)
            {
                if (maintenance.IsFaulted) lastError = maintenance.Exception?.GetBaseException().Message;
                maintenance = null;
            }
            var reclaimVersion = storage.ReclaimRequestVersion;
            if (storage.NeedsReclaim && reclaimVersion != scheduledReclaimVersion)
            {
                scheduledReclaimVersion = reclaimVersion;
                maintenanceNeeded = true;
                maintenanceRequest++;
            }
            if (maintenanceNeeded && maintenance is null && writing is null
                && (attemptedMaintenance != maintenanceRequest || attemptedStorageChange != storage.ChangeVersion))
            {
                var budget = availableBytes;
                var request = maintenanceRequest;
                attemptedMaintenance = request;
                // Keep other characters' disposable photos; their history is not in the current UI.
                maintenance = Task.Run(() => MaintainAsync(budget, request));
            }
            var found = currentID is { } id && records.TryGetValue(id, out var record);
            var value = found ? records[currentID!.Value] : default;
            ready &= found && snapshot is not null && value.Snapshot?.SameForHistory(snapshot) == true
                && physical.CleanupFailures == 0 && loaded.CleanupFailures == 0;
            var eligible = gate.Observe(owner, currentID ?? 0, value.Key ?? "", ready);
            FinishWrite();
            if (retryPngRequested && retainedPng is { } retained && writing is null && maintenance is null)
            {
                retryPngRequested = false;
                BeginPngWrite(retained);
            }
            readyToCompose = capture?.IsCompleted == true && ticket is not null && gate.CanAccept(ticket);
            if (encoding?.IsCompleted == true && maintenance is null)
            {
                try
                {
                    var bytes = encoding.GetAwaiter().GetResult();
                    // Poll is on Framework; its caller has freshly checked current identity and state.
                    if (ticket is null || !gate.CanAccept(ticket) || !PortraitThumbnailImage.IsSafeSize(bytes)) Reject();
                    else
                    {
                        retainedPng = (ticket.StateKey, bytes);
                        BeginPngWrite(retainedPng.Value);
                    }
                }
                catch (Exception error) { physical.ObserveFailure(error); lastError = error.Message; Reject(); }
                finally { encoding = null; encodingLifetime?.Dispose(); encodingLifetime = null; }
            }
            if (capture?.IsCompleted == true && !readyToCompose)
            {
                if (capture.IsCompletedSuccessfully) physical.Release(capture.Result);
                else physical.ObserveFailure(capture.Exception);
                capture = null;
                captureLifetime?.Dispose(); captureLifetime = null;
                Reject();
            }
            if (!eligible || capture is not null || encoding is not null || writing is not null || maintenance is not null) return;
            if (retainedPng is { } failed && failed.Key == value.Key) return;
            if (Volatile.Read(ref imageKeys).Contains(value.Key!)
                || favoritePhotos.Status(value.Key!) == PortraitFavoritePhotoStatus.Available)
            {
                if (gate.Begin() is { } present) gate.Complete(present, true);
                return;
            }
            if (availableBytes < 84 + 1) return;
            ticket = gate.Begin();
            if (ticket is not null)
            {
                Volatile.Write(ref photoOperation, photoTrace.Start(PortraitPhotoOperation.HistoryCapture));
                captureLifetime = physical.Enter();
                try { capture = source.CaptureAsync(identity, CancellationToken.None, photoOperation); }
                catch (Exception error) { physical.ObserveFailure(error); captureLifetime?.Dispose(); captureLifetime = null; throw; }
                // Dispose may reenter the source's synchronous prefix before capture is assigned.
                if (disposed) DetachCapture();
            }
        }
    }

    private void FinishWrite()
    {
        if (writing?.IsCompleted != true) return;
        var success = writing.IsCompletedSuccessfully && writing.Result;
        if (success)
        {
            saved++;
            // Copy only the completed managed PNG. This never starts another capture or replays
            // a snapshot. Retain it before ordinary history maintenance can evict the source.
            favoriteSynchronizationNeeded = true;
            maintenanceNeeded = true;
            maintenanceRequest++;
            if (writingKey is { } key) loaded.Invalidate(key);
            if (retainedPng?.Key == writingKey) retainedPng = null;
            pngWriteError = null;
            pngWriteCode = PortraitStorageCode.Success;
        }
        else pngWriteError = writing.IsFaulted ? writing.Exception?.GetBaseException().Message
            : cache.LastError ?? "照片写盘失败，已保留待保存图片。";
        if (!success) pngWriteCode = cache.LastStorageCode == PortraitStorageCode.Success
            ? PortraitStorageCode.StorageError : cache.LastStorageCode;
        if (ticket is not null) gate.Complete(ticket, success);
        writing = null;
        writingKey = null;
        ticket = null;
    }

    private void BeginPngWrite((string Key, byte[] Bytes) image)
    {
        writingKey = image.Key;
        writing = photoOperation.CallAsync(PortraitPhotoPhase.CacheWrite, () => Task.Run(() => WritePngAsync(image)));
    }

    private async Task<bool> WritePngAsync((string Key, byte[] Bytes) image)
    {
        bool Attempt()
        {
            int budget;
            lock (sync) budget = availableBytes;
            lock (favoriteSynchronization)
            {
                var accepted = cache.SetAvailableBytes(budget) && cache.TryPut(image.Key, image.Bytes);
                if (accepted) SynchronizeFavoritePhotos();
                PublishCacheMetadata();
                return accepted;
            }
        }
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt != 0) await retryDelay(TimeSpan.FromSeconds(1 << (attempt - 1)), CancellationToken.None).ConfigureAwait(false);
            if (PortraitPhotoReclaimPolicy.TryWithReclaim(
                attempt: Attempt,
                capacity: () => cache.LastStorageCode == PortraitStorageCode.Capacity,
                reclaim: ReclaimStorage,
                state: () => (cache.Count, (long)cache.Bytes),
                maxReclaims: PortraitThumbnailCache.MaximumEntriesForReclaim)) return true;
            if (cache.LastStorageCode == PortraitStorageCode.Capacity) continue;
            if (cache.LastStorageCode is not (PortraitStorageCode.Busy or PortraitStorageCode.StorageError)) return false;
        }
        return false;
    }

    /// <summary>Schedules owned pixel work without changing the game's render context.</summary>
    public void Draw(bool pointerDown = false)
    {
        lock (sync)
        {
            if (disposed) return;
            if (pointerDown) { gate.Reset(); readyToCompose = false; }
            if (readyToCompose && capture?.IsCompleted == true)
            {
                var completedCapture = capture; capture = null;
                var heldCapture = captureLifetime; captureLifetime = null;
                PortraitThumbnailLayers? layers = null;
                try
                {
                    layers = completedCapture.GetAwaiter().GetResult();
                    if (layers is null || ticket is null || !gate.CanAccept(ticket)) { Reject(); return; }
                    var inputs = new List<PortraitCaptureLayer> { new(layers.Character) };
                    if (layers.Frame is { } frame) inputs.Add(new(frame, UV0: layers.FrameUV0, UV1: layers.FrameUV1));
                    if (layers.Accent is { } accent) inputs.Add(new(accent, UV0: layers.AccentUV0, UV1: layers.AccentUV1));
                    encodingLifetime = physical.Enter();
                    try { encoding = ComposeAndEncode(inputs, layers.Character, lifetime.Token); }
                    catch (Exception error) { physical.ObserveFailure(error); encodingLifetime?.Dispose(); encodingLifetime = null; throw; }
                    if (disposed) DetachEncoding();
                }
                catch (Exception error) { physical.ObserveFailure(error); lastError = error.Message; Reject(); }
                finally { physical.Release(layers); heldCapture?.Dispose(); }
            }
        }
    }

    private async Task<byte[]> ComposeAndEncode(IReadOnlyList<PortraitCaptureLayer> layers,
        IDalamudTextureWrap character, CancellationToken token)
    {
        // Both the compositor and the visibility check own their references before Draw returns.
        var retainedCharacter = character.CreateWrapSharingLowLevelResource();
        try
        {
            var operation = photoOperation;
            var composed = await new PortraitCaptureSoftwareCompositor(textures, readback)
                .ComposeAsync(160, 256, layers, token, operation).ConfigureAwait(false);
            return await Encode(composed, retainedCharacter, token).ConfigureAwait(false);
        }
        finally { physical.Release(retainedCharacter); }
    }

    private async Task<byte[]> Encode(IDalamudTextureWrap texture, IDalamudTextureWrap character, CancellationToken token)
    {
        IDalamudTextureWrap? retainedCharacter = null;
        try
        {
            retainedCharacter = character.CreateWrapSharingLowLevelResource();
            using var stream = new MemoryStream();
            var operation = photoOperation;
            // A ready native model can precede its first rendered frame. Never make an empty
            // image a permanent cache hit; keep the gate retryable until actual pixels exist.
            var raw = await operation.CallAsync(PortraitPhotoPhase.VisibleReadback,
                () => readback.GetRawImageAsync(retainedCharacter, new TextureModificationArgs { Uv1 = Vector2.One },
                    leaveWrapOpen: true, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!PortraitThumbnailImage.HasVisiblePixels(raw.RawData, raw.Specification.Width, raw.Specification.Height,
                raw.Specification.Pitch, raw.Specification.DxgiFormat)) return [];
            await operation.CallAsync(PortraitPhotoPhase.PngEncode,
                () => readback.SaveToStreamAsync(texture, Png, stream, leaveWrapOpen: true,
                    leaveStreamOpen: true, cancellationToken: CancellationToken.None)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return stream.ToArray();
        }
        finally { physical.Release(retainedCharacter); physical.Release(texture); }
    }

    private void Reject()
    {
        discarded++;
        if (ticket is not null) gate.Complete(ticket, false);
        ticket = null;
    }

    public IDalamudTextureWrap? Get(long recordID)
    {
        lock (sync) return !disposed && records.TryGetValue(recordID, out var value) ? loaded.Get(value.Key) : null;
    }

    public PortraitHistoryPhotoStatus GetHistoryPhotoStatus(long recordID)
    {
        string key;
        bool pendingCapture;
        bool failedWrite;
        PortraitStorageCode failedWriteCode;
        int photoBudget;
        lock (sync)
        {
            if (disposed || !records.TryGetValue(recordID, out var record)) return PortraitHistoryPhotoStatus.Missing;
            key = record.Key;
            pendingCapture = ticket?.StateKey == key
                && (capture is not null || encoding is not null || writingKey == key
                    || retainedPng?.Key == key && pngWriteError is null);
            failedWrite = retainedPng?.Key == key && pngWriteError is not null;
            failedWriteCode = pngWriteCode;
            photoBudget = availableBytes;
        }

        var loadState = loaded.State(key);
        if (loadState == PortraitThumbnailTextureState.Resident) return PortraitHistoryPhotoStatus.Saved;
        if (loadState == PortraitThumbnailTextureState.Failed) return PortraitHistoryPhotoStatus.StorageError;
        if (failedWrite)
            return failedWriteCode == PortraitStorageCode.Capacity
                ? PortraitHistoryPhotoStatus.Capacity : PortraitHistoryPhotoStatus.StorageError;

        var favoriteStatus = favoritePhotos.Status(key);
        if (favoriteStatus == PortraitFavoritePhotoStatus.Capacity) return PortraitHistoryPhotoStatus.Capacity;
        if (favoriteStatus == PortraitFavoritePhotoStatus.StorageError) return PortraitHistoryPhotoStatus.StorageError;
        var hasDurableImage = Volatile.Read(ref imageKeys).Contains(key) || favoritePhotos.Contains(key);
        if (loadState == PortraitThumbnailTextureState.Loading || pendingCapture || hasDurableImage)
            return PortraitHistoryPhotoStatus.Loading;
        if (photoBudget < 84 + 1) return PortraitHistoryPhotoStatus.Capacity;

        var storageState = storage.Snapshot;
        if (storageState.UsedBytes > storageState.LimitBytes) return PortraitHistoryPhotoStatus.Capacity;
        if (storageState.Degraded) return PortraitHistoryPhotoStatus.StorageError;
        return PortraitHistoryPhotoStatus.Missing;
    }

    public bool SetFavoriteSnapshots(IEnumerable<(ulong ContentID, PortraitEditorSnapshot Snapshot)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var keys = values.Take(5001).Select(value => SnapshotKey(value.ContentID, value.Snapshot))
            .ToHashSet(StringComparer.Ordinal);
        return SetFavoriteKeys(keys, accepted: false);
    }

    private bool SetFavoriteKeys(HashSet<string> keys, bool accepted)
    {
        lock (favoriteSynchronization)
        {
            lock (sync)
            {
                if (disposed && !accepted) return false;
                favoriteKeys = keys;
                unarchivedFavoriteKeys = keys.ToHashSet(StringComparer.Ordinal);
                favoriteReferencesKnown = true;
                favoriteSynchronizationNeeded = true;
                maintenanceNeeded = true;
                maintenanceRequest++;
                UpdateCacheProtection();
            }
            return SynchronizeFavoritePhotos();
        }
    }

    /// <summary>
    /// Publishes a new durable reference set from a serialized worker. Accepted work remains
    /// tracked through close; callers never wait on the archive's physical I/O lock.
    /// </summary>
    public Task<bool> SetFavoriteSnapshotsAsync(IEnumerable<(ulong ContentID, PortraitEditorSnapshot Snapshot)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var keys = values.Take(5001).Select(value => SnapshotKey(value.ContentID, value.Snapshot))
            .ToHashSet(StringComparer.Ordinal);
        return QueueFavoriteWork(() => Task.FromResult(SetFavoriteKeys(keys, accepted: true)));
    }

    /// <summary>
    /// Admits one metadata action before close, immediately pinning a committed image. Its worker
    /// rescues any image displaced by an already executing candidate before
    /// metadata can become durable, then archives the resulting references before releasing
    /// that pin. A failed action retains its pin until the next explicit action or retry.
    /// </summary>
    public Task<bool> PersistFavoriteAsync(Func<Task<bool>> writeMetadata,
        Func<IEnumerable<(ulong ContentID, PortraitEditorSnapshot Snapshot)>> readFavorites,
        (ulong ContentID, PortraitEditorSnapshot Snapshot)? protect = null)
    {
        ArgumentNullException.ThrowIfNull(writeMetadata);
        ArgumentNullException.ThrowIfNull(readFavorites);
        var key = protect is { } value ? SnapshotKey(value.ContentID, value.Snapshot) : null;
        lock (sync)
        {
            if (disposed) return Task.FromResult(false);
            // Purely managed admission: neither the service I/O mutex nor the cache writer
            // gate is entered on Framework. The bounded rescue copy covers in-flight writes.
            var admission = key is null ? null : cache.ProtectAdmission(key);
            return QueueFavoriteWork(async () =>
            {
                lock (sync) favoriteMetadataFailed = true;
                lock (favoriteSynchronization)
                {
                    var rescue = admission?.Image ?? [];
                    if (rescue.Length == 0 && retainedFavoriteImage is { } previous && previous.Key == key)
                        rescue = previous.Bytes;
                    retainedFavoriteProtection?.Dispose();
                    retainedFavoriteProtection = admission;
                    retainedFavoriteImage = key is not null && rescue.Length != 0 ? (key, rescue) : null;
                    if (key is not null)
                    {
                        lock (sync)
                        {
                            provisionalFavoriteKeys[key] = provisionalFavoriteKeys.GetValueOrDefault(key) + 1;
                            UpdateCacheProtection();
                        }
                        retainedFavoriteProtection = new FavoriteActionProtection(new FavoriteProtection(this, key), admission);
                        if (rescue.Length != 0 && !cache.Contains(key) && !cache.TryPut(key, rescue))
                        {
                            PublishCacheMetadata();
                            return false;
                        }
                        PublishCacheMetadata();
                    }
                }
                if (!await writeMetadata().ConfigureAwait(false)) return false;
                lock (sync) favoriteMetadataFailed = false;
                var keys = readFavorites().Take(5001).Select(value => SnapshotKey(value.ContentID, value.Snapshot))
                    .ToHashSet(StringComparer.Ordinal);
                var archived = SetFavoriteKeys(keys, accepted: true);
                // Permanent unarchived pins are published by SetFavoriteKeys even when the archive
                // fails, so the provisional token can end without risking the only durable PNG.
                lock (favoriteSynchronization)
                {
                    retainedFavoriteProtection?.Dispose();
                    retainedFavoriteProtection = null;
                    retainedFavoriteImage = null;
                }
                return archived;
            });
        }
    }

    private sealed class FavoriteActionProtection(IDisposable policy, IDisposable? admission) : IDisposable
    {
        public void Dispose()
        {
            policy.Dispose();
            admission?.Dispose();
        }
    }

    private Task<bool> QueueFavoriteWork(Func<Task<bool>> action)
    {
        lock (sync)
        {
            if (disposed) return Task.FromResult(false);
            var previous = favoriteSynchronizationTask;
            return favoriteSynchronizationTask = Task.Run(async () =>
            {
                if (previous is not null)
                {
                    try { await previous.ConfigureAwait(false); }
                    catch (Exception error) { lock (sync) lastError = error.Message; }
                }
                return await action().ConfigureAwait(false);
            });
        }
    }

    public IDisposable ProtectFavoriteSnapshot(ulong contentID, PortraitEditorSnapshot snapshot)
    {
        var key = SnapshotKey(contentID, snapshot);
        lock (favoriteSynchronization)
        lock (sync)
        {
            if (disposed) return new FavoriteProtection(null, key);
            provisionalFavoriteKeys[key] = provisionalFavoriteKeys.GetValueOrDefault(key) + 1;
            UpdateCacheProtection();
            return new FavoriteProtection(this, key);
        }
    }

    private void ReleaseFavoriteProtection(string key)
    {
        lock (favoriteSynchronization)
        lock (sync)
        {
            if (!provisionalFavoriteKeys.TryGetValue(key, out var count)) return;
            if (count == 1) provisionalFavoriteKeys.Remove(key); else provisionalFavoriteKeys[key] = count - 1;
            UpdateCacheProtection();
            maintenanceNeeded = true;
            maintenanceRequest++;
        }
    }

    private sealed class FavoriteProtection(PortraitHistoryPhotos? owner, string key) : IDisposable
    {
        private PortraitHistoryPhotos? value = owner;
        public void Dispose() => Interlocked.Exchange(ref value, null)?.ReleaseFavoriteProtection(key);
    }

    /// <summary>Called outside any other store lock; favorites with no durable archive remain pinned.</summary>
    public bool ReclaimStorage()
    {
        var version = storage.ReclaimRequestVersion;
        try
        {
            lock (favoriteSynchronization)
            {
                lock (sync) { if (disposed) return false; }
                var success = cache.ReclaimUnprotected();
                PublishCacheMetadata();
                return success;
            }
        }
        finally { storage.CompleteReclaim(version); }
    }

    public bool TryWithStorageReclaim(Func<bool> attempt, Func<bool> capacity)
        => PortraitPhotoReclaimPolicy.TryWithReclaim(attempt, capacity, ReclaimStorage,
            () => (cache.Count, (long)cache.Bytes), PortraitThumbnailCache.MaximumEntriesForReclaim);

    private bool SynchronizeFavoritePhotos(bool requestReclaim = true)
    {
        lock (favoriteSynchronization)
        {
            HashSet<string> keys;
            lock (sync)
            {
                if (!favoriteReferencesKnown) return true;
                keys = favoriteKeys.ToHashSet(StringComparer.Ordinal);
            }
            // The archive holds its own lock while invoking this reader. Materialize completed
            // PNGs first so its callback never takes another store/authority/service lock.
            var completed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var key in keys)
                if (cache.TryGet(key, out var bytes)) completed[key] = bytes;
            var success = favoritePhotos.Synchronize(keys, key => completed.GetValueOrDefault(key), requestReclaim);
            var missing = keys.Where(key => favoritePhotos.Status(key) != PortraitFavoritePhotoStatus.Available)
                .ToHashSet(StringComparer.Ordinal);
            lock (sync)
            {
                unarchivedFavoriteKeys = missing;
                // A completed PNG that failed to archive is still the only durable copy.
                // Keep both its pin and a pending retry; missing uncaptured images wait for
                // FinishWrite rather than serializing the same empty archive repeatedly.
                favoriteSynchronizationNeeded = !success || missing.Any(completed.ContainsKey);
                UpdateCacheProtection();
            }
            return success && !missing.Any(completed.ContainsKey);
        }
    }

    private async Task MaintainAsync(int budget, long request)
    {
        var success = false;
        string? failure = null;
        for (var attempt = 0; attempt != 4; attempt++)
        {
            try
            {
                if (attempt != 0) await retryDelay(TimeSpan.FromSeconds(1 << (attempt - 1)), lifetime.Token).ConfigureAwait(false);
                lock (sync) { if (disposed) return; maintenanceAttempts = attempt + 1; }
                if (storage.NeedsReclaim) ReclaimStorage();
                lock (favoriteSynchronization)
                {
                    bool synchronize;
                    lock (sync) synchronize = favoriteSynchronizationNeeded;
                    var archived = !synchronize || SynchronizeFavoritePhotos(requestReclaim: false);
                    var fitted = cache.SetAvailableBytes(budget, requestReclaim: false);
                    PublishCacheMetadata();
                    success = archived && fitted;
                    failure = success ? null : cache.LastError ?? favoritePhotos.LastError
                        ?? "照片暂时无法保存；收藏照片已保留，等待存储空间或下一次重试。";
                }
                if (success) break;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { failure = error.Message; }
        }
        lock (sync)
        {
            if (request == maintenanceRequest) maintenanceNeeded = !success;
            attemptedStorageChange = storage.ChangeVersion;
            maintenanceError = failure;
        }
    }

    // Called only with sync held. The cache setter changes scalar/reference policy only;
    // it performs no I/O and never enters the quota authority or another store.
    private void UpdateCacheProtection()
    {
        var preferred = favoriteKeys.Concat(provisionalFavoriteKeys.Keys).ToHashSet(StringComparer.Ordinal);
        var pinned = favoriteReferencesKnown
            ? unarchivedFavoriteKeys.Concat(provisionalFavoriteKeys.Keys).ToHashSet(StringComparer.Ordinal) : null;
        cache.SetProtectedKeys(pinned, preferred);
    }

    public PortraitFavoritePhotoStatus FavoritePhotoStatus(ulong contentID, PortraitEditorSnapshot snapshot)
        => favoritePhotos.Status(SnapshotKey(contentID, snapshot));

    private static string SnapshotKey(ulong contentID, PortraitEditorSnapshot snapshot)
        => PortraitThumbnailCache.KeyForSnapshot(contentID.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "|" + snapshot.ToJSON());

    // Saved favorites read their independent archive first. The source character remains part
    // of the key; a different character's identical parameters cannot borrow the wrong model.
    public IDalamudTextureWrap? GetSnapshot(ulong contentID, PortraitEditorSnapshot snapshot)
    {
        var key = SnapshotKey(contentID, snapshot);
        lock (sync) return !disposed ? loaded.Get(key) : null;
    }

    /// <summary>
    /// Seals photo capture and releases Framework-owned texture references without waiting for
    /// managed cache maintenance. The returned task is safe to await after native cleanup.
    /// </summary>
    public Task BeginDispose()
    {
        Task<bool>? diskWrite;
        Task? diskMaintenance;
        lock (sync)
        {
            if (managedDrain is not null) return managedDrain;
            disposed = true;
            // Provider callbacks can await Present. Schedule them off Framework and observe
            // failures without joining their physical GPU lifetime to managed file cleanup.
            var cancellationLifetime = physical.Enter();
            _ = lifetime.CancelAsync().ContinueWith(task =>
            {
                try
                {
                    if (task.Exception is { } error)
                        lock (sync) lastError ??= error.GetBaseException().Message;
                }
                finally { cancellationLifetime.Dispose(); }
            }, TaskScheduler.Default);
            gate.Reset();
            loaded.Dispose();
            diskWrite = writing ?? (retainedPng is not null ? Task.FromResult(false) : null);
            diskMaintenance = maintenance;
            // No blocking wait on Present-dependent copies during hot reload.
            DetachCapture();
            DetachEncoding();
            records.Clear();
            var favoriteSynchronization = favoriteSynchronizationTask;
            managedDrain = DrainManagedAsync(diskWrite, diskMaintenance, favoriteSynchronization);
            physicalDrain = Task.WhenAll(physical.Seal(), loaded.DrainPhysicalAsync());
            return managedDrain;
        }
    }

    public Task DrainPhysicalAsync() => physicalDrain ?? throw new InvalidOperationException("Seal photo work before draining it.");

    private void DetachCapture()
    {
        if (capture is not { } pending) return;
        var held = captureLifetime ?? physical.Enter();
        capture = null; captureLifetime = null;
        _ = pending.ContinueWith(task =>
        {
            try { if (task.IsCompletedSuccessfully) physical.Release(task.Result); else physical.ObserveFailure(task.Exception); }
            finally { held.Dispose(); }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void DetachEncoding()
    {
        if (encoding is not { } pending) return;
        var held = encodingLifetime ?? physical.Enter();
        encoding = null; encodingLifetime = null;
        _ = pending.ContinueWith(task => { try { physical.ObserveFailure(task.Exception); } finally { held.Dispose(); } },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private async Task DrainManagedAsync(Task<bool>? diskWrite, Task? diskMaintenance, Task<bool>? favoriteTail)
    {
        // BeginDispose starts this method while it owns sync on Framework. Even when every
        // receipt is already complete, final observation must begin after that call returns.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await Task.WhenAll(new Task?[] { diskWrite, diskMaintenance, favoriteTail }
                .OfType<Task>()).ConfigureAwait(false);
            bool favoriteFailed;
            lock (sync) favoriteFailed = favoriteTail is not null && (favoriteMetadataFailed || favoriteSynchronizationNeeded);
            // A prior archive receipt may be false even though explicit maintenance has since
            // published the final archive. Evaluate the durable final state after all accepted
            // work settles; a failed metadata action must still remain a failed close.
            if (diskWrite is { Result: false } || favoriteFailed)
                throw new IOException("照片或收藏仍有未能保存的最终记录。");
        }
        catch (Exception error)
        {
            // All accepted managed tasks have settled. Keep a failure observable even when no
            // Framework Poll can run again; this continuation never re-enters native ownership.
            lock (sync) lastError ??= error.Message;
            throw;
        }
    }

    public void Dispose()
    {
        BeginDispose().GetAwaiter().GetResult();
    }
}
