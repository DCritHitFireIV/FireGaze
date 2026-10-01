using System.Security.Cryptography;
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
///     补丁状态的读写与备份管理（都在配置目录里，不动插件目录的其它文件）。
/// </summary>
internal sealed class UITextPatchStore
{
    private readonly string stateDirectory;
    private readonly string backupDirectory;
    private readonly object gate = new();

    public UITextPatchStore(string configDirectory)
    {
        this.stateDirectory = Path.Combine(configDirectory, "uitrans", "state");
        this.backupDirectory = Path.Combine(configDirectory, "uitrans", "backups");
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
    ///     写状态。
    /// </summary>
    public void Save(UITextPatchState state)
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
        }
        catch (Exception e)
        {
            Plugin.Log?.Warning(e, "[内部文本] 写补丁状态失败：{Path}", path);
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
    /// </summary>
    public string? Backup(string internalName, string dllPath, string sourceHash)
    {
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
    ///     文件 SHA-256（算不出来返回空串）。
    /// </summary>
    public static string HashOf(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

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
