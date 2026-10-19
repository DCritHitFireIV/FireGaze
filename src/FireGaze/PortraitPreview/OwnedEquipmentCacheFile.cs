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
    private bool inspected;
    private bool writesAllowed;

    public OwnedEquipmentCacheFile(string configDirectory, OwnedCacheMigrationFileIO? migrationIO = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        directory = Path.GetFullPath(configDirectory);
        filePath = Path.Combine(directory, FileName);
        this.migrationIO = migrationIO ?? new();
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
        lock (writeGate)
        {
            if (!inspected)
            {
                inspected = true;
                writesAllowed = !File.Exists(filePath);
            }
            if (!writesAllowed)
            {
                reason = "原拥有装备缓存尚未安全读取，未覆盖";
                return false;
            }
            reason = "拥有装备缓存快照无效，未覆盖原缓存";
            if (string.IsNullOrWhiteSpace(jsonText) || jsonText.Length > 16_000_000) return false;
            // Validate the complete detached structure without consulting native/game data.
            // A queued v1 snapshot must never downgrade a successfully migrated primary.
            var validator = new OwnedEquipmentCatalog(itemID => itemID);
            if (!validator.PrepareImportJSON(jsonText, out var currentJSON, out var legacy, out _) || legacy) return false;
            // Detached observations queued by an older build must not bring retired sources back.
            return WriteAtomic(currentJSON, out reason);
        }
    }

    private string PreserveMigrationBackup(byte[] original, string suffix)
    {
        var backupPath = filePath + suffix;
        if (!File.Exists(backupPath)) migrationIO.CreateBackup(backupPath, original);
        if (!FileMatches(backupPath, original))
            throw new IOException("Recovery copy does not match the original cache.");
        return backupPath;
    }

    private static bool FileMatches(string path, byte[] expected)
        => new FileInfo(path).Length == expected.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);

    private bool WriteAtomic(string jsonText, out string reason, byte[]? migrationOriginal = null, string? migrationBackupPath = null)
    {
        string? temporary = null;
        reason = "拥有装备缓存暂时无法保存，保留原缓存";
        try
        {
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(jsonText);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            // Verify after staging has finished, immediately before replacing the primary.
            // A changed/disappeared source or recovery copy cannot authorize migration.
            if (migrationOriginal is not null && (migrationBackupPath is null || !FileMatches(filePath, migrationOriginal) || !FileMatches(migrationBackupPath, migrationOriginal)))
            {
                reason = "拥有装备缓存或备份在迁移期间发生变化，未覆盖";
                return false;
            }
            if (File.Exists(filePath)) File.Replace(temporary, filePath, null);
            else File.Move(temporary, filePath);
            temporary = null;
            reason = "已保存拥有装备读取记录";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
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
