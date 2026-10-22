using System.Security.Cryptography;
using System.Text.Json;

namespace FireGaze.PortraitPreview;

internal enum PortraitFavoritePhotoStatus { Available, Missing, Capacity, StorageError }

/// <summary>
/// Durable, bounded copies of already completed thumbnails. The favorite snapshots own the
/// references; history retention may discard its own images without affecting this archive.
/// This class cannot render a portrait or obtain any game/GPU resource.
/// </summary>
internal sealed class PortraitFavoritePhotos
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    // Includes both valid v1 documents when recovering an interrupted older move.
    public const int MaximumReferences = 10000;
    private static readonly JsonSerializerOptions JSONOptions = new() { MaxDepth = 8 };
    private readonly object gate = new();
    private readonly string path;
    private readonly int maximumBytes;
    private readonly PortraitStorageBudget storage;
    private PortraitStorageCode lastPublishCode;
    // Serializes archive candidates without covering the committed snapshot reads below.
    // The archive may hold this gate across a slow atomic replacement; status and image reads
    // must continue to observe the last committed candidate during that interval.
    private readonly object operationGate = new();
    private Dictionary<string, SavedPhoto> photos = new(StringComparer.Ordinal);
    private Dictionary<string, PortraitFavoritePhotoStatus> statuses = new(StringComparer.Ordinal);
    private bool readOnly;
    public string? LastError { get { lock (gate) return lastError; } }
    private string? lastError;
    public int Count { get { lock (gate) return photos.Count; } }
    public bool Contains(string key) { lock (gate) return photos.ContainsKey(key); }

    public PortraitFavoritePhotos(string path, int maximumBytes = MaximumBytes, PortraitStorageBudget? storage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumBytes is < 128 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.path = Path.GetFullPath(path);
        this.maximumBytes = maximumBytes;
        this.storage = storage ?? PortraitStorageBudget.ForDirectory(Path.GetDirectoryName(this.path)!);
        this.storage.RegisterPath(this.path);
        Load();
    }

    public bool TryGet(string key, out byte[] image)
    {
        lock (gate)
        {
            var found = photos.TryGetValue(key, out var value);
            image = found ? (byte[])value!.Image.Clone() : [];
            return found;
        }
    }

    public PortraitFavoritePhotoStatus Status(string key)
    {
        lock (gate) return photos.ContainsKey(key) ? PortraitFavoritePhotoStatus.Available
            : readOnly ? PortraitFavoritePhotoStatus.StorageError
            : statuses.GetValueOrDefault(key, PortraitFavoritePhotoStatus.Missing);
    }

    /// <summary>
    /// Rebuild references from the durable favorite library. Only the supplied completed PNG
    /// reader can fill missing images. Every successful change is flushed here, never on unload.
    /// Capacity refuses new photos instead of evicting an existing referenced photo.
    /// </summary>
    public bool Synchronize(IReadOnlySet<string> references, Func<string, byte[]?> readCompletedPhoto,
        bool requestReclaim = true)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(readCompletedPhoto);
        if (references.Count > MaximumReferences || references.Any(key => !ValidKey(key)))
            throw new ArgumentException("Invalid favorite photo references.", nameof(references));
        lock (operationGate)
        {
            Dictionary<string, SavedPhoto> existing;
            lock (gate)
            {
                if (readOnly) return false;
                existing = new(photos, StringComparer.Ordinal);
            }
            var candidate = existing.Where(pair => references.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var candidateBytes = Serialize(candidate).Length;
            var nextStatuses = new Dictionary<string, PortraitFavoritePhotoStatus>(StringComparer.Ordinal);
            foreach (var key in references)
            {
                if (candidate.ContainsKey(key)) continue;
                var image = readCompletedPhoto(key);
                if (image is null || !PortraitThumbnailImage.IsSafeSize(image))
                {
                    nextStatuses[key] = PortraitFavoritePhotoStatus.Missing;
                    continue;
                }
                var photo = new SavedPhoto(Convert.ToHexString(SHA256.HashData(image)), (byte[])image.Clone());
                // A one-entry dictionary gives the exact escaped/base64 entry cost. Avoid
                // serializing the whole growing archive once per new favorite during migration.
                var entryBytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, SavedPhoto> { [key] = photo }, JSONOptions).Length
                    - 2 + (candidate.Count == 0 ? 0 : 1);
                if (candidateBytes + entryBytes > maximumBytes)
                {
                    nextStatuses[key] = PortraitFavoritePhotoStatus.Capacity;
                    continue;
                }
                candidate.Add(key, photo);
                candidateBytes += entryBytes;
            }
            if (SamePhotos(existing, candidate))
            {
                lock (gate)
                {
                    statuses = nextStatuses;
                    lastError = null;
                }
                return true;
            }
            if (!Publish(candidate, requestReclaim, out var publishCode, out var publishError))
            {
                lock (gate)
                {
                    foreach (var key in references.Where(key => !photos.ContainsKey(key)))
                        nextStatuses[key] = publishCode == PortraitStorageCode.Capacity
                            ? PortraitFavoritePhotoStatus.Capacity : PortraitFavoritePhotoStatus.StorageError;
                    statuses = nextStatuses;
                    lastError = publishError;
                }
                return false;
            }
            lock (gate)
            {
                photos = candidate;
                statuses = nextStatuses;
                lastError = null;
            }
            return true;
        }
    }

    private static bool SamePhotos(Dictionary<string, SavedPhoto> previous, Dictionary<string, SavedPhoto> candidate)
        => candidate.Count == previous.Count
        && candidate.All(pair => previous.TryGetValue(pair.Key, out var value) && value.Checksum == pair.Value.Checksum);

    private static bool ValidKey(string key) => key is { Length: 64 }
        && key.All(value => value is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static byte[] Serialize(Dictionary<string, SavedPhoto> values)
        => JsonSerializer.SerializeToUtf8Bytes(new SavedDocument(1, values), JSONOptions);

    private void Load()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes) throw new InvalidDataException("Favorite photo archive exceeds its capacity.");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            var document = JsonSerializer.Deserialize<SavedDocument>(bytes, JSONOptions);
            if (document is not { Version: 1, Photos: not null } || document.Photos.Count > MaximumReferences
                || document.Photos.Any(pair => !ValidKey(pair.Key) || pair.Value is null
                    || !PortraitThumbnailImage.IsSafeSize(pair.Value.Image) || !ValidKey(pair.Value.Checksum)
                    || Convert.ToHexString(SHA256.HashData(pair.Value.Image)) != pair.Value.Checksum))
                throw new InvalidDataException("Favorite photo archive is damaged or unsupported.");
            photos = new(document.Photos, StringComparer.Ordinal);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (IsStorageError(error) || error is JsonException)
        {
            // This is intentional saved content, unlike the disposable history image cache.
            readOnly = true;
            lastError = error.Message;
        }
    }

    private bool Publish(Dictionary<string, SavedPhoto> candidate, bool requestReclaim,
        out PortraitStorageCode publishCode, out string publishError)
    {
        lastPublishCode = PortraitStorageCode.StorageError;
        publishCode = PortraitStorageCode.StorageError;
        publishError = "收藏照片归档暂时无法保存。";
        try
        {
            var bytes = Serialize(candidate);
            if (bytes.Length > maximumBytes)
            {
                publishCode = PortraitStorageCode.Capacity;
                publishError = "收藏照片归档超过容量上限。";
                return false;
            }
            if (!storage.TryPublish(path, bytes.Length, stream => stream.Write(bytes), keepBackup: true,
                    out var result, requestReclaim: requestReclaim))
            {
                lastPublishCode = result.Code;
                publishCode = result.Code;
                publishError = result.Reason;
                return false;
            }
            lastPublishCode = PortraitStorageCode.Success;
            publishCode = PortraitStorageCode.Success;
            publishError = "";
            return true;
        }
        catch (Exception error) when (IsStorageError(error))
        {
            publishError = error.Message;
            return false;
        }
    }

    private static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;
    private sealed record SavedPhoto(string Checksum, byte[] Image);
    private sealed record SavedDocument(int Version, Dictionary<string, SavedPhoto> Photos);
}
