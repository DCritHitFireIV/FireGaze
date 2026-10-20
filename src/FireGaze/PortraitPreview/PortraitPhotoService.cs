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
    private readonly ITextureProvider textures;
    private readonly ITextureReadbackProvider readback;
    private readonly PortraitThumbnailSource source;
    private readonly PortraitThumbnailCache cache;
    private readonly PortraitFavoritePhotos favoritePhotos;
    private HashSet<string> favoriteKeys = new(StringComparer.Ordinal);
    private bool favoriteReferencesKnown;
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
    private Task? maintenance;
    private int availableBytes;
    private bool maintenanceNeeded;
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
        IEnumerable<(ulong ContentID, PortraitEditorSnapshot Snapshot)>? favoriteSnapshots = null)
    {
        photoTrace = new(logger);
        this.textures = textures;
        this.readback = readback;
        this.source = source;
        availableBytes = Math.Max(0, PortraitThumbnailCache.MaximumBytes - historyBytes);
        // On the first upgrade, rescue known favorites before shrinking the old disposable
        // cache. Otherwise an already completed image could be evicted before being archived.
        cache = new(path, favoriteSnapshots is null ? availableBytes : PortraitThumbnailCache.MaximumBytes);
        favoritePhotos = new(favoritePhotoPath ?? Path.ChangeExtension(path, ".favorites.json"));
        if (favoriteSnapshots is not null)
        {
            SetFavoriteSnapshots(favoriteSnapshots);
            cache.SetAvailableBytes(availableBytes);
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
            favoritePhotoError = favoritePhotos.LastError, lastError = lastError ?? cacheError,
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
        get { lock (sync) return capture is not null || encoding is not null || writing is not null || maintenance is not null; }
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
            if (maintenanceNeeded && maintenance is null && writing is null)
            {
                var budget = availableBytes;
                maintenanceNeeded = false;
                // Keep other characters' disposable photos; their history is not in the current UI.
                maintenance = Task.Run(() => { cache.SetAvailableBytes(budget); PublishCacheMetadata(); });
            }
            var found = currentID is { } id && records.TryGetValue(id, out var record);
            var value = found ? records[currentID!.Value] : default;
            ready &= found && snapshot is not null && value.Snapshot?.SameForHistory(snapshot) == true
                && physical.CleanupFailures == 0 && loaded.CleanupFailures == 0;
            var eligible = gate.Observe(owner, currentID ?? 0, value.Key ?? "", ready);
            FinishWrite();
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
                        var key = ticket.StateKey;
                        var operation = photoOperation;
                        writing = operation.CallAsync(PortraitPhotoPhase.CacheWrite, () => Task.Run(() =>
                        {
                            int budget;
                            lock (sync) { if (disposed) return false; budget = availableBytes; }
                            var accepted = cache.SetAvailableBytes(budget) && cache.TryPut(key, bytes);
                            PublishCacheMetadata();
                            return accepted;
                        }));
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
        if (success && ticket is not null)
        {
            saved++;
            // Copy only the completed managed PNG. This never starts another capture or replays
            // a snapshot. Retain it before ordinary history maintenance can evict the source.
            SynchronizeFavoritePhotos();
            loaded.Invalidate(ticket.StateKey);
        }
        else if (writing.IsFaulted) lastError = writing.Exception?.GetBaseException().Message;
        if (ticket is not null) gate.Complete(ticket, success);
        writing = null;
        ticket = null;
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

    public bool SetFavoriteSnapshots(IEnumerable<(ulong ContentID, PortraitEditorSnapshot Snapshot)> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (sync)
        {
            if (disposed) return false;
            favoriteKeys = values.Take(5001).Select(value => SnapshotKey(value.ContentID, value.Snapshot))
                .ToHashSet(StringComparer.Ordinal);
            favoriteReferencesKnown = true;
            return SynchronizeFavoritePhotos();
        }
    }

    private bool SynchronizeFavoritePhotos() => !favoriteReferencesKnown || favoritePhotos.Synchronize(favoriteKeys,
        key => cache.TryGet(key, out var bytes) ? bytes : null);

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
            diskWrite = writing;
            diskMaintenance = maintenance;
            // No blocking wait on Present-dependent copies during hot reload.
            DetachCapture();
            DetachEncoding();
            records.Clear();
            managedDrain = DrainManagedAsync(diskWrite, diskMaintenance);
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

    private async Task DrainManagedAsync(Task<bool>? diskWrite, Task? diskMaintenance)
    {
        try
        {
            await PortraitManagedDrain.Combine(diskWrite, diskMaintenance).ConfigureAwait(false);
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
