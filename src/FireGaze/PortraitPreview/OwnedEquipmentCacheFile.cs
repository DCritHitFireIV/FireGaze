using System.Security.Cryptography;
using System.Text;

namespace FireGaze.PortraitPreview;

/// <summary>Owns one portable JSON cache in the plugin's supplied configuration directory.</summary>
internal sealed class OwnedEquipmentCacheFile
{
    // Keep the existing watched location; the document carries its current schema version.
    private const string FileName = "portrait-owned-cache-v1.json";
    private readonly object writeGate = new();
    private readonly string directory;
    private readonly string filePath;
    private readonly OwnedCacheMigrationFileIO migrationIO;
    private readonly PortraitStorageBudget storage;
    private bool inspected;
    private bool writesAllowed;

    public OwnedEquipmentCacheFile(string configDirectory, OwnedCacheMigrationFileIO? migrationIO = null,
        PortraitStorageBudget? storage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        directory = Path.GetFullPath(configDirectory);
        filePath = Path.Combine(directory, FileName);
        this.migrationIO = migrationIO ?? new();
        this.storage = storage ?? PortraitStorageBudget.ForDirectory(directory);
        this.storage.RegisterPath(filePath);
    }

    public bool Load(OwnedEquipmentCatalog catalog, out string reason)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        lock (writeGate)
        {
            inspected = true;
            writesAllowed = false;
            reason = "尚无已读取记录缓存";
            try
            {
                if (!File.Exists(filePath)) { writesAllowed = true; return false; }
                if (new FileInfo(filePath).Length > 32_000_000)
                {
                    reason = "拥有装备缓存过大，保留原缓存";
                    return false;
                }
                var original = File.ReadAllBytes(filePath);
                var preamble = Encoding.UTF8.Preamble;
                var bytes = original.AsSpan();
                if (bytes.StartsWith(preamble)) bytes = bytes[preamble.Length..];
                var jsonText = new UTF8Encoding(false, true).GetString(bytes);
                if (!catalog.PrepareImportJSON(jsonText, out var currentJSON, out var legacy, out var retiredSources))
                {
                    reason = "拥有装备缓存无效，保留原缓存及本次已读取记录";
                    return false;
                }
                if (legacy || retiredSources)
                {
                    // A user may temporarily return to an older build and later bring back a changed v2 cache.
                    // Keep each retired-source recovery copy immutable without blocking the next safe cleanup.
                    var backupSuffix = legacy ? ".v1.bak" : $".retainer-equipped.{Convert.ToHexString(SHA256.HashData(original))}.bak";
                    var backupPath = PreserveMigrationBackup(original, backupSuffix);
                    if (!WriteAtomic(currentJSON, out reason, original, backupPath)) return false;
                }
                if (!catalog.ImportJSON(currentJSON)) return false;
                writesAllowed = true;
                reason = "已读取历史缓存，来源记录可能过期";
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or DecoderFallbackException)
            {
                reason = "拥有装备缓存暂时无法读取或安全迁移，保留原缓存";
                return false;
            }
        }
    }

    /// <summary>Convenience synchronous save for unload/startup; keep regular writes off Framework.</summary>
    public bool Save(OwnedEquipmentCatalog catalog, out string reason)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return SaveJSON(catalog.ExportJSON(), out reason);
    }

    /// <summary>Write a detached Framework snapshot on a worker; serialize calls in snapshot order.</summary>
    public bool SaveJSON(string jsonText, out string reason)
    {
        var result = SaveJSONResult(jsonText);
        reason = result.Reason;
        return result.Code == PortraitStorageCode.Success;
    }

    /// <summary>Preserves typed capacity and transient failures for the production retry worker.</summary>
    public PortraitStorageResult SaveJSONResult(string jsonText)
    {
        lock (writeGate)
        {
            if (!inspected)
            {
                inspected = true;
                writesAllowed = !File.Exists(filePath);
            }
            if (!writesAllowed)
            {
                return new(PortraitStorageCode.Conflict, "原拥有装备缓存尚未安全读取，未覆盖");
            }
            var invalid = new PortraitStorageResult(PortraitStorageCode.InvalidPath, "拥有装备缓存快照无效，未覆盖原缓存");
            if (string.IsNullOrWhiteSpace(jsonText) || jsonText.Length > 16_000_000) return invalid;
            // Validate the complete detached structure without consulting native/game data.
            // A queued v1 snapshot must never downgrade a successfully migrated primary.
            var validator = new OwnedEquipmentCatalog(itemID => itemID);
            if (!validator.PrepareImportJSON(jsonText, out var currentJSON, out var legacy, out _) || legacy) return invalid;
            // Detached observations queued by an older build must not bring retired sources back.
            return WriteAtomicResult(currentJSON);
        }
    }

    private string PreserveMigrationBackup(byte[] original, string suffix)
    {
        var backupPath = filePath + suffix;
        if (!File.Exists(backupPath)
            && !storage.TryRunMigration(filePath, original.Length,
                () => migrationIO.CreateBackup(backupPath, original), out var result))
            throw new IOException(result.Reason);
        if (!FileMatches(backupPath, original))
            throw new IOException("Recovery copy does not match the original cache.");
        return backupPath;
    }

    private static bool FileMatches(string path, byte[] expected)
        => new FileInfo(path).Length == expected.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);

    private bool WriteAtomic(string jsonText, out string reason, byte[]? migrationOriginal = null, string? migrationBackupPath = null)
    {
        var result = WriteAtomicResult(jsonText, migrationOriginal, migrationBackupPath);
        reason = result.Reason;
        return result.Code == PortraitStorageCode.Success;
    }

    private PortraitStorageResult WriteAtomicResult(string jsonText, byte[]? migrationOriginal = null, string? migrationBackupPath = null)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(jsonText);
            var success = storage.TryPublish(filePath, bytes.Length, stream => stream.Write(bytes),
                keepBackup: migrationOriginal is null, out var result, beforeReplace: () =>
            {
                // Validate after the complete staged generation was durably flushed.
                if (migrationOriginal is not null && (migrationBackupPath is null
                    || !FileMatches(filePath, migrationOriginal) || !FileMatches(migrationBackupPath, migrationOriginal)))
                    throw new IOException("拥有装备缓存或备份在迁移期间发生变化，未覆盖");
            });
            return success ? new(PortraitStorageCode.Success, "已保存拥有装备读取记录") : result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(PortraitStorageCode.StorageError, "拥有装备缓存暂时无法保存，保留原缓存：" + ex.Message);
        }
    }
}

/// <summary>The durable create-new boundary used when retaining a legacy recovery copy.</summary>
internal class OwnedCacheMigrationFileIO
{
    public virtual void CreateBackup(string path, byte[] contents)
    {
        var created = false;
        var complete = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
            complete = true;
        }
        finally
        {
            if (created && !complete)
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }
}
