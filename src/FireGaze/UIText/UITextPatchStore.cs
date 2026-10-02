using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FireGaze.UIText;

/// <summary>
///     一个被补丁的文件（主程序集或它的伴生程序集）。
/// </summary>
internal sealed record UITextPatchFile
{
    /// <summary>文件全路径（插件更新会换版本目录，所以要按文件名匹配而不是全路径）。</summary>
    [JsonPropertyName("Path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>打补丁时这个文件的 SHA-256（原始内容）。</summary>
    [JsonPropertyName("SourceHash")]
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>打完补丁后这个文件的 SHA-256。</summary>
    [JsonPropertyName("PatchedHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PatchedHash { get; set; }

    /// <summary>原始文件的备份路径。</summary>
    [JsonPropertyName("BackupPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackupPath { get; set; }

    [JsonIgnore]
    public bool HasBackup => !string.IsNullOrEmpty(this.BackupPath) && File.Exists(this.BackupPath);
}

/// <summary>
///     一个插件的补丁状态（存在 <c>&lt;配置目录&gt;/uitrans/state/&lt;内部名&gt;.json</c>）。
/// </summary>
internal sealed record UITextPatchState
{
    [JsonPropertyName("InternalName")]
    public string InternalName { get; set; } = string.Empty;

    /// <summary>
    ///     被打补丁的那个 DLL 路径（插件更新后会变成新版本目录）。
    /// </summary>
    [JsonPropertyName("DLLPath")]
    public string DLLPath { get; set; } = string.Empty;

    /// <summary>
    ///     打补丁时插件清单里的版本号。
    /// </summary>
    [JsonPropertyName("PluginVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginVersion { get; set; }

    /// <summary>
    ///     打补丁时「原始 DLL」的 SHA-256。
    /// </summary>
    [JsonPropertyName("SourceHash")]
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>
    ///     打补丁**之后**文件本身的 SHA-256：用来区分「盘上是我们自己的补丁」和「插件更新换掉了文件」。
    ///     没有它就会把自家的补丁误判成「插件更新了」，然后反复重打（2026-10-01 踩过）。
    /// </summary>
    [JsonPropertyName("PatchedHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PatchedHash { get; set; }

    [JsonPropertyName("PatchedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PatchedAt { get; set; }

    /// <summary>
    ///     原始 DLL 的备份路径。
    /// </summary>
    [JsonPropertyName("BackupPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackupPath { get; set; }

    /// <summary>
    ///     这次打进去了多少条译文（统计用）。
    /// </summary>
    [JsonPropertyName("AppliedEntries")]
    public int AppliedEntries { get; set; }

    /// <summary>
    ///     本会话里重载过几次（用来区分「补丁已经真的被加载过」和「只是写进了盘」）。
    /// </summary>
    [JsonPropertyName("ReloadAttempts")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReloadAttempts { get; set; }

    /// <summary>
    ///     打完补丁、还没确认重载成功：下次启动/重载后要复核，失败就自动还原。
    /// </summary>
    [JsonPropertyName("PendingVerify")]
    public bool PendingVerify { get; set; }

    [JsonPropertyName("PendingSince")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PendingSince { get; set; }

    /// <summary>
    ///     最近一次失败原因（失败后自动还原，这里留证据）。
    /// </summary>
    [JsonPropertyName("LastError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastError { get; set; }

    /// <summary>
    ///     这次补丁动过的全部文件（主程序集 + 伴生程序集）。
    ///     老版本的状态只有单个 <see cref="DLLPath" />，<see cref="EffectiveFiles" /> 会自动退化成一条。
    /// </summary>
    [JsonPropertyName("Files")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<UITextPatchFile>? Files { get; set; }

    /// <summary>
    ///     实际要看的文件列表（新状态用 <see cref="Files" />；老状态用 DLLPath/SourceHash/PatchedHash/BackupPath）。
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<UITextPatchFile> EffectiveFiles => this.Files is { Count: > 0 }
        ? this.Files
        :
        [
            new UITextPatchFile
            {
                Path = this.DLLPath,
                SourceHash = this.SourceHash,
                PatchedHash = this.PatchedHash,
                BackupPath = this.BackupPath,
            },
        ];

    [JsonIgnore]
    public bool HasBackup => this.EffectiveFiles.Any(f => f.HasBackup);
}

/// <summary>
///     持久目录里的原始备份清单（<c>&lt;持久目录&gt;/uit-originals/&lt;内部名&gt;/manifest.json</c>）。
///     补丁记录丢了时靠它 + 文件哈希自证认领；绝不能只凭文件名就还原（插件更新过时会把旧原文盖回去）。
/// </summary>
internal sealed record UITextOriginalsManifest
{
    [JsonPropertyName("format")]
    public int Format { get; set; } = 1;

    [JsonPropertyName("internalName")]
    public string InternalName { get; set; } = string.Empty;

    [JsonPropertyName("pluginVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginVersion { get; set; }

    [JsonPropertyName("patchedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PatchedAt { get; set; }

    [JsonPropertyName("appliedEntries")]
    public int AppliedEntries { get; set; }

    [JsonPropertyName("files")]
    public List<UITextOriginalsManifestFile> Files { get; set; } = [];
}

/// <summary>清单里的一份文件：当前盘上（打完补丁）的哈希 + 备份（原文）的哈希 + 备份文件名。</summary>
internal sealed record UITextOriginalsManifestFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sourceHash")]
    public string SourceHash { get; set; } = string.Empty;

    [JsonPropertyName("patchedHash")]
    public string PatchedHash { get; set; } = string.Empty;

    [JsonPropertyName("backup")]
    public string Backup { get; set; } = string.Empty;
}

/// <summary>
///     补丁状态的读写与备份管理。
///     原始备份优先写进**持久目录**（活过插件配置重置）；写不进去才退回配置目录（老行为，保底）。
/// </summary>
internal sealed class UITextPatchStore
{
    private readonly string stateDirectory;
    private readonly string backupDirectory;
    private readonly string? originalsDirectory;
    private readonly object gate = new();

    /// <summary>状态文件按「写入时间 + 长度」缓存的读结果：列表每 5 秒、编辑器每帧都会问一遍，不能每次都读盘解析。</summary>
    private readonly Dictionary<string, (DateTime WriteUtc, long Length, UITextPatchState? State)> loadCache = new(StringComparer.OrdinalIgnoreCase);

    public UITextPatchStore(string configDirectory, string? durableOriginalsDirectory = null)
    {
        this.stateDirectory = Path.Combine(configDirectory, "uitrans", "state");
        this.backupDirectory = Path.Combine(configDirectory, "uitrans", "backups");
        this.originalsDirectory = durableOriginalsDirectory;
    }

    /// <summary>
    ///     读一个插件的补丁状态（没有就返回 null）。
    /// </summary>
    public UITextPatchState? Load(string internalName)
    {
        var path = this.PathOf(internalName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<UITextPatchState>(json, Options);
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 读补丁状态失败：{Path}", path);
            return null;
        }
    }

    /// <summary>
    ///     读补丁状态：文件没变（写入时间 + 长度一致）时复用上次读出来的结果，不重复读盘解析。
    ///     界面每帧 / 每 5 秒都会问状态，走这条路（2026-10-03 B-01/B-03）。
    /// </summary>
    public UITextPatchState? LoadCached(string internalName)
    {
        var path = this.PathOf(internalName);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                lock (this.loadCache)
                {
                    this.loadCache.Remove(internalName);
                }

                return null;
            }

            lock (this.loadCache)
            {
                if (this.loadCache.TryGetValue(internalName, out var cached)
                    && cached.WriteUtc == info.LastWriteTimeUtc
                    && cached.Length == info.Length)
                {
                    return cached.State;
                }
            }

            var state = this.Load(internalName);
            lock (this.loadCache)
            {
                this.loadCache[internalName] = (info.LastWriteTimeUtc, info.Length, state);
            }

            return state;
        }
        catch (Exception)
        {
            return this.Load(internalName);
        }
    }

    /// <summary>
    ///     写状态；返回是否真的写成功了（写失败时调用方不能再把这次新建的备份当孤儿清掉——那是唯一能还原的原始件）。
    /// </summary>
    public bool Save(UITextPatchState state)
    {
        var path = this.PathOf(state.InternalName);
        try
        {
            lock (this.gate)
            {
                Directory.CreateDirectory(this.stateDirectory);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, Options) + Environment.NewLine, new System.Text.UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }

            try
            {
                var info = new FileInfo(path);
                lock (this.loadCache)
                {
                    this.loadCache[state.InternalName] = (info.LastWriteTimeUtc, info.Length, state);
                }
            }
            catch (Exception)
            {
                // 缓存更新失败不影响写盘结果
            }

            return true;
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 写补丁状态失败：{Path}", path);
            return false;
        }
    }

    /// <summary>
    ///     删状态（还原干净后）。
    /// </summary>
    public void Delete(string internalName)
    {
        try
        {
            var path = this.PathOf(internalName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 删不掉不影响使用
        }

        lock (this.loadCache)
        {
            this.loadCache.Remove(internalName);
        }
    }

    /// <summary>
    ///     列出全部状态（启动时检查「待复核」用）。
    /// </summary>
    public List<UITextPatchState> ListAll()
    {
        var result = new List<UITextPatchState>();
        try
        {
            if (!Directory.Exists(this.stateDirectory))
            {
                return result;
            }

            foreach (var file in Directory.GetFiles(this.stateDirectory, "*.json"))
            {
                var state = this.Load(Path.GetFileNameWithoutExtension(file));
                if (state is not null)
                {
                    result.Add(state);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 列补丁状态失败");
        }

        return result;
    }

    /// <summary>
    ///     备份原始 DLL（已经备份过同一份就复用），返回备份路径。
    ///     优先写持久目录（重置插件配置也还在）；写不进去才退回配置目录。
    /// </summary>
    public string? Backup(string internalName, string dllPath, string sourceHash)
    {
        var durable = this.BackupToOriginals(internalName, dllPath, sourceHash);
        if (durable is not null)
        {
            return durable;
        }

        try
        {
            lock (this.gate)
            {
                Directory.CreateDirectory(this.backupDirectory);
                var target = Path.Combine(this.backupDirectory, $"{Sanitize(internalName)}-{BackupLabel(internalName, dllPath)}{sourceHash[..12]}.dll");
                if (!File.Exists(target))
                {
                    File.Copy(dllPath, target, overwrite: false);
                }

                return target;
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 备份失败：{Path}", dllPath);
            return null;
        }
    }

    /// <summary>
    ///     这个备份路径是不是在持久目录里（迁移时只看配置目录里的旧备份；还原 / 清理也只清持久目录）。
    /// </summary>
    public bool IsDurableBackup(string? path)
    {
        if (this.originalsDirectory is null || string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(this.originalsDirectory);
            var full = Path.GetFullPath(path);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>持久目录里现有的全部原始备份（打补丁前的快照，失败时用它清理本次新建的孤儿）。</summary>
    public HashSet<string> SnapshotOriginals()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (this.originalsDirectory is null || !Directory.Exists(this.originalsDirectory))
        {
            return result;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(this.originalsDirectory, "*.orig", SearchOption.AllDirectories))
            {
                result.Add(path);
            }
        }
        catch (Exception)
        {
            // 列不出来就不清理（宁可留垃圾也不误删）
        }

        return result;
    }

    /// <summary>
    ///     把「这次打进 DLL 的哈希」与「备份文件的哈希」落成持久目录里的清单，供丢了补丁记录时认领。
    ///     只收录确实在持久目录里的备份；顺带清掉这一步不再引用的旧 *.orig。
    /// </summary>
    public void PublishManifest(string internalName, string? pluginVersion, string? patchedAt, int appliedEntries, IReadOnlyList<UITextPatchFile> files, string pluginDirectory)
    {
        var directory = this.OriginalsDirectoryOf(internalName);
        if (directory is null)
        {
            return;
        }

        try
        {
            lock (this.gate)
            {
                var manifest = new UITextOriginalsManifest
                {
                    InternalName = internalName,
                    PluginVersion = pluginVersion,
                    PatchedAt = patchedAt,
                    AppliedEntries = appliedEntries,
                };

                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    if (file.PatchedHash is not { Length: > 0 } patchedHash || !this.IsDurableBackup(file.BackupPath))
                    {
                        // 有文件没能进持久目录：不发清单——认领时缺一份就不完整，宁可退回反向还原
                        return;
                    }

                    var backupName = Path.GetFileName(file.BackupPath!);
                    keep.Add(backupName);
                    manifest.Files.Add(new UITextOriginalsManifestFile
                    {
                        Name = RelativeName(pluginDirectory, file.Path),
                        SourceHash = file.SourceHash,
                        PatchedHash = patchedHash,
                        Backup = backupName,
                    });
                }

                if (manifest.Files.Count == 0)
                {
                    return;
                }

                Directory.CreateDirectory(directory);
                foreach (var existing in Directory.GetFiles(directory, "*.orig"))
                {
                    if (!keep.Contains(Path.GetFileName(existing)))
                    {
                        TryDeleteFile(existing);
                    }
                }

                var path = Path.Combine(directory, "manifest.json");
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(manifest, Options) + Environment.NewLine, new System.Text.UTF8Encoding(false));
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 写原始备份清单失败：{Name}", internalName);
        }
    }

    /// <summary>
    ///     补丁记录丢了时，从持久目录认领：只有当「盘上文件正好是打完补丁的那份」且「备份正好是当时那份原文」时
    ///     才重建状态；任一哈希对不上就拒绝（插件更新过时，绝不能把旧原文盖回去）。
    /// </summary>
    public UITextPatchState? TryAdopt(string internalName, string dllPath, string? pluginVersion)
    {
        var directory = this.OriginalsDirectoryOf(internalName);
        if (directory is null || string.IsNullOrEmpty(dllPath))
        {
            return null;
        }

        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var pluginDirectory = Path.GetDirectoryName(dllPath);
        if (string.IsNullOrEmpty(pluginDirectory))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<UITextOriginalsManifest>(
                File.ReadAllText(manifestPath, System.Text.Encoding.UTF8),
                Options);
            if (manifest?.Files is not { Count: > 0 })
            {
                return null;
            }

            var mainName = Path.GetFileName(dllPath);
            var files = new List<UITextPatchFile>();
            UITextPatchFile? main = null;
            foreach (var item in manifest.Files)
            {
                var current = Path.Combine(pluginDirectory, item.Name);
                var backup = Path.Combine(directory, item.Backup);
                if (!File.Exists(current) || !File.Exists(backup))
                {
                    return null;
                }

                if (!string.Equals(HashOf(current), item.PatchedHash, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(HashOf(backup), item.SourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var file = new UITextPatchFile
                {
                    Path = current,
                    SourceHash = item.SourceHash,
                    PatchedHash = item.PatchedHash,
                    BackupPath = backup,
                };
                files.Add(file);
                if (string.Equals(item.Name, mainName, StringComparison.OrdinalIgnoreCase))
                {
                    main = file;
                }
            }

            if (main is null)
            {
                return null;
            }

            // 主程序集排第一（界面与旧状态都按第一份当主程序集用）
            files.Remove(main);
            files.Insert(0, main);

            var state = new UITextPatchState
            {
                InternalName = internalName,
                DLLPath = main.Path,
                PluginVersion = pluginVersion ?? manifest.PluginVersion,
                SourceHash = main.SourceHash,
                PatchedHash = main.PatchedHash,
                PatchedAt = manifest.PatchedAt ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                BackupPath = main.BackupPath,
                AppliedEntries = manifest.AppliedEntries,
                Files = files,
                PendingVerify = false,
            };
            this.Save(state);
            return state;
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 认领持久备份失败：{Name}", internalName);
            return null;
        }
    }

    /// <summary>把配置目录里的旧备份搬进持久目录（一次性迁移）。返回新路径；失败返回 null。</summary>
    public string? MoveBackupToOriginals(string internalName, string dllPath, string backupPath, string sourceHash)
    {
        var directory = this.OriginalsDirectoryOf(internalName);
        if (directory is null)
        {
            return null;
        }

        try
        {
            lock (this.gate)
            {
                Directory.CreateDirectory(directory);
                var target = Path.Combine(directory, DurableBackupName(dllPath));
                if (File.Exists(target)
                    && string.Equals(HashOf(target), sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    return target;
                }

                var temp = target + ".tmp";
                File.Copy(backupPath, temp, overwrite: true);
                if (!string.Equals(HashOf(temp), sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(temp);
                    return null;
                }

                File.Move(temp, target, overwrite: true);
                return target;
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 迁移备份到持久目录失败：{Name}", internalName);
            return null;
        }
    }

    /// <summary>还原干净后清掉持久目录里这份补丁的原始备份（DLL 已经回到原版，不需要它们了）。</summary>
    public void RemoveOriginals(UITextPatchState state)
    {
        var directory = this.OriginalsDirectoryOf(state.InternalName);
        if (directory is null)
        {
            return;
        }

        try
        {
            lock (this.gate)
            {
                foreach (var file in state.EffectiveFiles)
                {
                    if (this.IsDurableBackup(file.BackupPath))
                    {
                        TryDeleteFile(file.BackupPath!);
                    }
                }

                TryDeleteFile(Path.Combine(directory, "manifest.json"));
                if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                {
                    Directory.Delete(directory);
                }
            }
        }
        catch (Exception)
        {
            // 清理是尽力而为
        }
    }

    private string? OriginalsDirectoryOf(string internalName) =>
        this.originalsDirectory is null ? null : Path.Combine(this.originalsDirectory, Sanitize(internalName));

    /// <summary>持久目录里的原始备份；写不进去（权限 / 路径）返回 null，由调用方退回配置目录。</summary>
    private string? BackupToOriginals(string internalName, string dllPath, string sourceHash)
    {
        var directory = this.OriginalsDirectoryOf(internalName);
        if (directory is null)
        {
            return null;
        }

        try
        {
            lock (this.gate)
            {
                Directory.CreateDirectory(directory);
                var target = Path.Combine(directory, DurableBackupName(dllPath));
                if (File.Exists(target)
                    && string.Equals(HashOf(target), sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    return target;
                }

                var temp = target + ".tmp";
                File.Copy(dllPath, temp, overwrite: true);
                if (!string.Equals(HashOf(temp), sourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(temp);
                    return null;
                }

                File.Move(temp, target, overwrite: true);
                return target;
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 持久目录备份失败（退回配置目录）：{Path}", dllPath);
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // 删不掉不影响
        }
    }

    /// <summary>
    ///     清单里的文件名用「相对插件目录的路径」（本地化文件在子目录里，只存文件名认领不回来）；
    ///     跑出插件目录就退回文件名（旧清单里就是文件名，行为不变）。
    /// </summary>
    private static string RelativeName(string pluginDirectory, string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(pluginDirectory))
            {
                var relative = Path.GetRelativePath(pluginDirectory, path);
                if (!relative.StartsWith("..", StringComparison.Ordinal))
                {
                    return relative.Replace('\\', '/');
                }
            }
        }
        catch (Exception)
        {
            // 退回文件名
        }

        return Path.GetFileName(path);
    }

    /// <summary>
    ///     持久目录里备份文件的名字：源文件名 + 源路径的短哈希。
    ///     同一插件可能有**同名但不同目录**的文件（<c>zh-CN/A/x.json</c> 与 <c>zh-CN/B/x.json</c>），
    ///     只按文件名备份会让后写的顶掉先写的——还原时就会把 A 的内容写进 B。
    ///     路径不变时名字稳定：同一版本重打会复用已备份的原文，不会反复复制。
    /// </summary>
    private static string DurableBackupName(string sourcePath)
    {
        try
        {
            var full = Path.GetFullPath(sourcePath);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))).ToLowerInvariant();
            return Path.GetFileName(sourcePath) + "." + hash[..10] + ".orig";
        }
        catch (Exception)
        {
            return Path.GetFileName(sourcePath) + ".orig";
        }
    }

    /// <summary>
    ///     文件 SHA-256（算不出来返回空串）。
    /// </summary>
    public static string HashOf(string path) => UITextHash.OfFile(path);

    private string PathOf(string internalName) => Path.Combine(this.stateDirectory, Sanitize(internalName) + ".json");

    /// <summary>
    ///     备份文件名里区分「哪个文件」：主程序集不带（沿用老命名），伴生程序集带自己的名字，
    ///     否则多文件会互相覆盖（同一插件、不同文件、哈希不同倒不会撞，但撞上了就串了）。
    /// </summary>
    private static string BackupLabel(string internalName, string dllPath)
    {
        var stem = Path.GetFileNameWithoutExtension(dllPath);
        return string.Equals(stem, internalName, StringComparison.OrdinalIgnoreCase) ? string.Empty : Sanitize(stem) + "-";
    }

    private static string Sanitize(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
