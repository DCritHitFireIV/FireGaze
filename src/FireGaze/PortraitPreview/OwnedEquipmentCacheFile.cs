using System.Text;
using System.Text.Json;

namespace FireGaze.PortraitPreview;

/// <summary>Owns one portable JSON cache in the plugin's supplied configuration directory.</summary>
internal sealed class OwnedEquipmentCacheFile
{
    private const string FileName = "portrait-owned-cache-v1.json";
    private readonly object writeGate = new();
    private readonly string directory;
    private readonly string filePath;

    public OwnedEquipmentCacheFile(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        directory = Path.GetFullPath(configDirectory);
        filePath = Path.Combine(directory, FileName);
    }

    public bool Load(OwnedEquipmentCatalog catalog, out string reason)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        reason = "尚无已读取记录缓存";
        try
        {
            if (!File.Exists(filePath)) return false;
            if (new FileInfo(filePath).Length > 32_000_000)
            {
                reason = "拥有装备缓存过大，未读取";
                return false;
            }
            if (!catalog.ImportJSON(File.ReadAllText(filePath, Encoding.UTF8)))
            {
                reason = "拥有装备缓存无效，保留本次已读取记录";
                return false;
            }
            reason = "已读取历史缓存，来源记录可能过期";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            reason = "拥有装备缓存暂时无法读取";
            return false;
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
        lock (writeGate) return SaveJSONCore(jsonText, out reason);
    }

    private bool SaveJSONCore(string jsonText, out string reason)
    {
        string? temporary = null;
        reason = "拥有装备缓存暂时无法保存";
        try
        {
            if (string.IsNullOrWhiteSpace(jsonText) || jsonText.Length > 16_000_000)
            {
                reason = "拥有装备缓存快照为空或过大，未覆盖原缓存";
                return false;
            }
            using (var document = JsonDocument.Parse(jsonText))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out var version)
                    || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
                    || !root.TryGetProperty("Characters", out var characters) || characters.ValueKind != JsonValueKind.Array)
                {
                    reason = "拥有装备缓存快照无效，未覆盖原缓存";
                    return false;
                }
            }
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(jsonText);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, filePath, overwrite: true);
            temporary = null;
            reason = "已保存拥有装备读取记录";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException)
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
