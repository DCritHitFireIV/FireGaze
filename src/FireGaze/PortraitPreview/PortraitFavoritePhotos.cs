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
    private const int MaximumReferences = 5000;
    private static readonly JsonSerializerOptions JSONOptions = new() { MaxDepth = 8 };
    private readonly object gate = new();
    private readonly string path;
    private readonly int maximumBytes;
    private Dictionary<string, SavedPhoto> photos = new(StringComparer.Ordinal);
    private Dictionary<string, PortraitFavoritePhotoStatus> statuses = new(StringComparer.Ordinal);
    private bool readOnly;
    public string? LastError { get; private set; }
    public int Count { get { lock (gate) return photos.Count; } }

    public PortraitFavoritePhotos(string path, int maximumBytes = MaximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumBytes is < 128 or > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        this.path = Path.GetFullPath(path);
        this.maximumBytes = maximumBytes;
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
    public bool Synchronize(IReadOnlySet<string> references, Func<string, byte[]?> readCompletedPhoto)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(readCompletedPhoto);
        if (references.Count > MaximumReferences || references.Any(key => !ValidKey(key)))
            throw new ArgumentException("Invalid favorite photo references.", nameof(references));
        lock (gate)
        {
            if (readOnly) return false;
            var candidate = photos.Where(pair => references.Contains(pair.Key))
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
            if (SamePhotos(candidate))
            {
                statuses = nextStatuses;
                LastError = null;
                return true;
            }
            if (!Publish(candidate))
            {
                foreach (var key in references.Where(key => !photos.ContainsKey(key)))
                    nextStatuses[key] = PortraitFavoritePhotoStatus.StorageError;
                statuses = nextStatuses;
                return false;
            }
            statuses = nextStatuses;
            return true;
        }
    }

    private bool SamePhotos(Dictionary<string, SavedPhoto> candidate) => candidate.Count == photos.Count
        && candidate.All(pair => photos.TryGetValue(pair.Key, out var previous) && previous.Checksum == pair.Value.Checksum);

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
            LastError = error.Message;
        }
    }

    private bool Publish(Dictionary<string, SavedPhoto> candidate)
    {
        string? temporary = null;
        try
        {
            var bytes = Serialize(candidate);
            if (bytes.Length > maximumBytes) throw new InvalidDataException("Favorite photo archive exceeds its capacity.");
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Retain one complete prior generation in the same atomic replacement. This is
            // bounded saved content; a damaged current file remains read-only with its backup.
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
            temporary = null;
            photos = candidate;
            LastError = null;
            return true;
        }
        catch (Exception error) when (IsStorageError(error))
        {
            LastError = error.Message;
            return false;
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (IsStorageError(error)) { LastError = error.Message; }
        }
    }

    private static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;
    private sealed record SavedPhoto(string Checksum, byte[] Image);
    private sealed record SavedDocument(int Version, Dictionary<string, SavedPhoto> Photos);
}
