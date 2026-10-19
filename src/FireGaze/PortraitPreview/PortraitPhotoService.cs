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
    private readonly PortraitThumbnailCaptureGate gate = new();
    private readonly PortraitThumbnailTextureCache<IDalamudTextureWrap> loaded;
    private readonly PortraitPhotoTrace photoTrace;
    private PortraitPhotoTrace.Operation photoOperation = PortraitPhotoTrace.Operation.Silent;
    private readonly CancellationTokenSource lifetime = new();
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

    public PortraitHistoryPhotos(string path, ITextureProvider textures, ITextureReadbackProvider readback,
        PortraitThumbnailSource source, int historyBytes, Action<string>? logger = null)
    {
        photoTrace = new(logger);
        this.textures = textures;
        this.readback = readback;
        this.source = source;
        availableBytes = Math.Max(0, PortraitThumbnailCache.MaximumBytes - historyBytes);
        cache = new(path, availableBytes);
        PublishCacheMetadata();
        loaded = new(async key =>
        {
            var operation = photoTrace.Start(PortraitPhotoOperation.LoadedImage);
            byte[] image = [];
            if (!operation.Call(PortraitPhotoPhase.LoadedImageRead, () => cache.TryGet(key, out image))
                || !PortraitThumbnailImage.IsSafeSize(image)) return null;
            var result = await operation.CallAsync(PortraitPhotoPhase.LoadedImageUpload,
                () => textures.CreateFromImageAsync(image, "FireGaze history thumbnail", lifetime.Token)).ConfigureAwait(false);
            if (result.Width == 160 && result.Height == 256) return result;
            result.Dispose();
            return null;
        });
    }

    public object Diagnostics()
    {
        lock (sync) return new { count = imageKeys.Count, bytes = cacheBytes, availableBytes, loaded = loaded.Count,
            loading = loaded.InFlight, capturing = capture is not null || encoding is not null || writing is not null,
            saved, discarded, lastError = lastError ?? cacheError };
    }

    public bool NeedsStateValidation
    {
        get { lock (sync) return capture?.IsCompleted == true || encoding?.IsCompleted == true; }
    }

    public bool IsBusy
    {
        get { lock (sync) return capture is not null || encoding is not null || writing is not null || maintenance is not null; }
    }

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
            ready &= found && snapshot is not null && value.Snapshot?.SameForHistory(snapshot) == true;
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
                catch (Exception error) { lastError = error.Message; Reject(); }
                finally { encoding = null; }
            }
            if (capture?.IsCompleted == true && !readyToCompose)
            {
                if (capture.IsCompletedSuccessfully) capture.Result?.Dispose();
                else _ = capture.Exception;
                capture = null;
                Reject();
            }
            if (!eligible || capture is not null || encoding is not null || writing is not null || maintenance is not null) return;
            if (Volatile.Read(ref imageKeys).Contains(value.Key!))
            {
                if (gate.Begin() is { } present) gate.Complete(present, true);
                return;
            }
            if (availableBytes < 84 + 1) return;
            ticket = gate.Begin();
            if (ticket is not null)
            {
                photoOperation = photoTrace.Start(PortraitPhotoOperation.HistoryCapture);
                capture = source.CaptureAsync(identity, lifetime.Token, photoOperation);
            }
        }
    }

    private void FinishWrite()
    {
        if (writing?.IsCompleted != true) return;
        var success = writing.IsCompletedSuccessfully && writing.Result;
        if (success && ticket is not null) { saved++; loaded.Invalidate(ticket.StateKey); }
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
                try
                {
                    using var layers = capture.GetAwaiter().GetResult();
                    if (layers is null || ticket is null || !gate.CanAccept(ticket)) { Reject(); return; }
                    var inputs = new List<PortraitCaptureLayer> { new(layers.Character) };
                    if (layers.Frame is { } frame) inputs.Add(new(frame, UV0: layers.FrameUV0, UV1: layers.FrameUV1));
                    if (layers.Accent is { } accent) inputs.Add(new(accent, UV0: layers.AccentUV0, UV1: layers.AccentUV1));
                    encoding = ComposeAndEncode(inputs, layers.Character, lifetime.Token);
                }
                catch (Exception error) { lastError = error.Message; Reject(); }
                finally { capture = null; }
            }
        }
    }

    private async Task<byte[]> ComposeAndEncode(IReadOnlyList<PortraitCaptureLayer> layers,
        IDalamudTextureWrap character, CancellationToken token)
    {
        // Both the compositor and the visibility check own their references before Draw returns.
        using var retainedCharacter = character.CreateWrapSharingLowLevelResource();
        var operation = photoOperation;
        var composed = await new PortraitCaptureSoftwareCompositor(textures, readback)
            .ComposeAsync(160, 256, layers, token, operation).ConfigureAwait(false);
        return await Encode(composed, retainedCharacter, token).ConfigureAwait(false);
    }

    private async Task<byte[]> Encode(IDalamudTextureWrap texture, IDalamudTextureWrap character, CancellationToken token)
    {
        using (texture)
        using (var retainedCharacter = character.CreateWrapSharingLowLevelResource())
        using (var stream = new MemoryStream())
        {
            var operation = photoOperation;
            // A ready native model can precede its first rendered frame. Never make an empty
            // image a permanent cache hit; keep the gate retryable until actual pixels exist.
            var raw = await operation.CallAsync(PortraitPhotoPhase.VisibleReadback,
                () => readback.GetRawImageAsync(retainedCharacter, new TextureModificationArgs { Uv1 = Vector2.One },
                    leaveWrapOpen: true, cancellationToken: token)).ConfigureAwait(false);
            if (!PortraitThumbnailImage.HasVisiblePixels(raw.RawData, raw.Specification.Width, raw.Specification.Height,
                raw.Specification.Pitch, raw.Specification.DxgiFormat)) return [];
            await operation.CallAsync(PortraitPhotoPhase.PngEncode,
                () => readback.SaveToStreamAsync(texture, Png, stream, leaveWrapOpen: true,
                    leaveStreamOpen: true, cancellationToken: token)).ConfigureAwait(false);
            return stream.ToArray();
        }
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

    // A favorite can reuse an already captured image across characters. Browsing never renders
    // older states into the live editor; the durable snapshot remains usable if its photo expires.
    public IDalamudTextureWrap? GetSnapshot(ulong contentID, PortraitEditorSnapshot snapshot)
    {
        var key = PortraitThumbnailCache.KeyForSnapshot(contentID.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "|" + snapshot.ToJSON());
        lock (sync) return !disposed ? loaded.Get(key) : null;
    }

    public void Dispose()
    {
        Task? diskWrite, diskMaintenance;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            gate.Reset();
            loaded.Dispose();
            diskWrite = writing;
            diskMaintenance = maintenance;
            // No blocking wait on Present-dependent copies during hot reload.
            if (capture is { } pending)
                _ = pending.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result?.Dispose(); else _ = task.Exception; }, TaskScheduler.Default);
            if (encoding is { } encodingPending)
                _ = encodingPending.ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
            records.Clear();
        }
        // These tasks perform managed file I/O only; drain them before a new hot-reloaded owner opens the same cache.
        try { diskWrite?.GetAwaiter().GetResult(); diskMaintenance?.GetAwaiter().GetResult(); }
        catch (Exception) { }
    }
}
