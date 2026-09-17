using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FireGaze.RepoAudit;

/// <summary>
/// 插件图标的**落盘缓存**（卫月自己不落盘：每次重开游戏都要重新下载）。
/// </summary>
/// <remarks>
/// 目录结构：<c>&lt;配置目录&gt;/icons/</c>
/// <list type="bullet">
/// <item><c>icons.json</c>：InternalName → { Url, File, SavedUtc }；</item>
/// <item>图标文件本身：文件名 = 图标地址的 SHA-1 前 16 位 + 扩展名（同一张图被多个插件引用时只存一份）。</item>
/// </list>
/// 地址变了（作者换了新图标）→ 旧文件作废，下次重新下载。所有公开方法都可从后台线程调用（内部加锁）。
/// </remarks>
internal sealed class IconCache
{
    private sealed class Entry
    {
        public string Url { get; set; } = string.Empty;

        public string File { get; set; } = string.Empty;

        public DateTime SavedUtc { get; set; }
    }

    private readonly string dir;
    private readonly string indexPath;
    private readonly Dictionary<string, Entry> index = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private bool dirty;

    public IconCache(string configDirectory)
    {
        this.dir = Path.Combine(configDirectory, "icons");
        this.indexPath = Path.Combine(this.dir, "icons.json");
        this.Load();
    }

    /// <summary>缓存目录（界面上写给用户看 / 手动清理用）。</summary>
    public string Directory => this.dir;

    /// <summary>缓存里有多少个插件的图标。</summary>
    public int Count
    {
        get
        {
            lock (this.gate)
            {
                return this.index.Count;
            }
        }
    }

    /// <summary>这个插件的图标在缓存里吗（地址还必须对得上）。</summary>
    public bool TryGetPath(string internalName, string url, out string path)
    {
        path = string.Empty;

        lock (this.gate)
        {
            if (!this.index.TryGetValue(internalName, out var entry)
                || !string.Equals(entry.Url, url, StringComparison.Ordinal))
            {
                return false;
            }

            var full = Path.Combine(this.dir, entry.File);
            if (!File.Exists(full))
            {
                // 文件被人删了 → 索引也清掉，免得每次都说"有"
                this.index.Remove(internalName);
                this.dirty = true;
                return false;
            }

            path = full;
            return true;
        }
    }

    /// <summary>把下载到的字节写进缓存（后台线程可调）。</summary>
    public bool Save(string internalName, string url, byte[] bytes, string extension)
    {
        try
        {
            lock (this.gate)
            {
                System.IO.Directory.CreateDirectory(this.dir);

                var name = Hash(url) + extension;
                File.WriteAllBytes(Path.Combine(this.dir, name), bytes);
                this.index[internalName] = new Entry
                {
                    Url = url,
                    File = name,
                    SavedUtc = DateTime.UtcNow,
                };
                this.dirty = true;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把索引写盘（攒几次一起写，失败只当没写）。</summary>
    public void Flush()
    {
        try
        {
            lock (this.gate)
            {
                if (!this.dirty)
                {
                    return;
                }

                System.IO.Directory.CreateDirectory(this.dir);
                var json = JsonSerializer.Serialize(
                    this.index.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
                    new JsonSerializerOptions { WriteIndented = false });

                // 先写临时文件再替换：中途断电不会留下半个 JSON
                var tmp = this.indexPath + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, this.indexPath, true);
                this.dirty = false;
            }
        }
        catch
        {
            // 缓存写不进去不影响主流程
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(this.indexPath))
            {
                return;
            }

            var parsed = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                File.ReadAllText(this.indexPath, Encoding.UTF8));

            if (parsed is null)
            {
                return;
            }

            foreach (var (key, value) in parsed)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value?.File))
                {
                    this.index[key] = value!;
                }
            }
        }
        catch
        {
            // 索引坏了就当没有缓存（下次下载会覆盖）
            this.index.Clear();
        }
    }

    private static string Hash(string url)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>从 Content-Type / 地址后缀猜扩展名（纹理解码看内容，扩展名只是给人看的）。</summary>
    public static string ExtensionFor(string? contentType, string url)
    {
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
        var byType = type switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            _ => null,
        };

        if (byType is not null)
        {
            return byType;
        }

        foreach (var ext in (string[])[".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"])
        {
            if (url.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return ext == ".jpeg" ? ".jpg" : ext;
            }
        }

        return ".img";
    }

    /// <summary>内容像不像图片（防止把 404 页面 / HTML 错误页当图标存下来）。</summary>
    public static bool LooksLikeImage(byte[] bytes, string? contentType)
    {
        if (bytes.Length < 8)
        {
            return false;
        }

        if (contentType is not null
            && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // PNG / JPEG / GIF / BMP / WEBP(RIFF) / ICO 的魔数
        return (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
               || (bytes[0] == 0xFF && bytes[1] == 0xD8)
               || (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
               || (bytes[0] == 0x42 && bytes[1] == 0x4D)
               || (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46)
               || (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00);
    }
}
